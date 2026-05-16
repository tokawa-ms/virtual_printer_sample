using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VirtualPrinter.App.Workflow;

/// <summary>
/// Watches the printer spool directory for new XPS files written by the
/// Microsoft XPS Class Driver to a local file port, and renders each one
/// to PNG via <see cref="Rendering.XpsToPngRenderer"/>.
/// </summary>
internal sealed class SpoolWatcher : IDisposable
{
    public static string SpoolDir  => Path.Combine(App.OutputRoot, ".spool");
    public static string SpoolFile => Path.Combine(SpoolDir, "spool.xps");
    public static string FailedDir => Path.Combine(App.OutputRoot, ".failed");

    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SpoolWatcher()
    {
        Directory.CreateDirectory(SpoolDir);

        _watcher = new FileSystemWatcher(SpoolDir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter       = "*.xps",
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
            foreach (var f in Directory.EnumerateFiles(SpoolDir, "*.xps"))
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
            // for exclusive read and size is stable for a few hundred ms).
            if (!await WaitForStableAsync(path).ConfigureAwait(false))
            {
                long size = -1;
                try { size = new FileInfo(path).Length; } catch { }
                Logger.Error($"File '{path}' never became a complete XPS package "
                           + $"within the wait window (last size: {size}); deleting.");
                try { File.Delete(path); } catch { }
                return;
            }

            // Move the spool to a unique temp file so we don't block the
            // spooler from queuing the next job.
            var tempName = Path.Combine(SpoolDir,
                $"job_{DateTime.Now:yyyyMMdd_HHmmssfff}_{Guid.NewGuid():N}.xps.tmp");
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
                using (var fs = File.OpenRead(tempName))
                {
                    await Rendering.XpsToPngRenderer.RenderAsync(fs, "PrintJob")
                                                    .ConfigureAwait(false);
                }
                Logger.Info("Render completed.");
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
                            $"failed_{DateTime.Now:yyyyMMdd_HHmmssfff}.xps");
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
    /// the file to look like a valid ZIP (XPS is OPC = ZIP) — i.e.
    ///   * starts with the ZIP local-file-header signature (PK\x03\x04)
    ///   * the last 22+ bytes contain the End-Of-Central-Directory signature
    ///   * size is unchanged for <paramref name="quietMs"/>
    ///   * file can be opened with FileShare.None (writer released the handle)
    ///
    /// The Microsoft XPS Class Driver Local Port pipeline sometimes writes a
    /// tiny prelude (e.g. 2 bytes of CRLF) before the real XPS spool, so we
    /// MUST NOT accept anything that isn't a complete ZIP.
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
                else if (fi.Length > 22
                      && (DateTime.UtcNow - lastChange).TotalMilliseconds >= quietMs)
                {
                    try
                    {
                        using var fs = File.Open(path, FileMode.Open,
                            FileAccess.Read, FileShare.None);
                        if (HasZipLocalHeader(fs) && HasZipEocd(fs))
                        {
                            return true;
                        }
                        // Not a complete ZIP yet — the spooler may overwrite
                        // this file shortly with the real XPS payload.
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

    private static bool HasZipLocalHeader(FileStream fs)
    {
        if (fs.Length < 4) return false;
        fs.Seek(0, SeekOrigin.Begin);
        Span<byte> hdr = stackalloc byte[4];
        int n = fs.Read(hdr);
        return n == 4 && hdr[0] == 0x50 && hdr[1] == 0x4B
                      && hdr[2] == 0x03 && hdr[3] == 0x04;
    }

    /// <summary>
    /// Scan up to the last 64 KiB for the ZIP End-Of-Central-Directory record
    /// signature (0x06054b50). The EOCD lives within the last 22 + 65535 bytes
    /// of any ZIP file, so 64 KiB is enough in practice.
    /// </summary>
    private static bool HasZipEocd(FileStream fs)
    {
        const int Sig = 0x06054b50;
        long len = fs.Length;
        if (len < 22) return false;
        int scan = (int)Math.Min(len, 64 * 1024);
        var buf = new byte[scan];
        fs.Seek(len - scan, SeekOrigin.Begin);
        int read = 0;
        while (read < scan)
        {
            int n = fs.Read(buf, read, scan - read);
            if (n <= 0) break;
            read += n;
        }
        for (int i = read - 4; i >= 0; i--)
        {
            int v = buf[i] | (buf[i + 1] << 8) | (buf[i + 2] << 16) | (buf[i + 3] << 24);
            if (v == Sig) return true;
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
