namespace PdfAutoViewer.Core;

/// <summary>
/// Decides which version of each document is shown. Only ONE version of a
/// document is on screen at a time (one per language when there is no
/// language preference).
///
/// All versions of a document belong to the same family: same name once the
/// language tag (_SPA / _ENG), the "_docx" type tag and the browser copy
/// suffix " (n)" are removed. Within a family, the best version wins:
///   1. Language: preferred > untagged > other language. With no preference
///      ("Any") each language is its own group, so one per language is shown.
///   2. Type: the ".docx"-derived "_docx.pdf" > the native ".pdf".
///   3. Arrival: the most recent copy wins (a re-download replaces the open one).
/// So the preferred language is used only when it is available; otherwise the
/// closest alternative is shown.
///
/// The decision is taken BEFORE a viewer opens, from every version known at
/// that moment — open or still downloading — so the losing versions never
/// open at all (much lighter on low-end terminals). A version that arrives
/// later and ranks higher replaces the open one; one that ranks lower is
/// discarded. A cheap periodic <see cref="Reconcile"/> re-checks the open
/// documents (e.g. after the preferred language changes).
///
/// Thread-safe: each document's lifecycle calls in from its own thread.
/// </summary>
internal sealed class DocumentCoordinator
{
    internal enum Decision { Open, Discard }

    /// One version of a document known to the coordinator.
    internal sealed class Entry
    {
        public required string Path   { get; init; }
        public required string Family { get; init; }
        public required string Language { get; init; } // "SPA" / "ENG" / ""
        public required bool   IsDocx { get; init; }
        public required long   Arrival { get; init; }  // higher = more recent
        public required long   TrackedAtMs { get; init; } // Environment.TickCount64

        /// True while its viewer is (or is about to be) on screen.
        public bool IsOpen { get; internal set; }

        /// Cancelled when a better version takes its place: the viewer closes.
        public CancellationTokenSource CloseSignal { get; } = new();

        /// For the activity log: the version that replaced this one, or the
        /// one already open when this one was discarded.
        public string? Winner { get; internal set; }
    }

    /// <summary>
    /// Minimum time a version is known before it may decide. When several
    /// versions arrive together (both languages downloaded with one click,
    /// several files pasted), their file events come in one after another a
    /// few milliseconds apart; without this margin a worse version could open
    /// a moment before the better one is even detected, only to be replaced.
    /// Imperceptible next to the ~1 s it takes a viewer window to open.
    /// </summary>
    public const int DefaultGroupingMs = 400;

    private readonly Func<string> _preferredLanguage; // "SPA" / "ENG" / "" (Any)
    private readonly int _groupingMs;
    private readonly object _lock = new();
    private readonly List<Entry> _alive = new();
    private long _arrivals;

    public DocumentCoordinator(Func<string> preferredLanguage, int groupingMs = DefaultGroupingMs)
    {
        _preferredLanguage = preferredLanguage;
        _groupingMs        = groupingMs;
    }

    /// Registers a version as soon as its file is detected, so versions that
    /// are still downloading already count in the decision.
    public Entry Track(string pdfPath)
    {
        var entry = new Entry
        {
            Path     = pdfPath,
            Family   = PdfLifecycleManager.GetFamilyKey(pdfPath),
            Language = PdfLifecycleManager.DetectLanguageSuffix(pdfPath),
            IsDocx   = PdfLifecycleManager.IsDocxType(pdfPath),
            Arrival  = Interlocked.Increment(ref _arrivals),
            TrackedAtMs = Environment.TickCount64,
        };

        lock (_lock) _alive.Add(entry);
        return entry;
    }

    /// <summary>
    /// Called once the file is fully downloaded. Returns <see cref="Decision.Open"/>
    /// if this is the best version (any open, worse version is told to close),
    /// or <see cref="Decision.Discard"/> if a better version is already open.
    /// If a better version is still downloading, waits for it to either open
    /// (→ Discard) or disappear (→ re-evaluated), without busy-waiting.
    /// </summary>
    public Decision WaitForTurn(Entry entry, CancellationToken ct)
    {
        // Let versions that arrive together all be detected first.
        long wait = entry.TrackedAtMs + _groupingMs - Environment.TickCount64;
        if (wait > 0)
            ct.WaitHandle.WaitOne((int)wait);

        List<Entry> toClose;

        lock (_lock)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                string preferred = _preferredLanguage();
                var group = GroupOf(entry, preferred);
                var best  = BestOf(group, preferred);

                if (ReferenceEquals(best, entry))
                {
                    entry.IsOpen = true;
                    toClose = group.Where(e => e.IsOpen && !ReferenceEquals(e, entry)).ToList();
                    foreach (var loser in toClose) loser.Winner = entry.Path;
                    Monitor.PulseAll(_lock);
                    break;
                }

                if (best.IsOpen)
                {
                    entry.Winner = best.Path;
                    return Decision.Discard;
                }

                // A better version is still downloading: wait for it.
                Monitor.Wait(_lock, 500);
            }
        }

        CloseAll(toClose);
        return Decision.Open;
    }

    /// Forgets a version: its viewer closed, it was discarded, or its file vanished.
    public void Release(Entry entry)
    {
        lock (_lock)
        {
            entry.IsOpen = false;
            _alive.Remove(entry);
            Monitor.PulseAll(_lock); // a version waiting on this one re-evaluates
        }
    }

    /// <summary>
    /// Safety net, run periodically: in every group whose best version is open,
    /// closes any other open version. Covers a change of the preferred language
    /// while documents are open. Costs a pass over a handful of in-memory
    /// entries — no disk access, no allocations when nothing is open.
    /// </summary>
    public void Reconcile()
    {
        var toClose = new List<Entry>();

        lock (_lock)
        {
            if (_alive.Count < 2)
                return;

            string preferred = _preferredLanguage();
            foreach (var group in _alive.GroupBy(e => GroupKey(e, preferred)))
            {
                var best = BestOf(group.ToList(), preferred);
                if (best.IsOpen)
                    foreach (var loser in group.Where(e => e.IsOpen && !ReferenceEquals(e, best)))
                    {
                        loser.Winner = best.Path;
                        toClose.Add(loser);
                    }
            }
        }

        CloseAll(toClose);
    }

    // ── Ranking ────────────────────────────────────────────────────────────

    /// Versions compete only within their group: the family, or family +
    /// language when there is no preference (one document per language).
    internal static string GroupKey(Entry e, string preferred) =>
        preferred.Length > 0 ? e.Family : $"{e.Family}|{e.Language}";

    /// Higher is better: language first, then type. Arrival breaks ties.
    internal static int Rank(Entry e, string preferred)
    {
        int language = 0;
        if (preferred.Length > 0)
        {
            if (e.Language.Equals(preferred, StringComparison.OrdinalIgnoreCase)) language = 2;
            else if (e.Language.Length == 0)                                     language = 1;
        }
        return language * 2 + (e.IsDocx ? 1 : 0);
    }

    private List<Entry> GroupOf(Entry entry, string preferred)
    {
        string key = GroupKey(entry, preferred);
        return _alive.Where(e => GroupKey(e, preferred) == key).ToList();
    }

    private static Entry BestOf(List<Entry> group, string preferred) =>
        group.OrderByDescending(e => Rank(e, preferred))
             .ThenByDescending(e => e.Arrival)
             .First();

    // Outside the lock: cancelling runs the viewers' close callbacks.
    private static void CloseAll(List<Entry> entries)
    {
        foreach (var e in entries)
            try { e.CloseSignal.Cancel(); } catch { }
    }
}
