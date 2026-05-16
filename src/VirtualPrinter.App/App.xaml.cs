using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace VirtualPrinter.App;

public partial class App : Application
{
    /// <summary>Top-level output directory for rendered PNGs and the log file.</summary>
    public const string OutputRoot = @"C:\VirtualPrintDemo";

    /// <summary>Set when --watch mode is active so OnExit can clean up.</summary>
    private Workflow.SpoolWatcher? _watcher;

    /// <summary>Keeps the watcher process alive without a window.</summary>
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Logger.Initialize(OutputRoot);
        Logger.Info($"App startup. Args=[{string.Join(' ', e.Args)}]");

        var args = e.Args;

        // --render <pdf-path>  : one-shot render of an existing PDF file
        if (args.Length >= 2 && string.Equals(args[0], "--render", StringComparison.OrdinalIgnoreCase))
        {
            RunOneShotRender(args[1]);
            return;
        }

        // --watch : run headless, monitor spool directory, never show UI
        if (args.Any(a => string.Equals(a, "--watch", StringComparison.OrdinalIgnoreCase)))
        {
            StartWatcherMode();
            return;
        }

        // Default: show the small management window
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void RunOneShotRender(string pdfPath)
    {
        try
        {
            Logger.Info($"One-shot render of '{pdfPath}'.");
            Rendering.PdfToPngRenderer.RenderAsync(pdfPath, "PrintJob")
                                      .GetAwaiter().GetResult();
            Logger.Info("One-shot render complete.");
        }
        catch (Exception ex)
        {
            Logger.Error("One-shot render failed.", ex);
        }
        finally
        {
            Shutdown(0);
        }
    }

    private void StartWatcherMode()
    {
        // Allow only one watcher instance for the current user.
        bool created;
        _singleInstanceMutex = new Mutex(true, "Global\\VirtualPrintDemo.Watcher", out created);
        if (!created)
        {
            Logger.Info("Another watcher instance is already running; exiting.");
            Shutdown(0);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _watcher = new Workflow.SpoolWatcher();
        _watcher.Start();
        Logger.Info("Watcher mode active. No window will be shown.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _watcher?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
