namespace PdfAutoViewer.Core;

/// <summary>
/// Watches a folder and fires a callback for every new PDF that appears.
///
/// Primary detection: FileSystemWatcher events — instant.
/// Safety net: a light rescan every 3 seconds that re-fires any recent PDF,
/// so a download is NEVER missed even when a watcher event is lost (buffer
/// overflow, or the timing race around browser placeholder files: Edge can
/// create a 0-byte .pdf, delete it, and rename the real .crdownload into
/// place while the previous event is still being handled).
///
/// The lifecycle manager deduplicates by path, cooldown and a handled-files
/// memory, so firing the same path multiple times is harmless.
/// </summary>
public sealed class FolderMonitor : IDisposable
{
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _rescanTimer;
    private string? _folder;
    private DateTime _startedUtc;
    private readonly Action<string> _onPdfFound;

    public FolderMonitor(Action<string> onPdfFound)
    {
        _onPdfFound = onPdfFound;
    }

    public void Start(string folder)
    {
        Stop(); // Restart cleanly if the folder changes

        _folder     = folder;
        _startedUtc = DateTime.UtcNow;

        _watcher = new FileSystemWatcher(folder)
        {
            Filter             = "*.pdf",
            NotifyFilter       = NotifyFilters.FileName,
            InternalBufferSize = 64 * 1024, // maximum — minimizes event loss
            EnableRaisingEvents = true,
        };

        // Fired when a .pdf file appears directly
        _watcher.Created += (_, e) => _onPdfFound(e.FullPath);

        // Fired when Chrome/Edge rename the temp file once the download finishes
        // e.g.:  report.crdownload  →  report.pdf
        _watcher.Renamed += (_, e) =>
        {
            if (e.FullPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                _onPdfFound(e.FullPath);
        };

        // Watcher overflowed and dropped events — rescan right away
        _watcher.Error += (_, _) => RescanRecent();

        _rescanTimer = new System.Threading.Timer(
            _ => RescanRecent(), null,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    // Re-fires every PDF modified in the last 3 minutes (and after monitoring
    // started, so pre-existing files don't suddenly open on app launch).
    private void RescanRecent()
    {
        var folder = _folder;
        if (folder is null)
            return;

        try
        {
            foreach (var file in Directory.GetFiles(folder, "*.pdf"))
            {
                DateTime written = File.GetLastWriteTimeUtc(file);

                if (written >= _startedUtc.AddSeconds(-5) &&
                    (DateTime.UtcNow - written).TotalMinutes <= 3)
                    _onPdfFound(file);
            }
        }
        catch { /* folder briefly unavailable — next sweep catches up */ }
    }

    public void Stop()
    {
        _rescanTimer?.Dispose();
        _rescanTimer = null;
        _watcher?.Dispose();
        _watcher = null;
        _folder = null;
    }

    public void Dispose() => Stop();
}
