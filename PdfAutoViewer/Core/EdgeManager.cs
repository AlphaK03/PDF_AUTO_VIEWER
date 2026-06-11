using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PdfAutoViewer.Core;

/// <summary>
/// Opens a PDF in Microsoft Edge and detects when the user closes the tab.
///
/// Opening: Edge is launched with just the file URL. If Edge is already
/// running, the OS passes the URL to the existing instance, which opens a
/// new tab in the user's current window — no extra window is created.
///
/// Close detection: Edge shows the PDF filename in the window title when
/// that tab is active. We enumerate all visible window titles every second
/// via the native EnumWindows API. When the filename has been absent for
/// CloseConfirmCount consecutive checks the tab is considered closed.
///
/// Known limitation: if the user switches away from the PDF tab for
/// more than CloseConfirmCount seconds, cleanup may trigger early.
/// For short document reviews this is acceptable.
/// </summary>
public sealed class EdgeManager(string edgePath)
{
    // Seconds to wait for Edge to render the PDF before polling starts
    private const int StartupGraceMs = 1000;

    // How many consecutive "title absent" checks confirm the tab is closed
    private const int CloseConfirmCount = 3;

    /// <summary>
    /// Opens the PDF in Edge as a new tab, then blocks until the tab is closed.
    /// </summary>
    public void OpenAndWaitForClose(string pdfPath, CancellationToken ct)
    {
        string fileUrl = new Uri(pdfPath).AbsoluteUri;

        Process.Start(new ProcessStartInfo
        {
            FileName        = edgePath,
            Arguments       = $"--no-first-run --no-default-browser-check \"{fileUrl}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });

        WaitForTabClose(Path.GetFileName(pdfPath), ct);
    }

    private static void WaitForTabClose(string pdfName, CancellationToken ct)
    {
        string nameLower = pdfName.ToLowerInvariant();

        // Phase 1 — give Edge time to open and render the PDF
        Thread.Sleep(StartupGraceMs);

        // Phase 2 — wait up to 20 s for the title to appear (confirms the tab loaded)
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (IsTitleVisible(nameLower)) break;
            Thread.Sleep(1000);
        }

        // Phase 3 — wait until the title is absent for CloseConfirmCount checks in a row
        int absentCount = 0;
        while (absentCount < CloseConfirmCount && !ct.IsCancellationRequested)
        {
            Thread.Sleep(1000);
            absentCount = IsTitleVisible(nameLower) ? 0 : absentCount + 1;
        }
    }

    private static bool IsTitleVisible(string pdfNameLower)
    {
        bool found = false;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;

            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);

            if (sb.ToString().ToLowerInvariant().Contains(pdfNameLower))
            {
                found = true;
                return false; // stop enumeration early
            }
            return true;
        }, IntPtr.Zero);

        return found;
    }

    // ── Native Windows API ─────────────────────────────────────────────────
    // Used to read the title of every visible desktop window.

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}
