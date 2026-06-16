using System.Text.RegularExpressions;

namespace PdfAutoViewer.Core;

/// <summary>
/// Orchestrates the full lifecycle of each detected PDF:
///   1. Detect    — notify the UI that a new PDF was found.
///   2. Stabilize — wait for the file size to stop changing (download complete).
///   3. Open      — open the PDF in the built-in viewer and notify the UI.
///   4. Wait      — block until the viewer window closes (user, language filter,
///                  or the 20-minute viewing limit).
///   5. Delete    — remove the PDF and any browser-generated duplicate copies.
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
    public const string EventWarning  = "warning"; // 15-minute viewing-time alert

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
        }, ct);
    }

    // ── Lifecycle steps ────────────────────────────────────────────────────

    // Returns true if the cycle completed (file opened or deliberately skipped);
    // false means the file vanished or never stabilized — no cooldown, so a
    // later event for the same path (the real download) is still processed.
    // Full lifecycle: detect → wait for the download to finish → open in the
    // built-in viewer → delete. Language filtering (when both _SPA and _ENG
    // arrive) is handled inside the viewer, which opens every document and
    // closes the non-matching window once both are visible.
    private bool RunLifecycle(string pdfPath, CancellationToken ct)
    {
        Notify(EventDetected, $"PDF detected: {Path.GetFileName(pdfPath)}");

        if (!WaitForStability(pdfPath, ct))
            return false; // download never completed

        return OpenAndFinish(pdfPath, ct);
    }

    // Opens the PDF in the built-in viewer, blocks until the window closes
    // (by the user, by the language reconciler, or by the 20-minute limit),
    // then deletes the file. Auto-delete is always on by design.
    private bool OpenAndFinish(string pdfPath, CancellationToken ct)
    {
        // The file may have been deleted between stabilization and now
        // (e.g., duplicate cleanup from another cycle). Never open a dead link.
        if (!File.Exists(pdfPath))
            return false;

        string lang      = DetectLanguageSuffix(pdfPath);
        string preferred = _settings.PreferredLanguage == LanguagePreference.Any
            ? "" : _settings.PreferredLanguage.ToString();

        string? viewerError = UI.PdfViewerForm.ShowAndWait(
            pdfPath, ct, GetPairingKey(pdfPath), GetDocumentKey(pdfPath), lang, preferred,
            GetTypeGroupKey(pdfPath), IsDocxType(pdfPath), Notify);

        if (viewerError != null)
        {
            // No Edge fallback by design. Keep the file so it can be opened
            // manually; report the error so the failure is not silent.
            Notify(EventError, $"Could not open the viewer: {viewerError}");
            return true; // completed (avoids a retry storm)
        }

        Notify(EventOpened, $"Opened: {Path.GetFileName(pdfPath)}");
        DeleteWithDuplicates(pdfPath);
        return true;
    }

    private static DateTime SafeLastWriteUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
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

    // Pairing key: removes the _SPA/_ENG token, normalizes separators, uppercases.
    // e.g. "D123_H_SPA_Report" and "D123_H_ENG Report" both yield "D123 H REPORT".
    internal static string GetPairingKey(string pdfPath)
    {
        string dir      = Path.GetDirectoryName(pdfPath) ?? "";
        string stem     = Path.GetFileNameWithoutExtension(pdfPath);
        string clean    = Regex.Replace(stem, @"_(SPA|ENG)(?=[_\s]|$)", "", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"[_\s]+", " ").Trim().ToUpperInvariant();
        return Path.Combine(dir.ToUpperInvariant(), clean);
    }

    // Document key: keeps the language but drops the browser duplicate suffix,
    // so "doc.pdf", "doc (1).pdf" and "doc (2).pdf" all map to the same key.
    // Used by the viewer to replace an open document with a freshly downloaded
    // copy of the SAME document (same language) — the newest version wins.
    internal static string GetDocumentKey(string pdfPath)
    {
        string dir  = Path.GetDirectoryName(pdfPath) ?? "";
        string stem = StripNumericSuffix(Path.GetFileNameWithoutExtension(pdfPath));
        return Path.Combine(dir.ToUpperInvariant(), stem.ToUpperInvariant());
    }

    // True when the PDF was produced from a .docx source. Those files download as
    // "<name>_docx.pdf" (the ".docx" extension becomes a trailing "_docx" token).
    // The "(n)" duplicate suffix is stripped first, so a re-downloaded copy
    // ("<name>_docx (1).pdf") is still recognized as the docx-derived type.
    internal static bool IsDocxType(string pdfPath) =>
        Regex.IsMatch(StripNumericSuffix(Path.GetFileNameWithoutExtension(pdfPath)),
                      @"[_\s]docx$", RegexOptions.IgnoreCase);

    // Type-group key: identifies the same document AND language regardless of its
    // type (native ".pdf" vs the ".docx"-derived "_docx.pdf"). Keeps the language,
    // drops the "_docx" token and the "(n)" duplicate suffix, and normalizes
    // separators — so "D123_H_SPA_Report.pdf" and "D123_H_SPA_Report_docx.pdf"
    // share the same key. Used to give the docx-derived copy priority.
    internal static string GetTypeGroupKey(string pdfPath)
    {
        string dir  = Path.GetDirectoryName(pdfPath) ?? "";
        string stem = StripNumericSuffix(Path.GetFileNameWithoutExtension(pdfPath));
        stem = Regex.Replace(stem, @"[_\s]docx$", "", RegexOptions.IgnoreCase);
        stem = Regex.Replace(stem, @"[_\s]+", " ").Trim().ToUpperInvariant();
        return Path.Combine(dir.ToUpperInvariant(), stem);
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
}
