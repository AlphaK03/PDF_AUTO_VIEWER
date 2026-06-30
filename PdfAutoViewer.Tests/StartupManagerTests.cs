using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

public class StartupManagerTests
{
    [Fact]
    public void BuildStartupValue_QuotesExecutablePath()
    {
        const string executablePath = @"C:\Program Files\PdfAutoViewer\PdfAutoViewer.exe";

        var startupValue = StartupManager.BuildStartupValue(executablePath);

        Assert.Equal("\"C:\\Program Files\\PdfAutoViewer\\PdfAutoViewer.exe\"", startupValue);
    }
}
