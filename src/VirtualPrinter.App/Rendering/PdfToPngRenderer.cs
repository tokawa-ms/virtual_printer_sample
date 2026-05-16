using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using PDFtoImage;
using SkiaSharp;

namespace VirtualPrinter.App.Rendering;

/// <summary>
/// Result of one PDF ingest pass: the captured PDF plus the per-page PNGs.
/// </summary>
internal sealed record RenderResult(
    string JobFolder,
    string PdfPath,
    IReadOnlyList<string> PagePngPaths)
{
    public int PageCount => PagePngPaths.Count;
}

/// <summary>
/// Reads a PDF spool file written by the Microsoft Print To PDF driver and
/// writes one full-color PNG per page into
/// <see cref="App.OutputRoot"/>\\&lt;timestamp&gt;_&lt;job&gt;\\page_NNN.png.
///
/// PDFium (via the PDFtoImage NuGet) is used for rasterization, which fully
/// supports color output. This replaces the previous XPS-based pipeline:
/// Microsoft XPS Class Driver always forces grayscale spool data, so we
/// could never recover color from it. Microsoft Print To PDF, by contrast,
/// advertises PageOutputColor and emits a color PDF; PDFium then rasterizes
/// it preserving color.
/// </summary>
internal static class PdfToPngRenderer
{
    /// <summary>Render DPI for each PNG page. 300 dpi is good for OCR/inspection.</summary>
    public const int Dpi = 300;

    /// <summary>
    /// Renders <paramref name="pdfPath"/> to PNG files in a fresh job folder
    /// under <see cref="App.OutputRoot"/>, copying the original PDF alongside
    /// the pages as <c>print.pdf</c>. Returns metadata about the new job.
    /// </summary>
    public static Task<RenderResult> RenderAsync(string pdfPath, string jobName)
    {
        return Task.Run(() => RenderCore(pdfPath, jobName));
    }

    private static RenderResult RenderCore(string pdfPath, string jobName)
    {
        var jobFolder = Path.Combine(
            App.OutputRoot,
            $"{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitize(jobName)}");
        Directory.CreateDirectory(jobFolder);
        Logger.Info($"Output folder: {jobFolder}");

        // Keep the original PDF alongside the rendered pages for debugging
        // and re-rendering.
        var preservedPdf = Path.Combine(jobFolder, "print.pdf");
        File.Copy(pdfPath, preservedPdf, overwrite: true);

        var pngPaths = new List<string>();
        var options = new RenderOptions(Dpi: Dpi);

        using var pdfStream = File.OpenRead(preservedPdf);
        int pageIndex = 0;
        foreach (SKBitmap bitmap in Conversion.ToImages(
                     pdfStream,
                     leaveOpen: false,
                     password: null,
                     options: options))
        {
            using (bitmap)
            {
                pageIndex++;
                var pngPath = Path.Combine(jobFolder, $"page_{pageIndex:000}.png");
                using (var fs = File.Create(pngPath))
                using (var skStream = new SKManagedWStream(fs))
                {
                    if (!bitmap.Encode(skStream, SKEncodedImageFormat.Png, 100))
                    {
                        throw new InvalidOperationException(
                            $"Failed to PNG-encode page {pageIndex}.");
                    }
                }
                Logger.Info($"Wrote {pngPath} ({bitmap.Width}x{bitmap.Height})");
                pngPaths.Add(pngPath);
            }
        }

        if (pageIndex == 0)
        {
            Logger.Error("No pages were rendered from the PDF document.");
        }

        return new RenderResult(jobFolder, preservedPdf, pngPaths);
    }

    private static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "job";
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }
        return s.Trim().Length == 0 ? "job" : s.Trim();
    }
}
