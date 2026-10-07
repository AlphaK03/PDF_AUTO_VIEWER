using System.Collections.Concurrent;
using PdfAutoViewer.Core;
using Xunit;

namespace PdfAutoViewer.Tests;

/// <summary>
/// Verifies which version of a document ends up on screen.
///
/// Each scenario simulates the real lifecycle without UI or disk: every file
/// is tracked when "detected", asks for its turn when "downloaded", and a
/// window that is told to close releases itself — exactly what
/// PdfLifecycleManager does. The result is checked against an independent
/// oracle for EVERY combination of the six variants
///   S / Sd / E / Ed / N / Nd   (SPA / ENG / untagged × native / _docx)
/// with and without a duplicate copy, for every language preference, and for
/// three arrival patterns: all at once, one by one, and random overlap.
/// </summary>
public class DocumentCoordinatorTests
{
    private static readonly string[] Codes = ["S", "Sd", "E", "Ed", "N", "Nd"];

    // ── Exhaustive scenarios ──────────────────────────────────────────────

    public enum Arrival { AllAtOnce, OneByOne, RandomOverlap }

    [Theory]
    [InlineData("SPA", Arrival.AllAtOnce)]
    [InlineData("SPA", Arrival.OneByOne)]
    [InlineData("SPA", Arrival.RandomOverlap)]
    [InlineData("ENG", Arrival.AllAtOnce)]
    [InlineData("ENG", Arrival.OneByOne)]
    [InlineData("ENG", Arrival.RandomOverlap)]
    [InlineData("",    Arrival.AllAtOnce)]
    [InlineData("",    Arrival.OneByOne)]
    [InlineData("",    Arrival.RandomOverlap)]
    public void EveryCombination_LeavesExactlyTheBestVersionOpen(string preferred, Arrival arrival)
    {
        var rng = new Random(12345);

        for (int mask = 1; mask < 1 << Codes.Length; mask++)
        {
            var variants = Codes.Where((_, i) => (mask & (1 << i)) != 0).ToList();

            foreach (bool withCopy in new[] { false, true })
            {
                var files = variants.Select(c => (Code: c, Copy: 0)).ToList();
                if (withCopy)
                    files.Add((variants[rng.Next(variants.Count)], 1));

                for (int run = 0; run < 3; run++)
                {
                    var order = files.OrderBy(_ => rng.Next()).ToList();
                    var open  = Simulate(order, preferred, arrival, rng);
                    AssertBestOpen(order, open, preferred,
                        $"pref='{preferred}' {arrival} files=[{string.Join(", ", order.Select(Describe))}]");
                }
            }
        }
    }

    // ── Specific behaviors ────────────────────────────────────────────────

    [Fact]
    public void Ranking_PreferredLanguageFirst_ThenDocx_ThenUntagged_ThenOther()
    {
        var c = new DocumentCoordinator(() => "SPA", groupingMs: 0);
        string Rank(string code) => DocumentCoordinator.Rank(c.Track(PathFor(code, 0)), "SPA").ToString();

        // Sd > S > Nd > N > Ed > E
        var ranks = new[] { "Sd", "S", "Nd", "N", "Ed", "E" }.Select(Rank).Select(int.Parse).ToList();
        Assert.Equal(ranks.OrderByDescending(r => r).ToList(), ranks);
        Assert.Equal(ranks.Count, ranks.Distinct().Count());
    }

    [Fact]
    public async Task BetterVersionStillDownloading_WorseOneWaits_ThenIsDiscardedWhenBetterOpens()
    {
        var c   = new DocumentCoordinator(() => "SPA", groupingMs: 0);
        var spa = c.Track(PathFor("S", 0));   // detected, still downloading
        var eng = c.Track(PathFor("E", 0));   // downloaded first

        var engTurn = Task.Run(() => c.WaitForTurn(eng, CancellationToken.None));
        Assert.False(await Completes(engTurn, 300));   // waits: never opens the wrong language

        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(spa, CancellationToken.None));
        Assert.True(await Completes(engTurn, 2000));
        Assert.Equal(DocumentCoordinator.Decision.Discard, await engTurn);
    }

    [Fact]
    public async Task BetterVersionVanishes_WorseOneOpensInstead()
    {
        // e.g. the browser's 0-byte placeholder of the preferred language
        // disappears, or that download is cancelled.
        var c   = new DocumentCoordinator(() => "SPA", groupingMs: 0);
        var spa = c.Track(PathFor("S", 0));
        var eng = c.Track(PathFor("E", 0));

        var engTurn = Task.Run(() => c.WaitForTurn(eng, CancellationToken.None));
        Assert.False(await Completes(engTurn, 300));

        c.Release(spa);                                 // the better one never completed
        Assert.True(await Completes(engTurn, 2000));
        Assert.Equal(DocumentCoordinator.Decision.Open, await engTurn);
    }

    [Fact]
    public async Task VersionsArrivingTogether_WorseOneNeverOpens_EvenIfDetectedFirst()
    {
        // Both languages downloaded with one click: ENG is detected and fully
        // written a moment before SPA is even detected. Thanks to the grouping
        // margin, ENG must never open (no wasted viewer window).
        var c   = new DocumentCoordinator(() => "SPA", groupingMs: 300);
        var eng = c.Track(PathFor("E", 0));
        var engTurn = Task.Run(() => c.WaitForTurn(eng, CancellationToken.None));

        await Task.Delay(100);                          // SPA detected 100 ms later
        var spa = c.Track(PathFor("S", 0));
        var spaTurn = Task.Run(() => c.WaitForTurn(spa, CancellationToken.None));

        Assert.Equal(DocumentCoordinator.Decision.Discard, await engTurn);
        Assert.Equal(DocumentCoordinator.Decision.Open,    await spaTurn);
    }

    private static async Task<bool> Completes(Task task, int timeoutMs) =>
        await Task.WhenAny(task, Task.Delay(timeoutMs)) == task;

    [Fact]
    public void BetterVersionArrivesLater_ReplacesTheOpenOne()
    {
        var c   = new DocumentCoordinator(() => "SPA", groupingMs: 0);
        var eng = c.Track(PathFor("E", 0));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(eng, CancellationToken.None));

        var spa = c.Track(PathFor("Sd", 0));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(spa, CancellationToken.None));
        Assert.True(eng.CloseSignal.IsCancellationRequested);
        Assert.False(spa.CloseSignal.IsCancellationRequested);
    }

    [Fact]
    public void NewerCopyOfTheSameVersion_ReplacesTheOpenOne()
    {
        var c    = new DocumentCoordinator(() => "SPA", groupingMs: 0);
        var old  = c.Track(PathFor("S", 0));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(old, CancellationToken.None));

        var copy = c.Track(PathFor("S", 1));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(copy, CancellationToken.None));
        Assert.True(old.CloseSignal.IsCancellationRequested);
    }

    [Fact]
    public void PreferenceChangedWhileBothLanguagesOpen_ReconcileClosesTheOtherLanguage()
    {
        string preferred = "";                               // Any: one per language
        var c   = new DocumentCoordinator(() => preferred, groupingMs: 0);
        var spa = c.Track(PathFor("S", 0));
        var eng = c.Track(PathFor("E", 0));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(spa, CancellationToken.None));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(eng, CancellationToken.None));

        preferred = "ENG";                                   // user switches language
        c.Reconcile();

        Assert.True(spa.CloseSignal.IsCancellationRequested);
        Assert.False(eng.CloseSignal.IsCancellationRequested);
    }

    [Fact]
    public void DifferentDocuments_DoNotAffectEachOther()
    {
        var c = new DocumentCoordinator(() => "SPA", groupingMs: 0);
        var a = c.Track(@"C:\D\DocA_ENG.pdf");
        var b = c.Track(@"C:\D\DocB_SPA.pdf");

        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(a, CancellationToken.None));
        Assert.Equal(DocumentCoordinator.Decision.Open, c.WaitForTurn(b, CancellationToken.None));
        Assert.False(a.CloseSignal.IsCancellationRequested);
    }

    // ── Simulation ────────────────────────────────────────────────────────

    // Runs the files through the coordinator like PdfLifecycleManager does and
    // returns the entries left open at the end.
    private static List<DocumentCoordinator.Entry> Simulate(
        List<(string Code, int Copy)> order, string preferred, Arrival arrival, Random rng)
    {
        var c    = new DocumentCoordinator(() => preferred, groupingMs: 0);
        var open = new ConcurrentDictionary<DocumentCoordinator.Entry, byte>();

        void Decide(DocumentCoordinator.Entry e)
        {
            if (c.WaitForTurn(e, CancellationToken.None) == DocumentCoordinator.Decision.Discard)
            {
                c.Release(e);
                return;
            }
            open[e] = 0;
            // The viewer closes when told to; its lifecycle then releases it.
            e.CloseSignal.Token.Register(() => { open.TryRemove(e, out _); c.Release(e); });
        }

        switch (arrival)
        {
            case Arrival.OneByOne:
                foreach (var f in order) Decide(c.Track(PathFor(f.Code, f.Copy)));
                break;

            case Arrival.AllAtOnce:
            {
                var entries = order.Select(f => c.Track(PathFor(f.Code, f.Copy))).ToList();
                RunConcurrently(entries.OrderBy(_ => rng.Next()).Select(e => (Action)(() => Decide(e))));
                break;
            }

            case Arrival.RandomOverlap:
            {
                // Each file is detected in order, then finishes "downloading"
                // after a random delay, so detections and decisions interleave.
                var actions = new List<Action>();
                foreach (var f in order)
                {
                    var e = c.Track(PathFor(f.Code, f.Copy));
                    // SpinWait, not Sleep: Sleep(1) lasts ~15 ms on Windows,
                    // too coarse to interleave and needlessly slow.
                    int delay = rng.Next(0, 200_000);
                    actions.Add(() => { Thread.SpinWait(delay); Decide(e); });
                    Thread.SpinWait(rng.Next(0, 50_000));
                }
                RunConcurrently(actions);
                break;
            }
        }

        return open.Keys.ToList();
    }

    // One dedicated thread each, like the real lifecycle (LongRunning tasks).
    private static void RunConcurrently(IEnumerable<Action> actions)
    {
        var tasks = actions.Select(a => Task.Factory.StartNew(a, TaskCreationOptions.LongRunning)).ToArray();
        Assert.True(Task.WaitAll(tasks, TimeSpan.FromSeconds(10)), "Deadlock: a decision never completed");
    }

    // ── Oracle ────────────────────────────────────────────────────────────

    private static void AssertBestOpen(List<(string Code, int Copy)> order,
                                       List<DocumentCoordinator.Entry> open,
                                       string preferred, string scenario)
    {
        // Group: whole document when a language is preferred; otherwise per language.
        var expected = order
            .Select((f, arrivalIndex) => (f.Code, f.Copy, Arrival: arrivalIndex))
            .GroupBy(f => preferred.Length > 0 ? "doc" : Lang(f.Code))
            .Select(g => g.OrderByDescending(f => OracleRank(f.Code, preferred))
                          .ThenByDescending(f => f.Arrival)
                          .First())
            .Select(f => PathFor(f.Code, f.Copy))
            .OrderBy(p => p)
            .ToList();

        var actual = open.Select(e => e.Path).OrderBy(p => p).ToList();
        Assert.True(expected.SequenceEqual(actual),
            $"{scenario}\n  expected open: {string.Join(" | ", expected.Select(Path.GetFileName))}" +
            $"\n  actually open: {string.Join(" | ", actual.Select(Path.GetFileName))}");
    }

    private static int OracleRank(string code, string preferred)
    {
        string lang = Lang(code);
        int l = preferred.Length == 0 ? 0 : lang == preferred ? 2 : lang == "" ? 1 : 0;
        return l * 2 + (code.EndsWith('d') ? 1 : 0);
    }

    private static string Lang(string code) => code[0] switch { 'S' => "SPA", 'E' => "ENG", _ => "" };

    // Real-world naming: native SPA "…_SPA_MPI Test", native ENG "…_ENG MPI Test",
    // docx-derived "…_SPA_MPI_Test_docx", plus the browser copy suffix " (n)".
    private static string PathFor(string code, int copy)
    {
        string lang = Lang(code);
        string stem = code.EndsWith('d')
            ? (lang.Length > 0 ? $"D1_H_{lang}" : "D1_H") + "_MPI_Test_docx"
            : lang switch { "SPA" => "D1_H_SPA_MPI Test", "ENG" => "D1_H_ENG MPI Test", _ => "D1_H_MPI Test" };
        if (copy > 0) stem += $" ({copy})";
        return $@"C:\Downloads\{stem}.pdf";
    }

    private static string Describe((string Code, int Copy) f) => f.Copy > 0 ? $"{f.Code}+{f.Copy}" : f.Code;
}
