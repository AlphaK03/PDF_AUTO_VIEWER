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
    private ErrorPopupForm? _errorPopup;

    public TrayApp()
    {
        _settings = AppSettings.Load();

        _tray = BuildTrayIcon();

        // Wire the folder monitor to the PDF lifecycle manager
        _pdfManager = new PdfLifecycleManager(_settings, OnPdfEvent, OnPdfError);
        _monitor    = new FolderMonitor(path => _pdfManager.Schedule(path));
        _monitor.Start(_settings.EffectiveWatchFolder);

        _statusForm = new StatusForm(_settings, _pdfManager.Activity);
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

    // Detected / opened / deleted events are intentionally silent. The
    // 15-minute viewing-time warning is a pop-up raised by the viewer itself
    // (see ClosingWarningForm). No tray balloons are used: notifications are
    // disabled on the Wyse terminals.
    private static void OnPdfEvent(string type, string message) { }

    private void OnPdfError(PdfError error)
    {
        // Worker threads call this — marshal to the UI thread before touching any controls
        if (_statusForm?.InvokeRequired == true)
        {
            _statusForm.BeginInvoke(() => ShowError(error));
            return;
        }
        ShowError(error);
    }

    // A document did not open: show it in a pop-up so the failure is never
    // silent. Only one error window at a time — further errors are added to it.
    private void ShowError(PdfError error)
    {
        if (_errorPopup is null || _errorPopup.IsDisposed)
        {
            _errorPopup = new ErrorPopupForm(_settings.PreferredLanguage == LanguagePreference.SPA);
            _errorPopup.FormClosed += (_, _) => { _errorPopup?.Dispose(); _errorPopup = null; };
            _errorPopup.AddError(error);
            _errorPopup.Show();
        }
        else
        {
            _errorPopup.AddError(error);
        }

        _errorPopup.Activate();
    }

    // ── UI actions ─────────────────────────────────────────────────────────

    private void ShowStatusForm()
    {
        _statusForm ??= new StatusForm(_settings, _pdfManager.Activity);
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
