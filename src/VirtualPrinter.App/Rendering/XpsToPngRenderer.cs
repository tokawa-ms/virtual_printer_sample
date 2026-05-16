using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;

namespace VirtualPrinter.App.Rendering;

/// <summary>
/// Reads an XPS package from a Stream and writes one PNG per FixedPage into
/// <see cref="App.OutputRoot"/>\\&lt;timestamp&gt;_&lt;job&gt;\\page_NNN.png.
/// </summary>
internal static class XpsToPngRenderer
{
    /// <summary>Render DPI for each PNG page. 300 dpi is good for OCR/inspection.</summary>
    public const double Dpi = 300.0;

    /// <summary>
    /// Renders the supplied XPS stream to PNG files. Must run on an STA thread
    /// because WPF visual APIs require it.
    /// </summary>
    public static Task RenderAsync(Stream xpsStream, string jobName)
    {
        var tcs = new TaskCompletionSource<bool>();
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                RenderCore(xpsStream, jobName);
                tcs.SetResult(true);
            }
            catch (Exception ex)
            {
                Logger.Error("XpsToPngRenderer.RenderCore failed.", ex);
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static void RenderCore(Stream xpsStream, string jobName)
    {
        // System.IO.Packaging requires a seekable stream backed by a Package.
        // The spool stream is usually forward-only, so copy to a temp file first.
        var tempXps = Path.Combine(Path.GetTempPath(),
            $"vprint_{Guid.NewGuid():N}.xps");
        try
        {
            using (var fs = File.Create(tempXps))
            {
                xpsStream.CopyTo(fs);
            }
            Logger.Info($"Spool copied to {tempXps} ({new FileInfo(tempXps).Length} bytes).");

            // The Microsoft XPS Class Driver writes OPC packages in
            // "interleaved" / "piece-streaming" form (entries like
            // "[Content_Types].xml/[0].piece"). System.IO.Packaging does not
            // reassemble those automatically, so we rewrite the archive with
            // concatenated parts before handing it to XpsDocument.
            if (ReassembleOpcPieces(tempXps))
            {
                Logger.Info("Reassembled OPC piece-streamed package.");
            }

            // The Microsoft XPS Class Driver (Windows 8+) typically writes
            // OpenXPS (.oxps) packages rather than legacy XPS. WPF's
            // XpsDocument only understands legacy XPS, so we normalize the
            // package in-place if necessary.
            if (NormalizeOpenXpsToXps(tempXps))
            {
                Logger.Info("Normalized OpenXPS package to legacy XPS.");
            }

            var jobFolder = Path.Combine(
                App.OutputRoot,
                $"{DateTime.Now:yyyyMMdd_HHmmss}_{Sanitize(jobName)}");
            Directory.CreateDirectory(jobFolder);
            Logger.Info($"Output folder: {jobFolder}");

            using var xps = new XpsDocument(tempXps, FileAccess.Read);
            var seq = xps.GetFixedDocumentSequence()
                ?? throw new InvalidOperationException("XPS has no fixed document sequence.");

            int pageIndex = 0;
            foreach (DocumentReference docRef in seq.References)
            {
                var doc = docRef.GetDocument(forceReload: false);
                foreach (var pageRef in doc.Pages)
                {
                    pageIndex++;
                    var fixedPage = pageRef.GetPageRoot(forceReload: false);
                    var pngPath = Path.Combine(jobFolder, $"page_{pageIndex:000}.png");
                    SavePageAsPng(fixedPage, pngPath);
                    Logger.Info($"Wrote {pngPath}");
                }
            }

            if (pageIndex == 0)
            {
                Logger.Error("No pages were found in the XPS document.");
            }
        }
        finally
        {
            try { if (File.Exists(tempXps)) File.Delete(tempXps); } catch { }
        }
    }

    private static void SavePageAsPng(FixedPage page, string pngPath)
    {
        // Determine pixel size at the target DPI. FixedPage size is in DIPs (1/96").
        double widthDip  = page.Width;
        double heightDip = page.Height;
        int widthPx  = (int)Math.Ceiling(widthDip  * Dpi / 96.0);
        int heightPx = (int)Math.Ceiling(heightDip * Dpi / 96.0);

        // Force layout at the page's natural size so all visuals are realized.
        page.Measure(new Size(widthDip, heightDip));
        page.Arrange(new Rect(new Size(widthDip, heightDip)));
        page.UpdateLayout();

        var rtb = new RenderTargetBitmap(
            widthPx, heightPx, Dpi, Dpi, PixelFormats.Pbgra32);

        // Wrap the page so background is white (XPS pages are transparent).
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, widthDip, heightDip));
            dc.DrawRectangle(new VisualBrush(page) { Stretch = Stretch.None },
                             null, new Rect(0, 0, widthDip, heightDip));
        }
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var output = File.Create(pngPath);
        encoder.Save(output);
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

    // ---- OPC piece-stream reassembly -------------------------------------
    //
    // OPC supports two physical layouts inside the ZIP package: simple
    // (one part = one zip entry) and *interleaved / piece-streamed*, where a
    // part is split across multiple zip entries named:
    //
    //   <PartName>/[<n>].piece           (intermediate pieces, n >= 0)
    //   <PartName>/[<n>].last.piece      (final piece)
    //
    // System.IO.Packaging.ZipPackage does not transparently reassemble those.
    // Without reassembly, XpsDocument cannot find [Content_Types].xml,
    // _rels/.rels, or the FixedDocumentSequence relationship, and reports
    // "no fixed document sequence". The Microsoft XPS Class Driver emits
    // this form when streaming to a Local Port.

    private static readonly Regex _pieceRegex =
        new(@"^(?<part>.+)/\[(?<n>\d+)\](?<last>\.last)?\.piece$",
            RegexOptions.Compiled);

    /// <summary>
    /// If <paramref name="path"/> uses OPC piece streaming, rewrite the archive
    /// so each part appears as a single zip entry. Returns true if any
    /// reassembly was performed.
    /// </summary>
    private static bool ReassembleOpcPieces(string path)
    {
        // First pass: detect piece entries and load everything into memory.
        var groups   = new Dictionary<string, SortedDictionary<int, byte[]>>(StringComparer.Ordinal);
        var others   = new List<(string Name, byte[] Data)>();
        var hasPiece = false;

        using (var src = ZipFile.OpenRead(path))
        {
            foreach (var entry in src.Entries)
            {
                var m = _pieceRegex.Match(entry.FullName);
                if (m.Success)
                {
                    hasPiece = true;
                    var part = m.Groups["part"].Value;
                    var idx  = int.Parse(m.Groups["n"].Value);
                    if (!groups.TryGetValue(part, out var dict))
                    {
                        dict = new SortedDictionary<int, byte[]>();
                        groups[part] = dict;
                    }
                    dict[idx] = ReadAll(entry);
                }
                else
                {
                    others.Add((entry.FullName, ReadAll(entry)));
                }
            }
        }
        if (!hasPiece) return false;

        // Second pass: write a fresh archive with concatenated parts.
        var tmpPath = path + ".reassembled";
        using (var fs  = File.Create(tmpPath))
        using (var dst = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in others)
            {
                var e = dst.CreateEntry(name, CompressionLevel.Fastest);
                using var s = e.Open();
                s.Write(data, 0, data.Length);
            }
            foreach (var kv in groups)
            {
                var e = dst.CreateEntry(kv.Key, CompressionLevel.Fastest);
                using var s = e.Open();
                foreach (var piece in kv.Value.Values)
                {
                    s.Write(piece, 0, piece.Length);
                }
            }
        }
        File.Move(tmpPath, path, overwrite: true);
        return true;
    }

    private static byte[] ReadAll(ZipArchiveEntry entry)
    {
        using var es = entry.Open();
        using var ms = new MemoryStream(checked((int)Math.Max(entry.Length, 0)));
        es.CopyTo(ms);
        return ms.ToArray();
    }

    // ---- OpenXPS → XPS normalization -------------------------------------

    // Substitutions applied to every text-bearing OPC part inside the package.
    // OpenXPS and XPS are structurally identical OPC packages; only the
    // namespace URIs, relationship type URIs, and MIME content types differ.
    private static readonly (string From, string To)[] _subs = new[]
    {
        // XML namespaces used inside FixedPage / FixedDocument / FDSeq XAML
        ("http://schemas.openxps.org/oxps/v1.0",
         "http://schemas.microsoft.com/xps/2005/06"),
        // Resource dictionary namespace
        ("http://schemas.openxps.org/oxps/v1.0/resourcedictionary-key",
         "http://schemas.microsoft.com/winfx/2006/xaml"),
        // Relationship type URIs in _rels/*.rels
        ("http://schemas.openxps.org/oxps/v1.0/fixedrepresentation",
         "http://schemas.microsoft.com/xps/2005/06/fixedrepresentation"),
        ("http://schemas.openxps.org/oxps/v1.0/required-resource",
         "http://schemas.microsoft.com/xps/2005/06/required-resource"),
        ("http://schemas.openxps.org/oxps/v1.0/printticket",
         "http://schemas.microsoft.com/xps/2005/06/printticket"),
        ("http://schemas.openxps.org/oxps/v1.0/restricted-font",
         "http://schemas.microsoft.com/xps/2005/06/restricted-font"),
        ("http://schemas.openxps.org/oxps/v1.0/discard-control",
         "http://schemas.microsoft.com/xps/2005/06/discard-control"),
        ("http://schemas.openxps.org/oxps/v1.0/storyfragments",
         "http://schemas.microsoft.com/xps/2005/06/storyfragments"),
        ("http://schemas.openxps.org/oxps/v1.0/signaturedefinitions",
         "http://schemas.microsoft.com/xps/2005/06/signaturedefinitions"),
        ("http://schemas.openxps.org/oxps/v1.0/coresignature",
         "http://schemas.microsoft.com/xps/2005/06/coresignature"),
        // MIME content-types in [Content_Types].xml
        ("application/vnd.ms-package.oxps-",
         "application/vnd.ms-package.xps-"),
        ("application/oxps",
         "application/vnd.ms-package.xps-fixeddocumentsequence+xml"),
    };

    private static readonly string[] _textPartSuffixes = new[]
    {
        ".xml", ".rels", ".fdseq", ".fdoc", ".fpage", ".dict", ".xaml"
    };

    /// <summary>
    /// If <paramref name="path"/> looks like an OpenXPS package, rewrite its
    /// content types, relationship types, and XAML namespaces in place so the
    /// legacy WPF <see cref="XpsDocument"/> API can consume it. Returns true
    /// if any modification was made.
    /// </summary>
    private static bool NormalizeOpenXpsToXps(string path)
    {
        // Quick sniff: does [Content_Types].xml or any rels file reference
        // an OpenXPS URI? If not, skip the rewrite entirely.
        bool isOpenXps = false;
        try
        {
            using var probe = ZipFile.OpenRead(path);
            foreach (var entry in probe.Entries)
            {
                if (!IsTextPart(entry.FullName)) continue;
                using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
                var text = sr.ReadToEnd();
                if (text.Contains("openxps.org") || text.Contains("application/oxps"))
                {
                    isOpenXps = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("OpenXPS sniff failed.", ex);
            return false;
        }
        if (!isOpenXps) return false;

        // Rewrite every text-bearing part in place.
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var entry in archive.Entries)
        {
            if (!IsTextPart(entry.FullName)) continue;

            string content;
            using (var sr = new StreamReader(entry.Open(), Encoding.UTF8))
            {
                content = sr.ReadToEnd();
            }

            string updated = content;
            foreach (var (from, to) in _subs)
            {
                updated = updated.Replace(from, to);
            }
            if (updated == content) continue;

            // Rewrite the part contents.
            using var ws = entry.Open();
            ws.SetLength(0);
            using var sw = new StreamWriter(ws, new UTF8Encoding(false));
            sw.Write(updated);
        }
        return true;
    }

    private static bool IsTextPart(string entryName)
    {
        foreach (var suffix in _textPartSuffixes)
        {
            if (entryName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        // [Content_Types].xml has no folder prefix
        return entryName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
    }
}
