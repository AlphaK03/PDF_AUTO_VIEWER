using PdfAutoViewer.Core;

namespace PdfAutoViewer.UI;

/// <summary>
/// Small diagnostic window: what happened to each document this session
/// (downloaded, opened, not opened because a better version was open,
/// replaced, closed, deleted…). Opened from the "Log" button of the status
/// window; meant for testing and support evidence, not for operators.
///
/// Purely visual: it only reads the in-memory <see cref="ActivityLog"/>,
/// refreshing once per second and only when something changed.
/// </summary>
public sealed class ActivityLogForm : Form
{
    private readonly ActivityLog _log;
    private readonly ListView _list;
    private readonly Label _summary;
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 1000 };
    private long _shownVersion = -1;

    public ActivityLogForm(ActivityLog log)
    {
        _log = log;

        Text          = "Session activity — Philips Document Flow (PDF)";
        Icon          = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        ClientSize    = new Size(780, 340);
        MinimumSize   = new Size(480, 240);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor     = Color.FromArgb(240, 242, 245);
        Font          = new Font("Segoe UI", 9f);

        _list = new ListView
        {
            View          = View.Details,
            FullRowSelect = true,
            GridLines     = true,
            HeaderStyle   = ColumnHeaderStyle.Nonclickable,
            Dock          = DockStyle.Fill,
        };
        _list.Columns.Add("Time", 65);
        _list.Columns.Add("Event", 115);
        _list.Columns.Add("File", 270);
        _list.Columns.Add("Details", 310);

        _summary = new Label
        {
            AutoSize  = true,
            ForeColor = Color.FromArgb(26, 26, 46),
            Location  = new Point(10, 10),
        };

        var copyBtn = new Button
        {
            Text     = "Copy",
            Size     = new Size(70, 26),
            Anchor   = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
        };
        copyBtn.Click += (_, _) => CopyToClipboard(copyBtn);

        var clearBtn = new Button
        {
            Text     = "Clear",
            Size     = new Size(70, 26),
            Anchor   = AnchorStyles.Top | AnchorStyles.Right,
            FlatStyle = FlatStyle.Flat,
        };
        clearBtn.Click += (_, _) => _log.Clear();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        bottom.Controls.AddRange([_summary, copyBtn, clearBtn]);
        bottom.Resize += (_, _) =>
        {
            clearBtn.Location = new Point(bottom.Width - clearBtn.Width - 8, 5);
            copyBtn.Location  = new Point(clearBtn.Left - copyBtn.Width - 6, 5);
        };

        var hint = new Label
        {
            Text      = "Kept in memory only: the last 2 hours (max. 200 entries). Cleared when the app closes.",
            Dock      = DockStyle.Top,
            Height    = 20,
            ForeColor = Color.Gray,
            Padding   = new Padding(6, 3, 0, 0),
        };

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(bottom);

        _refresh.Tick += (_, _) => RefreshList();
        _refresh.Start();
        Shown += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        if (_log.Version == _shownVersion && _shownVersion >= 0)
            return;

        var entries = _log.Snapshot();
        _shownVersion = _log.Version;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in entries)
        {
            var item = new ListViewItem([e.Time.ToString("HH:mm:ss"), Label(e.Kind), e.File, e.Detail])
            {
                ForeColor = ColorFor(e.Kind),
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (_list.Items.Count > 0)
            _list.EnsureVisible(_list.Items.Count - 1);

        _summary.Text = Summarize(entries);
    }

    // "Open now: 2 · Opened: 3 · Not opened: 4 · Deleted: 5"
    private static string Summarize(List<ActivityEntry> entries)
    {
        var openNow = entries
            .Where(e => e.Kind is ActivityKind.Opened or ActivityKind.Replaced
                                or ActivityKind.ClosedByUser or ActivityKind.TimeLimit or ActivityKind.Error)
            .GroupBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .Count(g => g.Last().Kind == ActivityKind.Opened);

        int Count(ActivityKind k) => entries.Count(e => e.Kind == k);

        return $"Open now: {openNow}  ·  Opened: {Count(ActivityKind.Opened)}  ·  " +
               $"Not opened: {Count(ActivityKind.NotOpened)}  ·  Replaced: {Count(ActivityKind.Replaced)}  ·  " +
               $"Deleted: {Count(ActivityKind.Deleted)}";
    }

    private static string Label(ActivityKind kind) => kind switch
    {
        ActivityKind.Downloaded    => "Downloaded",
        ActivityKind.Opened        => "Opened",
        ActivityKind.NotOpened     => "Not opened",
        ActivityKind.Replaced      => "Replaced",
        ActivityKind.ClosedByUser  => "Closed by user",
        ActivityKind.TimeLimit     => "Closed (time limit)",
        ActivityKind.Deleted       => "Deleted",
        ActivityKind.DeletePending => "Delete pending",
        ActivityKind.Error         => "Error",
        _                          => kind.ToString(),
    };

    private static Color ColorFor(ActivityKind kind) => kind switch
    {
        ActivityKind.Opened        => Color.FromArgb(39, 130, 70),
        ActivityKind.NotOpened     => Color.Gray,
        ActivityKind.Replaced      => Color.FromArgb(200, 110, 0),
        ActivityKind.TimeLimit     => Color.FromArgb(120, 70, 160),
        ActivityKind.Deleted       => Color.FromArgb(90, 90, 90),
        ActivityKind.DeletePending => Color.FromArgb(200, 110, 0),
        ActivityKind.Error         => Color.FromArgb(192, 57, 43),
        _                          => Color.FromArgb(26, 26, 46),
    };

    private void CopyToClipboard(Button button)
    {
        var lines = _log.Snapshot()
            .Select(e => $"{e.Time:HH:mm:ss}\t{Label(e.Kind)}\t{e.File}\t{e.Detail}".TrimEnd());
        string text = _summary.Text + Environment.NewLine + string.Join(Environment.NewLine, lines);

        try
        {
            Clipboard.SetText(text);
            button.Text = "Copied ✓";
        }
        catch { button.Text = "Retry"; return; }

        var reset = new System.Windows.Forms.Timer { Interval = 1500 };
        reset.Tick += (_, _) => { reset.Dispose(); if (!IsDisposed) button.Text = "Copy"; };
        reset.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}
