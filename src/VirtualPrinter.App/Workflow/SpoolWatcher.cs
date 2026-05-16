using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualPrinter.App.Workflow;

/// <summary>
/// Watches the printer spool directory for new PDF files written by the
/// Microsoft Print To PDF driver to a local file port, and renders each one
/// to one PNG per page via <see cref="Rendering.PdfToPngRenderer"/>.
///
/// PDF was chosen over XPS because the Microsoft XPS Class Driver advertises
/// no PageOutputColor feature, so Chrome/Edge pre-rasterize jobs to grayscale
/// before spooling. Microsoft Print To PDF does advertise color, so we get
/// real color spool data which PDFium can rasterize into color PNGs.
/// </summary>
internal sealed class SpoolWatcher : IDisposable
{
    public static string SpoolDir  => Path.Combine(App.OutputRoot, ".spool");
    public static string SpoolFile => Path.Combine(SpoolDir, "spool.pdf");
    public static string FailedDir => Path.Combine(App.OutputRoot, ".failed");

    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SpoolWatcher()
    {
        Directory.CreateDirectory(SpoolDir);

        _watcher = new FileSystemWatcher(SpoolDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter       = "*.pdf",
            IncludeSubdirectories = false,
            EnableRaisingEvents   = false,
        };
        _watcher.Created += OnSpool;
        _watcher.Changed += OnSpool;
        _watcher.Renamed += OnSpool;
        _watcher.Error   += (s, e) => Logger.Error("SpoolWatcher error", e.GetException());
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
        Logger.Info($"SpoolWatcher started on '{SpoolDir}'.");

        // Process any file that may already be waiting at startup.
        try
        {
            foreach (var f in Directory.EnumerateFiles(SpoolDir, "*.pdf"))
            {
                _ = HandleAsync(f);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Initial scan failed.", ex);
        }
    }

    private async void OnSpool(object sender, FileSystemEventArgs e)
    {
        await HandleAsync(e.FullPath).ConfigureAwait(false);
    }

    private async Task HandleAsync(string path)
    {
        // Serialize handling so concurrent FS events don't race over the same file.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return;

            // Wait until the spooler is done writing (file becomes openable
            // for exclusive read, has a PDF header / EOF marker, and size is
            // stable for a few hundred ms).
            if (!await WaitForStableAsync(path).ConfigureAwait(false))
            {
                long size = -1;
                try { size = new FileInfo(path).Length; } catch { }
                Logger.Error($"File '{path}' never became a complete PDF "
                           + $"within the wait window (last size: {size}); deleting.");
                try { File.Delete(path); } catch { }
                return;
            }

            // Move the spool to a unique temp file so we don't block the
            // spooler from queuing the next job.
            var tempName = Path.Combine(SpoolDir,
                $"job_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.pdf.tmp");
            try
            {
                File.Move(path, tempName, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to move spool file to {tempName}.", ex);
                return;
            }

            Logger.Info($"Processing spool '{tempName}' ({new FileInfo(tempName).Length} bytes).");
            bool ok = false;
            try
            {
                var result = await Rendering.PdfToPngRenderer
                    .RenderAsync(tempName, "PrintJob")
                    .ConfigureAwait(false);
                Logger.Info($"Render completed: {result.PageCount} page(s) under {result.JobFolder}.");
                ok = true;
            }
            catch (Exception ex)
            {
                Logger.Error("Render failed.", ex);
            }
            finally
            {
                if (ok)
                {
                    try { File.Delete(tempName); } catch { }
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(FailedDir);
                        var preserved = Path.Combine(FailedDir,
                            $"failed_{DateTime.Now:yyyyMMdd_HHmmssfff}.pdf");
                        File.Move(tempName, preserved, overwrite: true);
                        Logger.Info($"Preserved failed spool to {preserved} for inspection.");
                    }
                    catch (Exception ex2)
                    {
                        Logger.Error("Failed to preserve failed spool.", ex2);
                        try { File.Delete(tempName); } catch { }
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Wait until the spooler has clearly finished writing the file. We require
    /// the file to look like a complete PDF — i.e.
    ///   * starts with "%PDF-"
    ///   * the last 2 KiB contains "%%EOF"
    ///   * size is unchanged for <paramref name="quietMs"/>
    ///   * file can be opened with FileShare.None (writer released the handle)
    /// </summary>
    private static async Task<bool> WaitForStableAsync(string path,
        int maxMs = 30000, int quietMs = 1500, int delayMs = 200)
    {
        long lastLen = -1;
        var lastChange = DateTime.UtcNow;
        var start = DateTime.UtcNow;

        while ((DateTime.UtcNow - start).TotalMilliseconds < maxMs)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return false;

                if (fi.Length != lastLen)
                {
                    lastLen = fi.Length;
                    lastChange = DateTime.UtcNow;
                }
                else if (fi.Length > 10
                      && (DateTime.UtcNow - lastChange).TotalMilliseconds >= quietMs)
                {
                    try
                    {
                        using var fs = File.Open(path, FileMode.Open,
                            FileAccess.Read, FileShare.None);
                        if (HasPdfHeader(fs) && HasPdfEof(fs))
                        {
                            return true;
                        }
                    }
                    catch (IOException)
                    {
                        // still locked, keep waiting
                    }
                }
            }
            catch (IOException)
            {
                // file still being written; retry
            }
            await Task.Delay(delayMs).ConfigureAwait(false);
        }
        return false;
    }

    private static bool HasPdfHeader(FileStream fs)
    {
        if (fs.Length < 5) return false;
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> hdr = stackalloc byte[5];
        int n = fs.Read(hdr);
        return n == 5 && hdr[0] == (byte)'%' && hdr[1] == (byte)'P'
                      && hdr[2] == (byte)'D' && hdr[3] == (byte)'F'
                      && hdr[4] == (byte)'-';
    }

    private static bool HasPdfEof(FileStream fs)
    {
        long len = fs.Length;
        if (len < 6) return false;
        int scan = (int)Math.Min(len, 2048);
        var buf = new byte[scan];
        fs.Seek(len - scan, SeekOrigin.Begin);
        int read = 0;
        while (read < scan)
        {
            int n = fs.Read(buf, read, scan - read);
            if (n <= 0) break;
            read += n;
        }
        for (int i = read - 5; i >= 0; i--)
        {
            if (buf[i]     == (byte)'%' && buf[i + 1] == (byte)'%'
             && buf[i + 2] == (byte)'E' && buf[i + 3] == (byte)'O'
             && buf[i + 4] == (byte)'F')
            {
                return true;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _gate.Dispose();
    }
}
