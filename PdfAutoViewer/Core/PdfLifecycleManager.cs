using System.Text.RegularExpressions;

namespace PdfAutoViewer.Core;

/// <summary>
/// Orchestrates the full lifecycle of each detected PDF:
///   1. Detect  — notify the UI that a new PDF was found.
///   2. Stabilize — wait for the file size to stop changing (download complete).
///   3. Open    — open the PDF in Edge and notify the UI.
///   4. Wait    — block until the user closes the Edge tab.
///   5. Delete  — if auto-delete is enabled, remove the PDF (and browser duplicates).
///
/// Each PDF runs in its own background Task so multiple simultaneous downloads
/// are handled independently without blocking each other.
/// </summary>
public sealed class PdfLifecycleManager : IDisposable
{
    public const string EventDetected = "detected";
    public const string EventOpened   = "opened";
    public const string EventDeleted  = "deleted";
    public const string EventError    = "error";

    private readonly AppSettings _settings;
    private readonly Action<string, string> _onEvent; // (eventType, message)
    private readonly CancellationTokenSource _cts = new();

    // Tracks which file paths are currently being processed to avoid duplicates
    private readonly HashSet<string> _inProgress = new(StringComparer.OrdinalIgnoreCase);

    // Records when each file was last finished to ignore late watchdog events
    private readonly Dictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);

    // Files already fully handled (path → LastWriteTimeUtc at completion).
    // The folder rescan re-fires existing files every few seconds; this map
    // keeps them from reopening. A re-download changes the write time, so
    // genuinely new content is always processed.
    private readonly Dictionary<string, DateTime> _handled = new(StringComparer.OrdinalIgnoreCase);

    // Pending language pairs waiting for their _SPA / _ENG counterpart
    private readonly Dictionary<string, PairSlot> _languagePairs = new(StringComparer.OrdinalIgnoreCase);

    // Files whose deletion failed because something (Defender scan, OneDrive
    // sync, Edge) still holds them. Value = LastWriteTimeUtc when enqueued,
    // so a re-downloaded file at the same path is never wrongly deleted.
    private readonly Dictionary<string, DateTime> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Threading.Timer _janitor;

    private readonly object _lock = new();

    public PdfLifecycleManager(AppSettings settings, Action<string, string> onEvent)
    {
        _settings = settings;
        _onEvent  = onEvent;
        _janitor  = new System.Threading.Timer(
            _ => SweepPendingDeletes(), null,
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

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
                seenStamp == SafeLastWriteUtc(key))
                return;

            _inProgress.Add(key);
        }

        var ct = _cts.Token;
        Task.Run(() =>
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
                Notify(EventError, $"Error with '{Path.GetFileName(pdfPath)}': {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _inProgress.Remove(key);
                    if (completed)
                    {
                        _cooldown[key] = DateTime.UtcNow;
                        _handled[key]  = SafeLastWriteUtc(key);

                        // Keep the map bounded: drop entries for deleted files
                        if (_handled.Count > 500)
                            foreach (var k in _handled.Keys.Where(k => !File.Exists(k)).ToList())
                                _handled.Remove(k);
                    }
                }
            }
        }, ct);
    }

    // ── Lifecycle steps ────────────────────────────────────────────────────

    // Returns true if the cycle completed (file opened or deliberately skipped);
    // false means the file vanished or never stabilized — no cooldown, so a
    // later event for the same path (the real download) is still processed.
    private bool RunLifecycle(string pdfPath, CancellationToken ct)
    {
        string lang = DetectLanguageSuffix(pdfPath);
        bool filterApplies =
            lang.Length > 0 && _settings.PreferredLanguage != LanguagePreference.Any;

        // Built-in viewer: every PDF ALWAYS opens; once both language siblings
        // are visible, the viewer itself closes the non-matching window. Edge
        // tabs can't be closed programmatically, so for Edge the non-preferred
        // file is filtered out before opening.
        if (filterApplies && !_settings.UseBuiltInViewer)
            return RunWithLanguageFilter(pdfPath, lang, ct);

        return RunLifecycleDirect(pdfPath, ct);
    }

    // Full lifecycle for a single file with no language filtering involved.
    private bool RunLifecycleDirect(string pdfPath, CancellationToken ct)
    {
        Notify(EventDetected, $"PDF detected: {Path.GetFileName(pdfPath)}");

        if (!WaitForStability(pdfPath, ct))
            return false;

        return OpenAndFinish(pdfPath, ct);
    }

    // Open in Edge, block until the tab closes, then delete if configured.
    private bool OpenAndFinish(string pdfPath, CancellationToken ct)
    {
        // The file may have been deleted between stabilization and now
        // (e.g., duplicate cleanup from another cycle). Never open a dead link.
        if (!File.Exists(pdfPath))
            return false;

        bool viewed = false;
        if (_settings.UseBuiltInViewer)
        {
            string lang      = DetectLanguageSuffix(pdfPath);
            string preferred = _settings.PreferredLanguage == LanguagePreference.Any
                ? "" : _settings.PreferredLanguage.ToString();

            string? viewerError = UI.PdfViewerForm.ShowAndWait(
                pdfPath, ct, GetPairingKey(pdfPath), lang, preferred);

            viewed = viewerError == null;
            if (!viewed)
                Notify(EventError, $"Built-in viewer failed → {viewerError}. Opening in Edge.");
        }

        // Edge path: default, or fallback when the built-in viewer can't start
        if (!viewed)
            new EdgeManager(_settings.EdgePath).OpenAndWaitForClose(pdfPath, ct);

        Notify(EventOpened, $"Opened: {Path.GetFileName(pdfPath)}");

        if (_settings.AutoDelete)
            DeleteWithDuplicates(pdfPath);

        return true;
    }

    // Language filtering with zero added latency for the preferred language:
    //   • Preferred file  → opens immediately and signals its counterpart to stand down.
    //   • Non-preferred   → opens immediately UNLESS the preferred counterpart is part
    //     of the same download burst (signaled by its task, or already on disk);
    //     if uncertain it waits at most 1.2s and then opens anyway (fallback).
    private bool RunWithLanguageFilter(string pdfPath, string lang, CancellationToken ct)
    {
        string name = Path.GetFileName(pdfPath);
        Notify(EventDetected, $"PDF detected: {name}");

        if (!WaitForStability(pdfPath, ct))
            return false; // download never completed

        string pairingKey  = GetPairingKey(pdfPath);
        string preferred   = _settings.PreferredLanguage.ToString(); // "SPA" / "ENG"
        bool   isPreferred = lang.Equals(preferred, StringComparison.OrdinalIgnoreCase);

        PairSlot slot;
        lock (_lock)
        {
            // Purge stale slots so the dictionary doesn't grow forever
            foreach (var stale in _languagePairs
                         .Where(p => (DateTime.UtcNow - p.Value.Created).TotalSeconds > 60)
                         .Select(p => p.Key).ToList())
                _languagePairs.Remove(stale);

            if (!_languagePairs.TryGetValue(pairingKey, out slot!) ||
                (DateTime.UtcNow - slot.Created).TotalSeconds > 15)
            {
                slot = new PairSlot();
                _languagePairs[pairingKey] = slot;
            }
        }

        if (isPreferred)
        {
            // Preferred language never waits.
            slot.PreferredArrived.Set();
            return OpenAndFinish(pdfPath, ct);
        }

        // Non-preferred: skip only if the preferred counterpart belongs to the
        // same burst. Disk check catches it even before its own task signals.
        bool preferredHere =
            slot.PreferredArrived.IsSet ||
            PreferredCounterpartOnDisk(pdfPath, preferred) ||
            slot.PreferredArrived.Wait(1200, ct) ||
            PreferredCounterpartOnDisk(pdfPath, preferred);

        if (!preferredHere)
        {
            // Solo download — open regardless of language preference.
            return OpenAndFinish(pdfPath, ct);
        }

        Notify(EventDetected, $"Skipped (language filter): {name}");
        if (_settings.AutoDelete)
        {
            // Deleting immediately races Edge/Defender's post-download virus
            // scan and makes Edge report "virus scan failed" on the download.
            // The file is never shown to the user, so this delay is invisible.
            DateTime stamp = SafeLastWriteUtc(pdfPath);

            if (ct.WaitHandle.WaitOne(3000))
                return true; // app shutting down

            // If the file changed during the wait, a new download overwrote
            // this path — process it as a fresh arrival instead of deleting it.
            if (SafeLastWriteUtc(pdfPath) != stamp)
                return RunLifecycle(pdfPath, ct);

            TryDeleteFile(pdfPath);
        }

        return true;
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    // True if a *recently downloaded* preferred-language counterpart of this
    // file exists in the same folder. Recency (30s) avoids matching leftovers
    // from older downloads when auto-delete is off.
    private static bool PreferredCounterpartOnDisk(string pdfPath, string preferredLang)
    {
        try
        {
            string dir = Path.GetDirectoryName(pdfPath)!;
            string key = GetPairingKey(pdfPath);

            foreach (var file in Directory.GetFiles(dir, "*.pdf"))
            {
                if (file.Equals(pdfPath, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!DetectLanguageSuffix(file).Equals(preferredLang, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!GetPairingKey(file).Equals(key, StringComparison.Ordinal))
                    continue;
                // 0-byte placeholders don't count — they may never become real
                if (new FileInfo(file).Length < 100)
                    continue;
                if ((DateTime.UtcNow - File.GetCreationTimeUtc(file)).TotalSeconds <= 30)
                    return true;
            }
        }
        catch { }

        return false;
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
    private static string StripNumericSuffix(string stem) =>
        Regex.Replace(stem, @"\s*\(\d+\)\s*$", "").Trim();

    // Returns "SPA", "ENG", or "" — matches _SPA/_ENG anywhere in the name,
    // requiring the token to be followed by _ , space, or end of stem.
    private static string DetectLanguageSuffix(string pdfPath)
    {
        string stem = Path.GetFileNameWithoutExtension(pdfPath);
        if (Regex.IsMatch(stem, @"_SPA(?=[_\s]|$)", RegexOptions.IgnoreCase)) return "SPA";
        if (Regex.IsMatch(stem, @"_ENG(?=[_\s]|$)", RegexOptions.IgnoreCase)) return "ENG";
        return "";
    }

    // Pairing key: removes the _SPA/_ENG token, normalizes separators, uppercases.
    // e.g. "D123_H_SPA_Report" and "D123_H_ENG Report" both yield "D123 H REPORT".
    private static string GetPairingKey(string pdfPath)
    {
        string dir      = Path.GetDirectoryName(pdfPath) ?? "";
        string stem     = Path.GetFileNameWithoutExtension(pdfPath);
        string clean    = Regex.Replace(stem, @"_(SPA|ENG)(?=[_\s]|$)", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"[_\s]+", " ").Trim().ToUpperInvariant();
        return Path.Combine(dir.ToUpperInvariant(), clean);
    }

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
                Notify(EventDeleted, $"Deleted: {Path.GetFileName(path)}");
                return;
            }
            catch { /* locked — retry */ }
        }

        lock (_lock)
            _pendingDeletes[Path.GetFullPath(path)] = SafeLastWriteUtc(path);

        Notify(EventError,
            $"'{Path.GetFileName(path)}' is locked — will keep retrying in background");
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
                else if (SafeLastWriteUtc(path) != stamp)
                {
                    // Replaced by a newer download — its own cycle cleans it up
                    resolved = true;
                }
                else
                {
                    File.Delete(path);
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

    public void Dispose()
    {
        _cts.Cancel();
        _janitor.Dispose();
    }

    // ── Nested type ────────────────────────────────────────────────────────

    // Shared coordination point for the two language variants of one document.
    // The preferred-language task sets PreferredArrived; the non-preferred task
    // checks/waits on it to decide whether to stand down.
    private sealed class PairSlot
    {
        public readonly ManualResetEventSlim PreferredArrived = new(false);
        public DateTime Created { get; } = DateTime.UtcNow;
    }
}
