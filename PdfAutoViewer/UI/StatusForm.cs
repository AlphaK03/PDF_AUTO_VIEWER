using PdfAutoViewer.Core;

namespace PdfAutoViewer.UI;

/// <summary>
/// Main window of the application. Shows whether the download monitor is
/// active and which folder is being watched, and lets the user choose the
/// preferred document language — the only user-adjustable setting.
///
/// Closing the window (×) hides it instead of exiting the app.
/// Double-clicking the tray icon brings it back.
/// </summary>
public sealed class StatusForm : Form
{
    private readonly AppSettings _settings;

    private Label _statusLabel = null!;
    private Label _folderLabel = null!;
    private ComboBox _langCombo = null!;
    private System.Windows.Forms.Timer _refreshTimer = null!;

    public StatusForm(AppSettings settings)
    {
        _settings = settings;

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
        _statusLabel.Text = "● ACTIVO";
        _folderLabel.Text = folder.Length > 50 ? "…" + folder[^48..] : folder;
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        Text            = "PDF Auto Viewer";
        ClientSize      = new Size(390, 232);
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
            Text      = "v2.0",
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
            Text      = "Monitor de descargas",
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
            Location  = new Point(258, 8),
        };

        _folderLabel = new Label
        {
            Font      = new Font("Segoe UI", 9f),
            ForeColor = Color.Gray,
            AutoSize  = true,
            Location  = new Point(10, 32),
        };

        card.Controls.AddRange([monitorLabel, _statusLabel, _folderLabel]);

        // ── Language selector ─────────────────────────────────────────
        var langTitle = new Label
        {
            Text      = "Idioma preferido",
            Font      = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = Color.FromArgb(26, 26, 46),
            AutoSize  = true,
            Location  = new Point(14, 128),
        };

        _langCombo = new ComboBox
        {
            Location      = new Point(14, 152),
            Width         = 250,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font          = new Font("Segoe UI", 9f),
        };
        _langCombo.Items.AddRange(["Cualquiera (abrir ambos)", "Español (_SPA)", "English (_ENG)"]);
        _langCombo.SelectedIndex = _settings.PreferredLanguage switch
        {
            LanguagePreference.SPA => 1,
            LanguagePreference.ENG => 2,
            _                      => 0,
        };
        _langCombo.SelectedIndexChanged += OnLanguageChanged;

        var hint = new Label
        {
            Text      = "Si se descargan ambos idiomas a la vez, se abre el preferido.",
            Font      = new Font("Segoe UI", 8f),
            ForeColor = Color.Gray,
            AutoSize  = true,
            Location  = new Point(14, 180),
        };

        var separator2 = new Panel
        {
            Location  = new Point(0, 198),
            Size      = new Size(390, 1),
            BackColor = Color.FromArgb(220, 225, 230),
        };

        // ── Hide button ───────────────────────────────────────────────
        var hideBtn = new Button
        {
            Text      = "Ocultar  ↓",
            Location  = new Point(294, 204),
            Size      = new Size(84, 24),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(236, 240, 241),
            ForeColor = Color.FromArgb(60, 60, 60),
            Font      = new Font("Segoe UI", 9f),
        };
        hideBtn.Click += (_, _) => Hide();

        Controls.AddRange([titleLabel, versionLabel, separator, card,
                           langTitle, _langCombo, hint, separator2, hideBtn]);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _settings.PreferredLanguage = _langCombo.SelectedIndex switch
        {
            1 => LanguagePreference.SPA,
            2 => LanguagePreference.ENG,
            _ => LanguagePreference.Any,
        };
        _settings.Save();
    }

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
