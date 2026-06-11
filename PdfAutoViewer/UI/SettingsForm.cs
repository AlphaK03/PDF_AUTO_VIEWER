using PdfAutoViewer.Core;

namespace PdfAutoViewer.UI;

/// <summary>
/// Settings dialog. Returns DialogResult.OK when the user confirms.
/// Changes are written to disk inside OnOk before closing.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private CheckBox _autoDetectCb    = null!;
    private TextBox  _folderBox       = null!;
    private Button   _browseFolderBtn = null!;
    private CheckBox _autoDeleteCb    = null!;
    private CheckBox _notifyCb        = null!;
    private CheckBox _builtInViewerCb = null!;
    private ComboBox _langCombo       = null!;
    private TextBox  _edgeBox         = null!;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        BuildLayout();
        PopulateValues();
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    private void BuildLayout()
    {
        Text            = "PDF Auto Viewer — Settings";
        ClientSize      = new Size(490, 426);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.White;

        int y      = 16;
        const int x = 16;
        const int w = 458; // usable width

        // ── Watched folder ────────────────────────────────────────────
        AddSectionLabel("Watched Folder", x, ref y);

        _autoDetectCb = new CheckBox
        {
            Text     = "Auto-detect (reads Windows Registry — recommended for all PCs)",
            Location = new Point(x, y),
            Width    = w,
            AutoSize = false,
            Height   = 22,
        };
        _autoDetectCb.CheckedChanged += OnAutoDetectChanged;
        Controls.Add(_autoDetectCb);
        y += 26;

        _folderBox = new TextBox
        {
            Location = new Point(x, y),
            Width    = w - 90,
        };
        _browseFolderBtn = new Button
        {
            Text     = "Browse…",
            Location = new Point(x + w - 84, y - 1),
            Size     = new Size(84, 24),
        };
        _browseFolderBtn.Click += OnBrowseFolder;
        Controls.AddRange([_folderBox, _browseFolderBtn]);
        y += 34;

        // ── Behavior ──────────────────────────────────────────────────
        AddSectionLabel("Behavior", x, ref y);

        _autoDeleteCb = new CheckBox
        {
            Text     = "Delete PDF automatically after the user closes the Edge tab",
            Location = new Point(x, y),
            Width    = w,
            Height   = 22,
            AutoSize = false,
        };
        Controls.Add(_autoDeleteCb);
        y += 26;

        _notifyCb = new CheckBox
        {
            Text     = "Show tray balloon notifications (detected / opened / deleted)",
            Location = new Point(x, y),
            Width    = w,
            Height   = 22,
            AutoSize = false,
        };
        Controls.Add(_notifyCb);
        y += 26;

        _builtInViewerCb = new CheckBox
        {
            Text     = "Use built-in viewer (faster, exact close detection) instead of Edge",
            Location = new Point(x, y),
            Width    = w,
            Height   = 22,
            AutoSize = false,
        };
        Controls.Add(_builtInViewerCb);
        y += 34;

        // ── Document Language ─────────────────────────────────────────
        AddSectionLabel("Document Language", x, ref y);

        Controls.Add(new Label
        {
            Text     = "Preferred language when both _SPA and _ENG download simultaneously:",
            Location = new Point(x, y),
            Width    = w,
            Height   = 18,
            AutoSize = false,
        });
        y += 20;

        _langCombo = new ComboBox
        {
            Location      = new Point(x, y),
            Width         = 230,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _langCombo.Items.AddRange(new object[] { "Cualquiera (abrir ambos)", "Español (_SPA)", "English (_ENG)" });
        Controls.Add(_langCombo);
        y += 34;

        // ── Edge path ─────────────────────────────────────────────────
        AddSectionLabel("Microsoft Edge Executable", x, ref y);

        _edgeBox = new TextBox
        {
            Location        = new Point(x, y),
            Width           = w - 90,
            PlaceholderText = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        };
        var browseEdgeBtn = new Button
        {
            Text     = "Browse…",
            Location = new Point(x + w - 84, y - 1),
            Size     = new Size(84, 24),
        };
        browseEdgeBtn.Click += OnBrowseEdge;
        Controls.AddRange([_edgeBox, browseEdgeBtn]);
        y += 44;

        // ── OK / Cancel ───────────────────────────────────────────────
        var okBtn = new Button
        {
            Text         = "OK",
            Location     = new Point(ClientSize.Width - 188, y),
            Size         = new Size(84, 28),
            DialogResult = DialogResult.OK,
        };
        okBtn.Click += OnOk;

        var cancelBtn = new Button
        {
            Text         = "Cancel",
            Location     = new Point(ClientSize.Width - 96, y),
            Size         = new Size(80, 28),
            DialogResult = DialogResult.Cancel,
        };

        Controls.AddRange([okBtn, cancelBtn]);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    private void AddSectionLabel(string text, int x, ref int y)
    {
        Controls.Add(new Label
        {
            Text      = text,
            Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(50, 100, 160),
            AutoSize  = true,
            Location  = new Point(x, y),
        });
        y += 22;
    }

    // ── Populate and save ──────────────────────────────────────────────────

    private void PopulateValues()
    {
        _autoDetectCb.Checked = _settings.AutoDetectFolder;
        _folderBox.Text       = _settings.EffectiveWatchFolder;
        _autoDeleteCb.Checked    = _settings.AutoDelete;
        _notifyCb.Checked        = _settings.ShowNotifications;
        _builtInViewerCb.Checked = _settings.UseBuiltInViewer;
        _langCombo.SelectedIndex = _settings.PreferredLanguage switch
        {
            LanguagePreference.SPA => 1,
            LanguagePreference.ENG => 2,
            _                      => 0,
        };
        _edgeBox.Text         = _settings.EdgePath;

        // Sync folder field state with the checkbox
        OnAutoDetectChanged(null, EventArgs.Empty);
    }

    private void OnAutoDetectChanged(object? sender, EventArgs e)
    {
        bool auto = _autoDetectCb.Checked;
        _folderBox.Enabled       = !auto;
        _browseFolderBtn.Enabled = !auto;

        if (auto)
        {
            _folderBox.Text      = AppSettings.GetSystemDownloadsFolder();
            _folderBox.ForeColor = Color.Gray;
        }
        else
        {
            _folderBox.ForeColor = SystemColors.WindowText;
        }
    }

    private void OnOk(object? sender, EventArgs e)
    {
        _settings.AutoDetectFolder  = _autoDetectCb.Checked;
        _settings.WatchFolder       = _folderBox.Text.Trim();
        _settings.AutoDelete        = _autoDeleteCb.Checked;
        _settings.ShowNotifications = _notifyCb.Checked;
        _settings.UseBuiltInViewer  = _builtInViewerCb.Checked;
        _settings.PreferredLanguage = _langCombo.SelectedIndex switch
        {
            1 => LanguagePreference.SPA,
            2 => LanguagePreference.ENG,
            _ => LanguagePreference.Any,
        };
        _settings.EdgePath          = _edgeBox.Text.Trim();
        _settings.Save();
    }

    // ── File browser dialogs ───────────────────────────────────────────────

    private void OnBrowseFolder(object? sender, EventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description            = "Select the folder to watch for new PDFs",
            SelectedPath           = _folderBox.Text,
            UseDescriptionForTitle = true,
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _folderBox.Text = dlg.SelectedPath;
    }

    private void OnBrowseEdge(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title            = "Locate msedge.exe",
            Filter           = "Edge executable (msedge.exe)|msedge.exe|All executables (*.exe)|*.exe",
            InitialDirectory = File.Exists(_edgeBox.Text)
                ? Path.GetDirectoryName(_edgeBox.Text)
                : @"C:\",
        };
        if (dlg.ShowDialog() == DialogResult.OK)
            _edgeBox.Text = dlg.FileName;
    }
}
