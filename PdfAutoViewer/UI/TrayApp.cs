using PdfAutoViewer.Core;

namespace PdfAutoViewer.UI;

/// <summary>
/// Root application object. Owns the system tray icon and wires all components together.
///
/// Extends ApplicationContext so the app stays alive without a main window —
/// the tray icon IS the application.
/// </summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly AppSettings _settings;
    private readonly FolderMonitor _monitor;
    private readonly PdfLifecycleManager _pdfManager;
    private readonly NotifyIcon _tray;
    private StatusForm? _statusForm;

    public TrayApp()
    {
        _settings = AppSettings.Load();

        _tray = BuildTrayIcon();

        // Wire the folder monitor to the PDF lifecycle manager
        _pdfManager = new PdfLifecycleManager(_settings, OnPdfEvent);
        _monitor    = new FolderMonitor(path => _pdfManager.Schedule(path));
        _monitor.Start(_settings.EffectiveWatchFolder);

        _statusForm = new StatusForm(_settings);
        _statusForm.Show();

        // Warm up the built-in viewer's browser process so the first PDF
        // opens instantly instead of paying the WebView2 cold start.
        PdfViewerForm.Prewarm();
    }

    // ── Tray icon ──────────────────────────────────────────────────────────

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("● Philips Document Flow (PDF) active").Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Show window", null, (_, _) => ShowStatusForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit",        null, (_, _) => Quit());

        var icon = new NotifyIcon
        {
            Icon             = CreateTrayIcon(),
            Text             = "Philips Document Flow (PDF)",
            ContextMenuStrip = menu,
            Visible          = true,
        };

        icon.DoubleClick += (_, _) => ShowStatusForm();
        return icon;
    }

    /// Draws a blue icon with "PDF" text in memory — no external .ico file needed.
    private static Icon CreateTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g   = Graphics.FromImage(bmp);

        g.Clear(Color.Transparent);
        g.FillRectangle(new SolidBrush(Color.FromArgb(30, 90, 192)), 1, 1, 30, 30);

        using var font = new Font("Arial", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        var textSize = g.MeasureString("PDF", font);
        g.DrawString("PDF", font, Brushes.White,
            (32 - textSize.Width) / 2f,
            (32 - textSize.Height) / 2f);

        return Icon.FromHandle(bmp.GetHicon());
    }

    // ── Cross-thread event bridge ──────────────────────────────────────────

    private void OnPdfEvent(string type, string message)
    {
        // Worker threads call this — marshal to the UI thread before touching any controls
        if (_statusForm?.InvokeRequired == true)
        {
            _statusForm.BeginInvoke(() => HandleEvent(type, message));
            return;
        }
        HandleEvent(type, message);
    }

    private void HandleEvent(string type, string message)
    {
        // The 15-minute viewing-time warning is the only routine notification.
        if (type == PdfLifecycleManager.EventWarning)
        {
            _tray.ShowBalloonTip(8000, "Philips Document Flow (PDF)", message, ToolTipIcon.Warning);
            return;
        }

        // Errors are surfaced so that a failure to open is never silent.
        if (type == PdfLifecycleManager.EventError)
        {
            _tray.ShowBalloonTip(5000, "Error — Philips Document Flow (PDF)", message, ToolTipIcon.Error);
            return;
        }

        // Detected / opened / deleted events are intentionally silent.
    }

    // ── UI actions ─────────────────────────────────────────────────────────

    private void ShowStatusForm()
    {
        _statusForm ??= new StatusForm(_settings);
        _statusForm.Show();
        _statusForm.BringToFront();
        _statusForm.Activate();
    }

    private void Quit()
    {
        _monitor.Stop();
        _pdfManager.Dispose();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _monitor.Dispose();
            _pdfManager.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }
}
