using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// The session activity log must never accumulate: the app can run for days.
/// </summary>
public class ActivityLogTests
{
    [Fact]
    public void Add_BeyondTheLimit_KeepsOnlyTheMostRecentEntries()
    {
        var log = new ActivityLog();
        for (int i = 0; i < ActivityLog.MaxEntries + 50; i++)
            log.Add($@"C:\D\doc{i}.pdf", ActivityKind.Opened);

        var entries = log.Snapshot();
        Assert.Equal(ActivityLog.MaxEntries, entries.Count);
        Assert.Equal("doc50.pdf", entries[0].File);                                  // oldest dropped
        Assert.Equal($"doc{ActivityLog.MaxEntries + 49}.pdf", entries[^1].File);     // newest kept
    }

    [Fact]
    public void Clear_EmptiesTheLogAndChangesItsVersion()
    {
        var log = new ActivityLog();
        log.Add(@"C:\D\a.pdf", ActivityKind.Downloaded);
        long before = log.Version;

        log.Clear();

        Assert.Empty(log.Snapshot());
        Assert.NotEqual(before, log.Version);
    }
}
