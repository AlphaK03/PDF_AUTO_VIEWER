using System.Text.Json;
using Microsoft.Win32;

namespace PdfAutoViewer.Core;

public enum LanguagePreference { Any, SPA, ENG }

/// <summary>
/// User configuration, persisted as JSON in:
///   %LOCALAPPDATA%\PdfAutoViewer\settings.json
///
/// By design, the preferred document language is the ONLY user-adjustable
/// option. Everything else is fixed:
///   • The watched folder is always the system Downloads folder.
///   • PDFs are always opened in the built-in viewer.
///   • PDFs are always deleted automatically after viewing.
/// </summary>
public sealed class AppSettings
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "PdfAutoViewer");

    private static readonly string ConfigFile = Path.Combine(ConfigDir, "settings.json");

    // ── Settings ───────────────────────────────────────────────────────────

    /// Preferred language when both _SPA and _ENG versions of the same document
    /// download simultaneously. Any = open both. Only user-adjustable setting.
    public LanguagePreference PreferredLanguage { get; set; } = LanguagePreference.SPA;

    // ── Computed (fixed by design) ───────────────────────────────────────────

    /// The folder monitored at runtime — always the system Downloads folder,
    /// resolved from the Windows Registry so it respects moved folders and
    /// non-English Windows installations.
    public string EffectiveWatchFolder => GetSystemDownloadsFolder();

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
}
