using PdfAutoViewer.UI;

namespace PdfAutoViewer;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // ApplicationContext keeps the app alive through the tray icon
        // without requiring a main window to stay open.
        Application.Run(new TrayApp());
    }
}
