namespace PdfAutoViewer.UI;

/// <summary>
/// Pop-up shown before a viewer window reaches its viewing-time limit.
///
/// Replaces the tray balloon tip: on locked-down terminals (e.g. Wyse thin
/// clients) Windows notifications are disabled, so balloons never appear.
/// A plain window always does.
///
///   • TopMost and owned by the viewer, so it stays visible above other apps
///     and closes automatically together with the viewer.
///   • Accessibility first: large text, high contrast and a very large live
///     countdown, laid out top to bottom so it reads at a glance:
///         [document name]
///         Will close in:
///         4:59
///   • Text follows the preferred language: Spanish when SPA is selected,
///     English otherwise (ENG or "Any").
/// </summary>
public sealed class ClosingWarningForm : Form
{
    private const int ContentWidth = 420;
    private const int Margin_      = 20;

    private readonly DateTime _closeAtUtc;
    private readonly Label _countdownLabel;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    public ClosingWarningForm(string documentName, DateTime closeAtUtc, bool spanish)
    {
        _closeAtUtc = closeAtUtc;

        Text            = "Philips Document Flow (PDF)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.CenterParent;
        BackColor       = Color.White;

        var dark = Color.FromArgb(26, 26, 46);

        var icon = new PictureBox
        {
            Image    = SystemIcons.Warning.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size     = new Size(40, 40),
        };

        // Document name as the reader knows it: no quotes, no ".pdf" extension.
        // Long names wrap onto several lines instead of being cut off.
        var nameLabel = new Label
        {
            Text        = Path.GetFileNameWithoutExtension(documentName),
            Font        = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor   = dark,
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth, 0),
            TextAlign   = ContentAlignment.MiddleCenter,
        };

        var closesInLabel = new Label
        {
            Text      = spanish ? "Se cerrará en:" : "Will close in:",
            Font      = new Font("Segoe UI", 15f),
            ForeColor = dark,
            AutoSize  = true,
        };

        // Fixed size so the window does not jitter as the digits change.
        _countdownLabel = new Label
        {
            Font      = new Font("Segoe UI", 48f, FontStyle.Bold),
            ForeColor = Color.FromArgb(192, 57, 43),
            AutoSize  = false,
            Size      = new Size(ContentWidth, 90),
            TextAlign = ContentAlignment.MiddleCenter,
        };

        var okButton = new Button
        {
            Text         = spanish ? "Aceptar" : "OK",
            Font         = new Font("Segoe UI", 12f, FontStyle.Bold),
            DialogResult = DialogResult.OK,
            Size         = new Size(160, 46),
        };
        okButton.Click += (_, _) => Close();
        AcceptButton = okButton;

        Controls.AddRange([icon, nameLabel, closesInLabel, _countdownLabel, okButton]);

        UpdateCountdown();
        StackCentered(icon, nameLabel, closesInLabel, _countdownLabel, okButton);

        _tick.Tick += (_, _) => UpdateCountdown();
        _tick.Start();
    }

    // Places the controls top to bottom, each centered horizontally, and sizes
    // the window to fit them.
    private void StackCentered(params Control[] controls)
    {
        int formWidth = ContentWidth + 2 * Margin_;
        int y = Margin_;

        foreach (var c in controls)
        {
            var size = c.AutoSize ? c.PreferredSize : c.Size;
            c.Location = new Point((formWidth - size.Width) / 2, y);
            y += size.Height + 10;
        }

        ClientSize = new Size(formWidth, y - 10 + Margin_);
    }

    private void UpdateCountdown()
    {
        var remaining = _closeAtUtc - DateTime.UtcNow;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        _countdownLabel.Text = $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tick.Dispose();
        base.Dispose(disposing);
    }
}
