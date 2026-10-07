using Microsoft.Win32;
using System.Windows.Forms;

namespace PdfAutoViewer.Core;

/// <summary>
/// Enables startup registration for the current user via the standard Windows Run key.
/// The registration is scoped to HKEY_CURRENT_USER only, so no administrator rights are required.
///
/// Windows decides what to launch at sign-in from two places:
///   • Run\PdfAutoViewer                      — WHAT to launch (the quoted .exe path).
///   • Explorer\StartupApproved\Run\PdfAutoViewer — whether that entry is switched
///     ON or OFF (the toggle in Settings → Apps → Startup / Task Manager).
/// A Run entry that is switched off is listed but never launched, so both are
/// set: the app must always start with the user session (24/7 operation).
/// </summary>
public sealed class StartupManager
{
    private const string RunKeyPath        = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKeyPath   = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string StartupValueName  = "PdfAutoViewer";

    private readonly Action<string, Exception?>? _logger;

    public StartupManager(Action<string, Exception?>? logger = null)
    {
        _logger = logger;
    }

    /// Registers (or refreshes) the startup entry, re-enables it if it was
    /// switched off, and verifies the result. Failures are logged, never thrown.
    public void EnableStartup()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                             ?? Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.SetValue(StartupValueName, BuildStartupValue(GetExecutablePath()));
            }

            // Switched off in Settings / Task Manager → remove the "disabled"
            // marker. Without a marker Windows treats the entry as enabled.
            using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: true))
            {
                if (approved?.GetValue(StartupValueName) is byte[] state && IsDisabledState(state))
                    approved.DeleteValue(StartupValueName, throwOnMissingValue: false);
            }

            if (!IsStartupEnabled())
                _logger?.Invoke("Startup", new InvalidOperationException(
                    "The startup entry was written but could not be read back as enabled."));
        }
        catch (Exception ex)
        {
            _logger?.Invoke("Startup", ex);
        }
    }

    /// True when the Run entry points to THIS executable and is not switched off.
    public bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var currentValue = key?.GetValue(StartupValueName) as string;
            if (!string.Equals(currentValue, BuildStartupValue(GetExecutablePath()), StringComparison.OrdinalIgnoreCase))
                return false;

            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath, writable: false);
            return !(approved?.GetValue(StartupValueName) is byte[] state && IsDisabledState(state));
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

    /// StartupApproved values are 12 bytes; the lowest bit of the first byte
    /// marks the entry as disabled (0x02/0x06 = on, 0x03/0x07 = off).
    internal static bool IsDisabledState(byte[] state) =>
        state.Length > 0 && (state[0] & 0x01) != 0;

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
