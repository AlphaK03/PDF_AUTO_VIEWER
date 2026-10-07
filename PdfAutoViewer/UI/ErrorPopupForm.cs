using PdfAutoViewer.Core;

namespace PdfAutoViewer.UI;

/// <summary>
/// Pop-up shown when a document could not be opened. Replaces the tray
/// balloon tip, which never appears on terminals with notifications disabled
/// (e.g. Wyse thin clients). Same visual style as <see cref="ClosingWarningForm"/>.
///
///   • One window at a time: further errors are appended to the open window
///     instead of stacking new ones (see <see cref="AddError"/>).
///   • Plain-language message for the operator, plus the technical detail in a
///     read-only box that "Copy error" puts on the clipboard for support.
///   • Text follows the preferred language: Spanish when SPA is selected,
///     English otherwise (ENG or "Any").
/// </summary>
public sealed class ErrorPopupForm : Form
{
    private const int ContentWidth = 420;
    private const int Margin_      = 20;
    private const int Gap          = 10;

    private static readonly Color Dark = Color.FromArgb(26, 26, 46);
    private static readonly Color Red  = Color.FromArgb(192, 57, 43);

    private readonly bool _spanish;
    private readonly List<PdfError> _errors = new();

    private readonly PictureBox _icon;
    private readonly Label _titleLabel;
    private readonly Label _documentLabel;
    private readonly Label _hintLabel;
    private readonly Label _detailCaption;
    private readonly TextBox _detailBox;
    private readonly Panel _buttonRow;
    private readonly Button _copyButton;

    public ErrorPopupForm(bool spanish)
    {
        _spanish = spanish;

        Text            = "Philips Document Flow (PDF)";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.CenterScreen;
        BackColor       = Color.White;

        _icon = new PictureBox
        {
            Image    = SystemIcons.Error.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size     = new Size(40, 40),
        };

        _titleLabel = new Label
        {
            Font        = new Font("Segoe UI", 15f, FontStyle.Bold),
            ForeColor   = Red,
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth, 0),
            TextAlign   = ContentAlignment.MiddleCenter,
        };

        _documentLabel = new Label
        {
            Font        = new Font("Segoe UI", 13f, FontStyle.Bold),
            ForeColor   = Dark,
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth, 0),
            TextAlign   = ContentAlignment.MiddleCenter,
        };

        _hintLabel = new Label
        {
            Font        = new Font("Segoe UI", 11f),
            ForeColor   = Dark,
            AutoSize    = true,
            MaximumSize = new Size(ContentWidth, 0),
            TextAlign   = ContentAlignment.MiddleCenter,
        };

        _detailCaption = new Label
        {
            Text      = spanish ? "Detalle del error:" : "Error details:",
            Font      = new Font("Segoe UI", 9f),
            ForeColor = Color.Gray,
            AutoSize  = true,
        };

        _detailBox = new TextBox
        {
            Multiline   = true,
            ReadOnly    = true,
            ScrollBars  = ScrollBars.Vertical,
            WordWrap    = true,
            Font        = new Font("Consolas", 9f),
            BackColor   = Color.FromArgb(245, 246, 248),
            ForeColor   = Dark,
            BorderStyle = BorderStyle.FixedSingle,
            Size        = new Size(ContentWidth, 90),
            TabStop     = false,
        };

        _copyButton = new Button
        {
            Text     = spanish ? "Copiar error" : "Copy error",
            Font     = new Font("Segoe UI", 12f),
            Size     = new Size(160, 46),
            Location = new Point(0, 0),
        };
        _copyButton.Click += (_, _) => CopyToClipboard();

        var okButton = new Button
        {
            Text         = spanish ? "Aceptar" : "OK",
            Font         = new Font("Segoe UI", 12f, FontStyle.Bold),
            DialogResult = DialogResult.OK,
            Size         = new Size(160, 46),
            Location     = new Point(160 + 16, 0),
        };
        okButton.Click += (_, _) => Close();
        AcceptButton = okButton;

        _buttonRow = new Panel { Size = new Size(160 * 2 + 16, 46) };
        _buttonRow.Controls.AddRange([_copyButton, okButton]);

        Controls.AddRange([_icon, _titleLabel, _documentLabel, _hintLabel,
                           _detailCaption, _detailBox, _buttonRow]);

        // Focus "OK" rather than the text box, so Enter closes the window.
        Shown += (_, _) => okButton.Focus();
    }

    /// <summary>
    /// Adds an error to the window. The first error fills it; later ones are
    /// summarized ("problems with N documents") and listed in the details.
    /// </summary>
    public void AddError(PdfError error)
    {
        _errors.Add(error);

        if (_errors.Count == 1)
        {
            _titleLabel.Text    = TitleFor(error.Kind);
            _documentLabel.Text = Path.GetFileNameWithoutExtension(error.DocumentName);
            _hintLabel.Text     = HintFor(error.Kind);
        }
        else
        {
            _titleLabel.Text = _spanish
                ? $"Hubo problemas con {_errors.Count} documentos"
                : $"There were problems with {_errors.Count} documents";

            _documentLabel.Text = string.Join(Environment.NewLine,
                _errors.Select(e => Path.GetFileNameWithoutExtension(e.DocumentName))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .Take(4))
                + (_errors.Count > 4 ? Environment.NewLine + "…" : "");

            _hintLabel.Text = _errors.Any(e => e.Kind == PdfErrorKind.ViewerFailed)
                ? HintFor(PdfErrorKind.ViewerFailed)
                : HintFor(PdfErrorKind.Unexpected);
        }

        _detailBox.Text = string.Join(Environment.NewLine + Environment.NewLine,
                                      _errors.Select(DescribeError));

        StackCentered(_icon, _titleLabel, _documentLabel, _hintLabel,
                      _detailCaption, _detailBox, _buttonRow);
    }

    // ── Texts ──────────────────────────────────────────────────────────────

    private string TitleFor(PdfErrorKind kind) => (kind, _spanish) switch
    {
        (PdfErrorKind.ViewerFailed, true)  => "No se pudo abrir el documento",
        (PdfErrorKind.ViewerFailed, false) => "The document could not be opened",
        (_, true)                          => "Ocurrió un error con el documento",
        (_, false)                         => "An error occurred with the document",
    };

    private string HintFor(PdfErrorKind kind) => (kind, _spanish) switch
    {
        (PdfErrorKind.ViewerFailed, true)  =>
            "El archivo se conservó en Descargas. Si el problema continúa, avise a soporte.",
        (PdfErrorKind.ViewerFailed, false) =>
            "The file was kept in Downloads. If the problem persists, contact support.",
        (_, true)  => "Si el problema continúa, avise a soporte.",
        (_, false) => "If the problem persists, contact support.",
    };

    private string DescribeError(PdfError e) =>
        $"[{e.Time:yyyy-MM-dd HH:mm:ss}] {e.DocumentName}{Environment.NewLine}" +
        $"{TitleFor(e.Kind)}{Environment.NewLine}" +
        e.Detail;

    // ── Copy ───────────────────────────────────────────────────────────────

    // Copies everything support needs: app version, computer, user and every
    // error with its technical detail. Briefly confirms on the button itself.
    private void CopyToClipboard()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;

        string text =
            $"Philips Document Flow (PDF) v{version}{Environment.NewLine}" +
            $"{(_spanish ? "Equipo" : "Computer")}: {Environment.MachineName}  ·  " +
            $"{(_spanish ? "Usuario" : "User")}: {Environment.UserName}{Environment.NewLine}" +
            Environment.NewLine +
            _detailBox.Text;

        string original = _spanish ? "Copiar error" : "Copy error";
        try
        {
            Clipboard.SetText(text);
            _copyButton.Text = _spanish ? "Copiado ✓" : "Copied ✓";
        }
        catch
        {
            // Clipboard briefly held by another app — let the user try again.
            _copyButton.Text = _spanish ? "Reintentar" : "Try again";
            return;
        }

        var reset = new System.Windows.Forms.Timer { Interval = 2000 };
        reset.Tick += (_, _) =>
        {
            reset.Dispose();
            if (!IsDisposed) _copyButton.Text = original;
        };
        reset.Start();
    }

    // ── Layout ─────────────────────────────────────────────────────────────

    // Places the controls top to bottom, each centered horizontally, and sizes
    // the window to fit them. Re-run whenever the texts change.
    private void StackCentered(params Control[] controls)
    {
        int formWidth = ContentWidth + 2 * Margin_;
        int y = Margin_;

        foreach (var c in controls)
        {
            var size = c.AutoSize ? c.PreferredSize : c.Size;
            int x = c == _detailCaption ? Margin_ : (formWidth - size.Width) / 2;
            c.Location = new Point(x, y);
            y += size.Height + (c == _detailCaption ? 2 : Gap);
        }

        ClientSize = new Size(formWidth, y - Gap + Margin_);
    }
}
