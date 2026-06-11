using PdfAutoViewer.Core;

namespace PdfAutoViewer.UI;

/// <summary>
/// Floating status panel. Shows whether the download monitor is active
/// and which folder is being watched.
///
/// Closing the window (×) hides it instead of exiting the app.
/// Double-clicking the tray icon brings it back.
/// </summary>
public sealed class StatusForm : Form
{
    private readonly AppSettings _settings;
    private readonly Action _onOpenSettings;

    private Label _statusLabel = null!;
    private Label _folderLabel = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;

    public StatusForm(AppSettings settings, Action onOpenSettings)
    {
        _settings       = settings;
        _onOpenSettings = onOpenSettings;

        BuildLayout();

        // Refresh the displayed folder path every 2 seconds
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _refreshTimer.Tick += (_, _) => RefreshDisplay();
        _refreshTimer.Start();

        RefreshDisplay();
    }

    public void RefreshDisplay()
    {
        string folder = _settings.EffectiveWatchFolder;
        _statusLabel.Text = "● ACTIVE";
        _folderLabel.Text = folder.Length > 50 ? "…" + folder[^48..] : folder;
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        Text            = "PDF Auto Viewer";
        ClientSize      = new Size(390, 168);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.FromArgb(240, 242, 245);

        // ── Header ────────────────────────────────────────────────────
        var titleLabel = new Label
        {
            Text      = "PDF Auto Viewer",
            Font      = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 26, 46),
            AutoSize  = true,
            Location  = new Point(14, 14),
        };

        var versionLabel = new Label
        {
            Text      = "v1.0",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = Color.Silver,
            AutoSize  = true,
            Location  = new Point(352, 18),
        };

        var separator = new Panel
        {
            Location  = new Point(0, 44),
            Size      = new Size(390, 1),
            BackColor = Color.FromArgb(220, 225, 230),
        };

        // ── Status card ───────────────────────────────────────────────
        var card = new Panel
        {
            Location    = new Point(12, 54),
            Size        = new Size(366, 62),
            BackColor   = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var monitorLabel = new Label
        {
            Text      = "Download Monitor",
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 26, 46),
            AutoSize  = true,
            Location  = new Point(10, 8),
        };

        _statusLabel = new Label
        {
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(39, 174, 96),
            AutoSize  = true,
            Location  = new Point(260, 8),
        };

        _folderLabel = new Label
        {
            Font      = new Font("Segoe UI", 9f),
            ForeColor = Color.Gray,
            AutoSize  = true,
            Location  = new Point(10, 32),
        };

        card.Controls.AddRange([monitorLabel, _statusLabel, _folderLabel]);

        var separator2 = new Panel
        {
            Location  = new Point(0, 126),
            Size      = new Size(390, 1),
            BackColor = Color.FromArgb(220, 225, 230),
        };

        // ── Buttons ───────────────────────────────────────────────────
        var settingsBtn = MakeButton("⚙  Settings", new Point(12, 134));
        settingsBtn.Click += (_, _) => _onOpenSettings();

        var hideBtn = MakeButton("Hide  ↓", new Point(294, 134));
        hideBtn.Click += (_, _) => Hide();

        Controls.AddRange([titleLabel, versionLabel, separator, card, separator2, settingsBtn, hideBtn]);
    }

    private static Button MakeButton(string text, Point location) => new()
    {
        Text      = text,
        Location  = location,
        Size      = new Size(84, 28),
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(236, 240, 241),
        ForeColor = Color.FromArgb(60, 60, 60),
        Font      = new Font("Segoe UI", 9f),
    };

    // ── Overrides ──────────────────────────────────────────────────────────

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Hide instead of closing so the user can restore it from the tray
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer?.Dispose();
        base.Dispose(disposing);
    }
}
