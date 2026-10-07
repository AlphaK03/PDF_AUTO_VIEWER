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

    [Fact]
    public void BuildStartupValue_AlreadyQuoted_IsNotQuotedTwice()
        => Assert.Equal("\"C:\\A\\b.exe\"", StartupManager.BuildStartupValue("\"C:\\A\\b.exe\""));

    // Values as written by Windows in Explorer\StartupApproved\Run when the
    // user flips the toggle in Settings → Apps → Startup or Task Manager.
    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)] // enabled
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)] // enabled
    [InlineData(new byte[] { 0x03, 0x8E, 0x1C, 0x6A, 0, 0, 0, 0, 0, 0, 0, 0 }, true)] // disabled (+ timestamp)
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]  // disabled
    [InlineData(new byte[0], false)]
    public void IsDisabledState_ReadsTheToggleBit(byte[] state, bool disabled)
        => Assert.Equal(disabled, StartupManager.IsDisabledState(state));
}
