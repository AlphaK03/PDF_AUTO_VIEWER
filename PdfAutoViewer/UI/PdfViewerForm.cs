using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PdfAutoViewer.UI;

/// <summary>
/// Built-in PDF viewer: a plain window hosting WebView2 (the Chromium PDF
/// engine that ships with Windows 11). Compared to opening an Edge tab:
///   • Opens instantly — no browser startup, no tab juggling.
///   • Close detection is EXACT (the form's own close event), replacing the
///     window-title polling heuristic used for Edge tabs.
///   • Windows CAN be closed programmatically: when a better version of the
///     document arrives (see Core.DocumentCoordinator) this one is closed.
///
/// Reliability rules (a PDF must ALWAYS open):
///   • LoadFailed is set ONLY when WebView2 cannot initialize at all (runtime
///     missing/broken) or init times out — then the lifecycle keeps the file
///     and reports the error (there is NO Edge fallback by design).
///     Navigation success is NOT gated: rendering a local PDF with the runtime
///     present always works, and gating on it caused false failures.
///   • ONE shared CoreWebView2Environment for every window; a prewarmed hidden
///     instance keeps the browser process alive so each viewer opens fast.
///
/// Each viewer runs on its own STA thread with its own message loop, so the
/// lifecycle manager's background task simply blocks until the window closes.
/// </summary>
public sealed class PdfViewerForm : Form
{
    private const int InitTimeoutMs = 20000;

    // Maximum time a document may stay open, and when to warn the user first.
    private const int WarnAfterMs  = 15 * 60 * 1000; // 15 minutes
    private const int CloseAfterMs = 20 * 60 * 1000; // 20 minutes (hard limit)

    private readonly string _pdfPath;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    // Pop-ups in Spanish when SPA is the preferred language; English otherwise.
    private readonly bool _spanish;

    // Cancelled to close the window (better version arrived, or app exit).
    private readonly CancellationToken _closeToken;

    private System.Windows.Forms.Timer? _warnTimer;
    private System.Windows.Forms.Timer? _closeTimer;

    /// True if WebView2 failed to initialize; the lifecycle then keeps the file
    /// and reports the error (no Edge fallback by design).
    public bool LoadFailed { get; private set; }

    /// True if the window was closed by the 20-minute viewing limit.
    public bool ClosedByTimeLimit { get; private set; }

    /// Human-readable reason for a failed init (null when it worked).
    public string? InitError { get; private set; }

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfAutoViewer", "viewer-error.log");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // ── Environment + warm process ────────────────────────────────────────

    private static int _prewarmStarted;

    private static readonly string UserDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfAutoViewer", "WebView2");

    // IMPORTANT: WebView2 environments have COM thread affinity — an
    // environment must be created on the SAME UI thread that will use it to
    // create the WebView2 control. So we create a fresh environment per window
    // on that window's own STA thread. The shared user-data folder keeps the
    // underlying browser process warm, so secondary windows still open fast.
    private static Task<CoreWebView2Environment> CreateEnvironmentAsync() =>
        CoreWebView2Environment.CreateAsync(userDataFolder: UserDataDir);

    /// <summary>
    /// Starts the shared browser process in the background and keeps it warm
    /// for the lifetime of the app, so the first (and every) viewer window
    /// opens without the multi-second WebView2 cold start. Safe to call
    /// multiple times; only the first call does anything.
    /// </summary>
    public static void Prewarm()
    {
        if (Interlocked.Exchange(ref _prewarmStarted, 1) == 1)
            return;

        var thread = new Thread(() =>
        {
            try
            {
                // Invisible off-screen keeper window. Its WebView2 instance
                // holds the browser process alive between documents.
                var keeper = new Form
                {
                    ShowInTaskbar   = false,
                    Opacity         = 0,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition   = FormStartPosition.Manual,
                    Location        = new Point(-32000, -32000),
                    Size            = new Size(1, 1),
                };

                var warm = new WebView2();
                keeper.Controls.Add(warm);

                keeper.Load += async (_, _) =>
                {
                    try
                    {
                        var env = await CreateEnvironmentAsync();
                        await warm.EnsureCoreWebView2Async(env);
                    }
                    catch (Exception ex) { Log($"Prewarm failed → {ex.GetType().Name}: {ex.Message}"); }
                };

                Application.Run(keeper); // lives until the app exits
            }
            catch { }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _warnTimer?.Dispose();
        _closeTimer?.Dispose();
        base.OnFormClosed(e);
    }

    // ── Viewer window ──────────────────────────────────────────────────────

    private PdfViewerForm(string pdfPath, bool spanish, CancellationToken closeToken)
    {
        _pdfPath    = pdfPath;
        _spanish    = spanish;
        _closeToken = closeToken;

        Text          = Path.GetFileName(pdfPath);
        ClientSize    = new Size(1100, 800);
        StartPosition = FormStartPosition.CenterScreen;
        WindowState   = FormWindowState.Maximized;

        Controls.Add(_webView);

        Load  += async (_, _) => await InitAsync();
        Shown += (_, _) =>
        {
            // Steal focus reliably even though we were launched from a
            // background task: a TopMost pulse brings the window forward.
            Activate();
            TopMost = true;
            TopMost = false;
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var init = InitCoreAsync();

            if (await Task.WhenAny(init, Task.Delay(InitTimeoutMs)) != init)
                throw new TimeoutException("WebView2 did not initialize in time");

            await init; // propagate any initialization failure

            // Told to close while starting up (a better version arrived, or the
            // close request came before the window had a handle to receive it).
            if (_closeToken.IsCancellationRequested)
            {
                if (!IsDisposed) Close();
                return;
            }

            // Start the 20-minute viewing limit (with a warning at 15 minutes).
            StartViewingLimit();
        }
        catch (Exception ex)
        {
            // Closed on purpose mid-start: not a viewer failure, report nothing.
            if (_closeToken.IsCancellationRequested || IsDisposed)
                return;

            InitError  = $"{ex.GetType().Name}: {ex.Message}";
            LoadFailed = true;
            Log($"InitAsync failed for '{Path.GetFileName(_pdfPath)}' → {InitError}");
            Close();
        }
    }

    // Enforces the maximum viewing time. At 15 minutes the user is warned with
    // a pop-up window (not a tray balloon: notifications are disabled on the
    // Wyse terminals); at 20 minutes the window closes by itself, after which
    // the lifecycle deletes the document as usual.
    private void StartViewingLimit()
    {
        DateTime closeAtUtc = DateTime.UtcNow.AddMilliseconds(CloseAfterMs);

        _warnTimer = new System.Windows.Forms.Timer { Interval = WarnAfterMs };
        _warnTimer.Tick += (_, _) =>
        {
            _warnTimer!.Stop();

            // Owned by this viewer: stays above it and closes together with it.
            var popup = new ClosingWarningForm(Path.GetFileName(_pdfPath), closeAtUtc, _spanish);
            popup.FormClosed += (_, _) => popup.Dispose();
            popup.Show(this);
        };
        _warnTimer.Start();

        _closeTimer = new System.Windows.Forms.Timer { Interval = CloseAfterMs };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer!.Stop();
            ClosedByTimeLimit = true;
            Close();
        };
        _closeTimer.Start();
    }

    private async Task InitCoreAsync()
    {
        // Created on THIS window's own STA thread (see CreateEnvironmentAsync).
        var env = await CreateEnvironmentAsync();
        await _webView.EnsureCoreWebView2Async(env);

        // No navigation-success gating: with the runtime present, navigating
        // to a local PDF always renders. Gating here caused false fallbacks.
        _webView.CoreWebView2.Navigate(new Uri(_pdfPath).AbsoluteUri);
    }

    /// <summary>
    /// Shows the viewer and blocks the calling (background) thread until the
    /// window is closed — by the user, by <paramref name="ct"/> (a better
    /// version replaced it, or the app exits), or by the 20-minute limit.
    /// Returns null on success, or an error message if the viewer could not
    /// start (the lifecycle then keeps the file; there is no Edge fallback).
    /// <paramref name="spanish"/> selects the language of the pop-ups.
    /// <paramref name="closedByTimeLimit"/> tells whether the 20-minute limit closed it.
    /// </summary>
    public static string? ShowAndWait(string pdfPath, CancellationToken ct, bool spanish,
                                      out bool closedByTimeLimit)
    {
        closedByTimeLimit = false;

        // Already replaced before it could even open: nothing to show.
        if (ct.IsCancellationRequested)
            return null;

        string? error = null;
        bool timedOut = false;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new PdfViewerForm(pdfPath, spanish, ct);

                // If this fires before the window has a handle, BeginInvoke
                // throws and is ignored: InitAsync checks the token once the
                // window is up and closes it then.
                using var reg  = ct.Register(() =>
                {
                    try { form.BeginInvoke(new Action(form.Close)); } catch { }
                });

                Application.Run(form);
                timedOut = form.ClosedByTimeLimit;
                if (form.LoadFailed)
                    error = form.InitError ?? "unknown viewer error";
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                Log($"Viewer thread crashed for '{Path.GetFileName(pdfPath)}' → {error}");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join(); // block until the viewer window closes

        closedByTimeLimit = timedOut;
        return error; // null = success
    }
}
