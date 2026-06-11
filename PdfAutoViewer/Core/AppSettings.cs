using System.Text.Json;
using Microsoft.Win32;

namespace PdfAutoViewer.Core;

public enum LanguagePreference { Any, SPA, ENG }

/// <summary>
/// User configuration, persisted as JSON in:
///   %LOCALAPPDATA%\PdfAutoViewer\settings.json
/// </summary>
public sealed class AppSettings
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "PdfAutoViewer");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "settings.json");

    // ── Settings ───────────────────────────────────────────────────────────

    /// When true, the Downloads folder is resolved from the Windows Registry
    /// on every startup — correctly handles moved folders and non-English Windows.
    public bool AutoDetectFolder { get; set; } = true;

    /// Custom folder path; only used when AutoDetectFolder is false.
    public string WatchFolder { get; set; } = GetSystemDownloadsFolder();

    /// If true, the PDF is deleted automatically after the user closes the Edge tab.
    public bool AutoDelete { get; set; } = true;

    /// Full path to msedge.exe.
    public string EdgePath { get; set; } = FindEdgeExecutable();

    /// Show tray balloon notifications on detect / open / delete.
    public bool ShowNotifications { get; set; } = false;

    /// When both _SPA and _ENG versions of the same document download simultaneously,
    /// open only the one matching this preference. Any = open both.
    public LanguagePreference PreferredLanguage { get; set; } = LanguagePreference.SPA;

    /// Open PDFs in the app's own embedded viewer (WebView2 / Chromium PDF
    /// engine) instead of an Edge tab. Faster open and exact close detection.
    public bool UseBuiltInViewer { get; set; } = false;

    // ── Computed ───────────────────────────────────────────────────────────

    /// The folder that will actually be monitored at runtime.
    public string EffectiveWatchFolder =>
        AutoDetectFolder ? GetSystemDownloadsFolder() : WatchFolder;

    // ── Persistence ────────────────────────────────────────────────────────

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile, System.Text.Encoding.UTF8);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { /* File missing or corrupt — fall back to defaults */ }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(ConfigFile,
            JsonSerializer.Serialize(this, options),
            System.Text.Encoding.UTF8);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// Reads the real Downloads folder from the Windows Registry.
    /// More reliable than hardcoding %USERPROFILE%\Downloads because it respects
    /// user-moved folders and non-English Windows installations.
    public static string GetSystemDownloadsFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");

            if (key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string raw)
            {
                var expanded = Environment.ExpandEnvironmentVariables(raw);
                if (Directory.Exists(expanded))
                    return expanded;
            }
        }
        catch { }

        // Fallback for non-standard environments
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    private static string FindEdgeExecutable()
    {
        string[] candidates =
        [
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        ];
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
