using System;
using System.IO;

namespace VirtualPrinter.App;

/// <summary>
/// Minimal append-only file logger. Writes to &lt;OutputRoot&gt;\\virtual-printer.log so
/// even background/foreground print activations can be diagnosed after the fact.
/// </summary>
internal static class Logger
{
    private static readonly object Sync = new();
    public static string LogFilePath { get; private set; } = string.Empty;

    public static void Initialize(string outputRoot)
    {
        try
        {
            Directory.CreateDirectory(outputRoot);
            LogFilePath = Path.Combine(outputRoot, "virtual-printer.log");
        }
        catch
        {
            // Fall back silently; logging is best-effort.
            LogFilePath = Path.Combine(Path.GetTempPath(), "virtual-printer.log");
        }
    }

    public static void Info(string message) => Write("INFO", message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERR ", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        if (string.IsNullOrEmpty(LogFilePath))
        {
            Initialize(App.OutputRoot);
        }
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (ex != null)
        {
            line += Environment.NewLine + ex;
        }
        try
        {
            lock (Sync)
            {
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Best-effort logging.
        }
    }
}
