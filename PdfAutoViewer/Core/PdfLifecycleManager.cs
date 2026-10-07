using System.Text.RegularExpressions;

namespace PdfAutoViewer.Core;

/// <summary>
/// Orchestrates the full lifecycle of each detected PDF:
///   1. Detect    — register the version with the <see cref="DocumentCoordinator"/>.
///   2. Stabilize — wait for the file size to stop changing (download complete).
///   3. Select    — ask the coordinator whether this is the version to show
///                  (language / type / most recent copy). If not, it is
///                  discarded (deleted) without ever opening.
///   4. Open      — open the PDF in the built-in viewer and block until the
///                  window closes (user, a better version replacing it, or the
///                  20-minute viewing limit).
///   5. Delete    — remove the PDF and any browser-generated duplicate copies.
///
/// Each PDF runs on its own dedicated background thread so multiple
/// simultaneous downloads are handled independently without blocking each other.
/// </summary>
public sealed class PdfLifecycleManager : IDisposable
{
    public const string EventDetected = "detected";
    public const string EventOpened   = "opened";
    public const string EventDeleted  = "deleted";

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PdfAutoViewer", "app-error.log");

    private readonly AppSettings _settings;
    private readonly Action<string, string> _onEvent; // (eventType, message)
    private readonly Action<PdfError> _onError;       // document did not open
    private readonly CancellationTokenSource _cts = new();

    // Tracks which file paths are currently being processed to avoid duplicates
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);

    // Records when each file was last finished to ignore late watchdog events
    private readonly Dictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);

    // Files already fully handled (path → FileStampUtc at completion).
    // The folder rescan re-fires existing files every few seconds; this map
    // keeps them from reopening. A re-download (new write time) or a fresh
    // copy of the file (new creation time) is always processed again.
    private readonly Dictionary<string, DateTime> _handled = new(StringComparer.OrdinalIgnoreCase);

    // Files whose deletion failed because something (Defender scan, OneDrive
    // sync, Edge) still holds them. Value = FileStampUtc when enqueued,
    // so a re-downloaded file at the same path is never wrongly deleted.
    private readonly Dictionary<string, DateTime> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    // Decides which version of each document is shown (language / type / copy).
    private readonly DocumentCoordinator _coordinator;

    /// What happened to each document this session (status window → "Log").
    public ActivityLog Activity { get; } = new();

    private readonly System.Threading.Timer _janitor;
    private readonly System.Threading.Timer _reconciler;

    private readonly object _lock = new();

    public PdfLifecycleManager(AppSettings settings, Action<string, string> onEvent,
                               Action<PdfError> onError)
    {
        _settings    = settings;
        _onEvent     = onEvent;
        _onError     = onError;
        _coordinator = new DocumentCoordinator(() => PreferredLanguage);
        _janitor     = new System.Threading.Timer(
            _ => SweepPendingDeletes(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));

        // Continuous safety net for the open documents (e.g. the preferred
        // language changed while two languages were open). In-memory only.
        _reconciler  = new System.Threading.Timer(
            _ => _coordinator.Reconcile(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    // "SPA" / "ENG", or "" when the preference is Any (read live: the user can
    // change it at any time from the main window).
    private string PreferredLanguage =>
        _settings.PreferredLanguage == LanguagePreference.Any
            ? "" : _settings.PreferredLanguage.ToString();

    // ── Public API ─────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules a PDF for processing in a background thread.
    /// Silently drops the call if this file is already being handled,
    /// or if it was processed within the last 5 seconds.
    /// </summary>
    public void Schedule(string pdfPath)
    {
        string key = Path.GetFullPath(pdfPath);

        lock (_lock)
        {
            if (_inProgress.Contains(key))
                return;

            // Ignore events that arrive right after a previous cycle finished
            // (Edge can fire extra file events right after closing a tab)
            if (_cooldown.TryGetValue(key, out var last) &&
                (DateTime.UtcNow - last).TotalSeconds < 5)
                return;

            // Already handled and unchanged since — re-fired by the rescan
            if (_handled.TryGetValue(key, out var seenStamp) &&
                seenStamp == FileStampUtc(key))
                return;

            _inProgress.Add(key);
        }

        // LongRunning = a dedicated thread. Each cycle blocks for as long as its
        // document is open; on the shared thread pool a burst of downloads
        // would starve it and delay every other document.
        var ct = _cts.Token;
        Task.Factory.StartNew(() =>
        {
            // Cooldown must apply ONLY to completed cycles. Browsers often
            // create a 0-byte placeholder .pdf, delete it, and rename the real
            // .crdownload seconds later — if the failed placeholder cycle set a
            // cooldown, the real download's event would be dropped.
            bool completed = false;
            try   { completed = RunLifecycle(pdfPath, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                completed = true; // avoid retry storms on persistent errors
                ReportError(PdfErrorKind.Unexpected, pdfPath, $"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _inProgress.Remove(key);
                    if (completed)
                    {
                        _cooldown[key] = DateTime.UtcNow;
                        _handled[key]  = FileStampUtc(key);

                        // Bound growth for 24/7 operation. Cooldown entries are
                        // only relevant for ~5s, so drop stale ones; handled
                        // entries for files that no longer exist can also go.
                        if (_cooldown.Count > 200)
                        {
                            var cutoff = DateTime.UtcNow.AddMinutes(-1);
                            foreach (var k in _cooldown.Where(p => p.Value < cutoff)
                                                       .Select(p => p.Key).ToList())
                                _cooldown.Remove(k);
                        }

                        if (_handled.Count > 500)
                            foreach (var k in _handled.Keys.Where(k => !File.Exists(k)).ToList())
                                _handled.Remove(k);
                    }
                }
            }
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // ── Lifecycle steps ────────────────────────────────────────────────────

    // Returns true if the cycle completed (file opened or deliberately skipped);
    // false means the file vanished or never stabilized — no cooldown, so a
    // later event for the same path (the real download) is still processed.
    // Full lifecycle: detect → wait for the download to finish → select the
    // version to show → open in the built-in viewer → delete.
    private bool RunLifecycle(string pdfPath, CancellationToken ct)
    {
        Notify(EventDetected, $"PDF detected: {Path.GetFileName(pdfPath)}");

        // Registered right away, so a version still downloading already
        // counts when its siblings decide whether to open.
        var entry = _coordinator.Track(pdfPath);
        try
        {
            if (!WaitForStability(pdfPath, ct))
                return false; // download never completed

            // The file may have been deleted between stabilization and now
            // (e.g., duplicate cleanup from another cycle). Never open a dead link.
            if (!File.Exists(pdfPath))
                return false;

            // Logged only once the file is complete, so the browser's
            // short-lived placeholder files never clutter the log.
            Activity.Add(pdfPath, ActivityKind.Downloaded);

            if (_coordinator.WaitForTurn(entry, ct) == DocumentCoordinator.Decision.Discard)
            {
                // A better version of this document is already on screen:
                // never open this one, just clean it up.
                Activity.Add(pdfPath, ActivityKind.NotOpened,
                    $"kept instead: {Path.GetFileName(entry.Winner)}");
                TryDeleteFile(pdfPath);
                return true;
            }

            return OpenAndFinish(pdfPath, entry, ct);
        }
        finally
        {
            _coordinator.Release(entry);
        }
    }

    // Opens the PDF in the built-in viewer, blocks until the window closes
    // (by the user, by a better version replacing it, or by the 20-minute
    // limit), then deletes the file. Auto-delete is always on by design.
    private bool OpenAndFinish(string pdfPath, DocumentCoordinator.Entry entry, CancellationToken ct)
    {
        using var close = CancellationTokenSource.CreateLinkedTokenSource(ct, entry.CloseSignal.Token);
        bool spanish = _settings.PreferredLanguage == LanguagePreference.SPA;

        Activity.Add(pdfPath, ActivityKind.Opened);
        string? viewerError = UI.PdfViewerForm.ShowAndWait(pdfPath, close.Token, spanish,
                                                           out bool closedByTimeLimit);

        if (viewerError != null)
        {
            // No Edge fallback by design. Keep the file so it can be opened
            // manually; report the error so the failure is not silent.
            ReportError(PdfErrorKind.ViewerFailed, pdfPath, viewerError);
            return true; // completed (avoids a retry storm)
        }

        if (entry.CloseSignal.IsCancellationRequested)
            Activity.Add(pdfPath, ActivityKind.Replaced, $"replaced by: {Path.GetFileName(entry.Winner)}");
        else if (closedByTimeLimit)
            Activity.Add(pdfPath, ActivityKind.TimeLimit, "20-minute viewing limit");
        else if (!ct.IsCancellationRequested)
            Activity.Add(pdfPath, ActivityKind.ClosedByUser);

        Notify(EventOpened, $"Opened: {Path.GetFileName(pdfPath)}");
        DeleteWithDuplicates(pdfPath);
        return true;
    }

    // Identifies a file's content version: the later of its last write and its
    // creation. A browser re-download changes the write time; a file copied or
    // pasted into Downloads keeps its old write time but gets a new creation
    // time — both must count as new.
    internal static DateTime FileStampUtc(string path)
    {
        try
        {
            var created = File.GetCreationTimeUtc(path);
            var written = File.GetLastWriteTimeUtc(path);
            return created > written ? created : written;
        }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// Waits until the browser finishes writing the file.
    ///
    /// Fast path: try to open the file with FileShare.Read, which DENIES other
    /// writers — it only succeeds when no process holds a write handle. Edge
    /// writes to a .crdownload temp and renames on completion, so the renamed
    /// .pdf is immediately openable → near-zero latency in the common case.
    ///
    /// Slow path (writer detected): fall back to size polling — unchanged for
    /// 2 consecutive checks, like the original logic.
    /// </summary>
    private static bool WaitForStability(string pdfPath, CancellationToken ct)
    {
        long lastSize   = -1;
        int stableCount = 0;
        var deadline    = DateTime.UtcNow.AddMinutes(5);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (!File.Exists(pdfPath))
                return false;

            try
            {
                long size = new FileInfo(pdfPath).Length;

                if (size >= 100)
                {
                    // Fast path: no write handle open → download complete.
                    try
                    {
                        using var fs = new FileStream(
                            pdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return true;
                    }
                    catch (IOException) { /* still being written — fall through */ }

                    if (size == lastSize)
                    {
                        if (++stableCount >= 2) return true;
                    }
                    else
                    {
                        stableCount = 0;
                        lastSize    = size;
                    }
                }
                else
                {
                    lastSize = size;
                }
            }
            catch { /* File briefly inaccessible — try again */ }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>
    /// Deletes the PDF and any browser-generated duplicate copies.
    /// Browsers name duplicates as "report (2).pdf", "report (3).pdf", etc.
    /// Each file is deleted independently so one locked file never prevents
    /// the others from being cleaned up.
    /// </summary>
    private void DeleteWithDuplicates(string pdfPath)
    {
        string self     = Path.GetFullPath(pdfPath);
        string dir      = Path.GetDirectoryName(self)!;
        string baseName = StripNumericSuffix(Path.GetFileNameWithoutExtension(self));

        string[] candidates;
        try   { candidates = Directory.GetFiles(dir, "*.pdf"); }
        catch { candidates = [self]; }

        foreach (var file in candidates)
        {
            string fileStem = StripNumericSuffix(Path.GetFileNameWithoutExtension(file));
            if (!fileStem.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Never delete a duplicate that another task is actively processing
            // (it may be about to open it); that task cleans up its own file.
            string full = Path.GetFullPath(file);
            if (!full.Equals(self, StringComparison.OrdinalIgnoreCase))
            {
                lock (_lock)
                {
                    if (_inProgress.Contains(full))
                        continue;
                }
            }

            TryDeleteFile(file);
        }
    }

    // Removes the "(2)", "(3)" suffix that browsers add to duplicate downloads
    internal static string StripNumericSuffix(string stem) =>
        Regex.Replace(stem, @"\s*\(\d+\)\s*$", "").Trim();

    // Returns "SPA", "ENG", or "" — matches _SPA/_ENG anywhere in the name,
    // requiring the token to be followed by _ , space, or end of stem.
    internal static string DetectLanguageSuffix(string pdfPath)
    {
        string stem = Path.GetFileNameWithoutExtension(pdfPath);
        if (Regex.IsMatch(stem, @"_SPA(?=[_\s]|$)", RegexOptions.IgnoreCase)) return "SPA";
        if (Regex.IsMatch(stem, @"_ENG(?=[_\s]|$)", RegexOptions.IgnoreCase)) return "ENG";
        return "";
    }

    // Family key: identifies the DOCUMENT regardless of its version — drops the
    // "(n)" duplicate suffix, the "_docx" type token and the _SPA/_ENG language
    // token, normalizes separators and uppercases. Every version of a document
    // shares it, so they compete for the single slot on screen. e.g.
    //   "D123_H_SPA_Report_docx (1)" and "D123_H_ENG Report" → "D123 H REPORT".
    // (Stripping "_docx" too is what lets a docx-derived file in one language
    // compete with a native file in the other one.)
    internal static string GetFamilyKey(string pdfPath)
    {
        string dir   = Path.GetDirectoryName(pdfPath) ?? "";
        string stem  = StripNumericSuffix(Path.GetFileNameWithoutExtension(pdfPath));
        stem         = Regex.Replace(stem, @"[_\s]docx$", "", RegexOptions.IgnoreCase);
        string clean = Regex.Replace(stem, @"_(SPA|ENG)(?=[_\s]|$)", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"[_\s]+", " ").Trim().ToUpperInvariant();
        return Path.Combine(dir.ToUpperInvariant(), clean);
    }

    // True when the PDF was produced from a .docx source. Those files download as
    // "<name>_docx.pdf" (the ".docx" extension becomes a trailing "_docx" token).
    // The "(n)" duplicate suffix is stripped first, so a re-downloaded copy
    // ("<name>_docx (1).pdf") is still recognized as the docx-derived type.
    internal static bool IsDocxType(string pdfPath) =>
        Regex.IsMatch(StripNumericSuffix(Path.GetFileNameWithoutExtension(pdfPath)),
                      @"[_\s]docx$", RegexOptions.IgnoreCase);

    // Deletes with growing retries (~9s total). Right after a download the
    // file is often locked by Defender's scan, OneDrive sync, or Edge itself.
    // If it is STILL locked after all attempts, it is handed to the janitor,
    // which keeps retrying in the background every 20s — no file is ever
    // silently left behind.
    private void TryDeleteFile(string path)
    {
        int[] waitsMs = [0, 700, 1500, 2500, 4000];

        foreach (int wait in waitsMs)
        {
            if (wait > 0)
                Thread.Sleep(wait);

            try
            {
                if (!File.Exists(path))
                    return;

                File.Delete(path);
                Activity.Add(path, ActivityKind.Deleted);
                Notify(EventDeleted, $"Deleted: {Path.GetFileName(path)}");
                return;
            }
            catch { /* locked — retry */ }
        }

        lock (_lock)
            _pendingDeletes[Path.GetFullPath(path)] = FileStampUtc(path);

        // Not shown to the operator: it resolves by itself and needs no action.
        Activity.Add(path, ActivityKind.DeletePending, "locked by another program — retrying every 20 s");
        Log("Delete", $"'{Path.GetFileName(path)}' is locked — will keep retrying in background");
    }

    // Janitor pass: retries every pending delete. Runs every 20 seconds.
    private void SweepPendingDeletes()
    {
        KeyValuePair<string, DateTime>[] pending;
        lock (_lock)
        {
            if (_pendingDeletes.Count == 0)
                return;
            pending = _pendingDeletes.ToArray();
        }

        foreach (var (path, stamp) in pending)
        {
            lock (_lock)
            {
                if (_inProgress.Contains(path))
                    continue; // a new cycle owns this path right now
            }

            bool resolved;
            try
            {
                if (!File.Exists(path))
                {
                    resolved = true; // already gone
                }
                else if (FileStampUtc(path) != stamp)
                {
                    // Replaced by a newer download — its own cycle cleans it up
                    resolved = true;
                }
                else
                {
                    File.Delete(path);
                    Activity.Add(path, ActivityKind.Deleted, "after retry");
                    Notify(EventDeleted, $"Deleted (retry): {Path.GetFileName(path)}");
                    resolved = true;
                }
            }
            catch
            {
                resolved = false; // still locked — keep for the next sweep
            }

            if (resolved)
                lock (_lock) _pendingDeletes.Remove(path);
        }
    }

    private void Notify(string type, string message)
    {
        try { _onEvent(type, message); }
        catch { }
    }

    // A document did not open: always logged, and shown to the operator.
    private void ReportError(PdfErrorKind kind, string pdfPath, string detail)
    {
        Log(kind.ToString(), $"'{Path.GetFileName(pdfPath)}' → {detail}");
        Activity.Add(pdfPath, ActivityKind.Error, detail);

        try { _onError(new PdfError(kind, Path.GetFileName(pdfPath), detail, DateTime.Now)); }
        catch { }
    }

    private static void Log(string kind, string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
            File.AppendAllText(LogFile,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{kind}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _janitor.Dispose();
        _reconciler.Dispose();
    }
}
