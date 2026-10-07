namespace PdfAutoViewer.Core;

public enum ActivityKind
{
    Downloaded,     // file fully arrived in Downloads
    Opened,         // shown in a viewer window
    NotOpened,      // a better version of the same document was already open
    Replaced,       // its viewer was closed because a better/newer version arrived
    ClosedByUser,   // the user closed the viewer
    TimeLimit,      // closed by the 20-minute viewing limit
    Deleted,        // removed from Downloads
    DeletePending,  // locked by another process; retried in the background
    Error,          // the document could not be opened
}

public sealed record ActivityEntry(DateTime Time, string File, ActivityKind Kind, string Detail);

/// <summary>
/// What happened to each document during this session, for diagnosis and
/// test evidence (shown by the "Log" button of the status window).
///
/// In memory only — nothing is written to disk, and it is gone when the app
/// closes. It never accumulates, even if the app runs for days: entries
/// older than <see cref="MaxAge"/> expire, and at most <see cref="MaxEntries"/>
/// are kept. Thread-safe: written from the lifecycle threads, read by the UI.
/// </summary>
public sealed class ActivityLog
{
    public const int MaxEntries = 200;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(2);

    private readonly object _lock = new();
    private readonly LinkedList<ActivityEntry> _entries = new();
    private long _version;

    /// Increments on every change, so a viewer can refresh only when needed.
    public long Version => Interlocked.Read(ref _version);

    public void Add(string pdfPath, ActivityKind kind, string detail = "")
    {
        var entry = new ActivityEntry(DateTime.Now, Path.GetFileName(pdfPath), kind, detail);
        lock (_lock)
        {
            _entries.AddLast(entry);
            Prune();
        }
        Interlocked.Increment(ref _version);
    }

    public List<ActivityEntry> Snapshot()
    {
        lock (_lock)
        {
            if (Prune()) Interlocked.Increment(ref _version);
            return _entries.ToList();
        }
    }

    // Drops expired entries and keeps at most MaxEntries. Returns true if any
    // entry was removed. Caller holds the lock.
    private bool Prune()
    {
        bool removed = false;
        var cutoff = DateTime.Now - MaxAge;
        while (_entries.First is { } first && (first.Value.Time < cutoff || _entries.Count > MaxEntries))
        {
            _entries.RemoveFirst();
            removed = true;
        }
        return removed;
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Interlocked.Increment(ref _version);
    }
}
