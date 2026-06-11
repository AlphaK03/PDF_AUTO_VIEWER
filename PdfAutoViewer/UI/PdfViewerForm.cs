using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace PdfAutoViewer.UI;

/// <summary>
/// Built-in PDF viewer: a plain window hosting WebView2 (the Chromium PDF
/// engine that ships with Windows 11). Compared to opening an Edge tab:
///   • Opens instantly — no browser startup, no tab juggling.
///   • Close detection is EXACT (the form's own close event), replacing the
///     window-title polling heuristic used for Edge tabs.
///   • Windows CAN be closed programmatically, so the language filter is done
///     by opening every PDF and then closing the non-matching sibling.
///
/// Reliability rules (a PDF must ALWAYS open):
///   • LoadFailed is set ONLY when WebView2 cannot initialize at all (runtime
///     missing/broken) or init times out — then the caller falls back to Edge.
///     Navigation success is NOT gated: rendering a local PDF with the runtime
///     present always works, and gating on it caused false fallbacks.
///   • ONE shared CoreWebView2Environment for every window; a prewarmed hidden
///     instance keeps the browser process alive so each viewer opens fast.
///
/// Each viewer runs on its own STA thread with its own message loop, so the
/// lifecycle manager's background task simply blocks until the window closes —
/// mirroring EdgeManager.OpenAndWaitForClose.
/// </summary>
public sealed class PdfViewerForm : Form
{
    private const int InitTimeoutMs = 20000;

    private readonly string _pdfPath;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    // Language coordination data (empty when no filtering applies)
    private readonly string _pairingKey;
    private readonly string _language;   // "SPA" / "ENG" / ""
    private readonly string _preferred;  // "SPA" / "ENG" / "" (empty = no preference)

    /// True if WebView2 failed to initialize; the caller falls back to Edge.
    public bool LoadFailed { get; private set; }

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

    // ── Language reconciliation across viewer windows ─────────────────────

    private static readonly object CoordLock = new();
    private static readonly List<PdfViewerForm> OpenViewers = new();

    // Once a window is ready, register it and close the non-matching sibling:
    //   • If THIS is the preferred language → close any non-preferred sibling
    //     of the same document that is already open.
    //   • If THIS is non-preferred and a preferred sibling is already open →
    //     close myself.
    // The rule is symmetric, so it works regardless of which downloads first,
    // and never needs a timing window. A solo download stays open (no sibling).
    private void RegisterAndReconcile()
    {
        if (_preferred.Length == 0 || _language.Length == 0)
            return; // no filtering needed (no preference, or untagged document)

        var toClose = new List<PdfViewerForm>();

        lock (CoordLock)
        {
            OpenViewers.Add(this);

            bool iAmPreferred =
                _language.Equals(_preferred, StringComparison.OrdinalIgnoreCase);

            if (iAmPreferred)
            {
                foreach (var other in OpenViewers)
                    if (!ReferenceEquals(other, this)
                        && other._pairingKey == _pairingKey
                        && !other._language.Equals(_preferred, StringComparison.OrdinalIgnoreCase))
                        toClose.Add(other);
            }
            else if (OpenViewers.Any(o =>
                         !ReferenceEquals(o, this)
                         && o._pairingKey == _pairingKey
                         && o._language.Equals(_preferred, StringComparison.OrdinalIgnoreCase)))
            {
                toClose.Add(this);
            }
        }

        foreach (var f in toClose)
            try { f.BeginInvoke(new Action(f.Close)); } catch { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        lock (CoordLock) OpenViewers.Remove(this);
        base.OnFormClosed(e);
    }

    // ── Viewer window ──────────────────────────────────────────────────────

    private PdfViewerForm(string pdfPath, string pairingKey, string language, string preferred)
    {
        _pdfPath    = pdfPath;
        _pairingKey = pairingKey;
        _language   = language;
        _preferred  = preferred;

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

            // Window is functional — apply the language filter now.
            RegisterAndReconcile();
        }
        catch (Exception ex)
        {
            InitError  = $"{ex.GetType().Name}: {ex.Message}";
            LoadFailed = true;
            Log($"InitAsync failed for '{Path.GetFileName(_pdfPath)}' → {InitError}");
            Close();
        }
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
    /// window is closed — either by the user or by the language reconciler.
    /// Returns false only if the viewer could not start, so the caller falls
    /// back to Edge. <paramref name="preferred"/> empty = no language filter.
    /// </summary>
    public static string? ShowAndWait(
        string pdfPath, CancellationToken ct,
        string pairingKey, string language, string preferred)
    {
        string? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new PdfViewerForm(pdfPath, pairingKey, language, preferred);
                using var reg  = ct.Register(() =>
                {
                    try { form.BeginInvoke(new Action(form.Close)); } catch { }
                });

                Application.Run(form);
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

        return error; // null = success
    }
}
