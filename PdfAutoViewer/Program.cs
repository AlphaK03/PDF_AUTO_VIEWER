using PdfAutoViewer.Core;
using PdfAutoViewer.UI;

namespace PdfAutoViewer;

static class Program
{
    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfAutoViewer", "app-error.log");

    [STAThread]
    static void Main()
    {
        // Session-scoped single instance. Prevents a duplicate copy from running
        // within the SAME Windows session (e.g. autostart + a manual launch).
        // The "Local\" prefix keeps the mutex per-session ON PURPOSE: in VDI or
        // multi-session hosts each user session gets its own namespace, so every
        // operator's session runs its own independent instance and no one is
        // ever locked out. A crashed instance releases the name automatically.
        using var mutex = new Mutex(initiallyOwned: true,
            @"Local\PdfAutoViewer_SingleInstance", out bool isFirstInstance);

        if (!isFirstInstance)
            return; // another instance already owns this session

        // Global safety net for unattended 24/7 operation: record unhandled
        // exceptions instead of letting the app die silently. UI-thread
        // exceptions are caught and logged so a stray error does not bring the
        // whole tray application down.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Fatal", e.ExceptionObject as Exception);

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var startupManager = new StartupManager((kind, ex) => Log(kind, ex));
        startupManager.EnableStartup();

        // ApplicationContext keeps the app alive through the tray icon
        // without requiring a main window to stay open.
        Application.Run(new TrayApp());
    }

    private static void Log(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{kind}] {ex}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
