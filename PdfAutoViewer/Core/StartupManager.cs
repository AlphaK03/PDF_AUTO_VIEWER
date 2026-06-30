using Microsoft.Win32;
using System.Windows.Forms;

namespace PdfAutoViewer.Core;

/// <summary>
/// Enables startup registration for the current user via the standard Windows Run key.
/// The registration is scoped to HKEY_CURRENT_USER only, so no administrator rights are required.
/// </summary>
public sealed class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "PdfAutoViewer";

    private readonly Action<string, Exception?>? _logger;

    public StartupManager(Action<string, Exception?>? logger = null)
    {
        _logger = logger;
    }

    public void EnableStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            key.SetValue(StartupValueName, BuildStartupValue(GetExecutablePath()));
        }
        catch (Exception ex)
        {
            _logger?.Invoke("Startup", ex);
        }
    }

    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var currentValue = key?.GetValue(StartupValueName) as string;
            return string.Equals(currentValue, BuildStartupValue(GetExecutablePath()), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger?.Invoke("Startup", ex);
            return false;
        }
    }

    public static string BuildStartupValue(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return string.Empty;

        var trimmed = executablePath.Trim();
        return trimmed.StartsWith('"') ? trimmed : $"\"{trimmed}\"";
    }

    private static string GetExecutablePath()
    {
        try
        {
            return Path.GetFullPath(Application.ExecutablePath);
        }
        catch
        {
            return Path.GetFullPath(Environment.ProcessPath ?? string.Empty);
        }
    }
}
