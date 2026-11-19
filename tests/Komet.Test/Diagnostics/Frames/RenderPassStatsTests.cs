using Komet.Diagnostics;
using static Komet.Testing.Profiles;

namespace Komet.Test.Diagnostics;

// The per-pass worst is the worst value each pass ever reached, possibly in a different frame each. Asking "what was
// the 79 ms spike" needs the one worst frame kept whole, which is what these tests pin down.
public sealed class RenderPassStatsTests
{
    // The worst steady frame with its own marks, dearest first, and the dearest one's ms
    private static IEnumerable<TestCaseData> WorstFrames()
    {
        static TestCaseData Case(string name, double worstMs, string[] marks, double firstMs,
            params (ProfileEntryRange Frame, bool Steady)[] frames) =>
            new TestCaseData(frames, worstMs, marks, firstMs).SetName(name);

        // The point of the whole feature: the spike's own marks, not the largest mark of any frame
        yield return Case("WorstFrameKeepsItsOwnMarksNotTheLargestEverSeen", 79,
            ["packethandler", "chunkupload", ""], 60, (Frame(20, ("chunkupload", 18)), true),
            (Frame(79, ("packethandler", 60), ("chunkupload", 2)), true));
        yield return Case("MarksAreRankedByCostWithinTheWorstFrame", 79, ["largest", "middle", "small"], 50,
            (Frame(79, ("small", 1), ("largest", 50), ("middle", 20)), true));
        // A load stall or the pause gap would otherwise own the worst frame for the rest of the session
        yield return Case("UnsteadyFramesNeverBecomeTheWorstFrame", 12, ["beginrenderstage-Opaque", "", ""], 4,
            (Frame(12, ("beginrenderstage-Opaque", 4)), true), (Frame(1000, ("chunkupload", 900)), false));
        // Nothing to blame, which is itself the finding
        yield return Case("AFrameWithoutMarksIsStillCountedAsWorst", 79, ["", "", ""], double.NaN,
            (Frame(5, ("beginrenderstage-Opaque", 2)), true), (Frame(79), true));
    }

    [TestCaseSource(nameof(WorstFrames))]
    public void TheWorstFrameIsKeptWhole((ProfileEntryRange Frame, bool Steady)[] frames, double worstMs,
        string[] marks, double firstMs)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var stats = new RenderPassStats();
        foreach (var (frame, steady) in frames)
        {
            stats.AddFrame(frame, steady);
        }

        Assert.Multiple(() =>
        {
            Assert.That(stats.WorstFrameMs, Is.EqualTo(worstMs).Within(0.5));
            Assert.That(Enumerable.Range(0, RenderPassStats.DetailCount).Select(stats.WorstMarkName),
                Is.EqualTo(marks));
            Assert.That(stats.WorstMarkMs(0), double.IsNaN(firstMs) ? Is.NaN : Is.EqualTo(firstMs).Within(0.5));
        });
    }

    private static FrameRecord Record(double dtMs, ProfileEntryRange? root = null, double outsideMs = 0,
        double gcMs = 0, double jitMs = 0, double runQueueMs = 0) =>
        new(0, dtMs, gcMs, outsideMs, jitMs, runQueueMs, root);

    private static SpikeLedger Settled(double ms = 10, int frames = 500)
    {
        var ledger = new SpikeLedger();
        for (var i = 0; i < frames; i++)
        {
            _ = ledger.Add(Record(ms), [], true, i);
        }

        return ledger;
    }

    [TestCase(25, false)]
    [TestCase(26, true)]
    public void SpikesStartAt25MsOverAFastMean(double ms, bool spike)
    {
        var ledger = Settled();
        Assert.That(ledger.Add(Record(ms), [], true, 0), Is.EqualTo(spike), $"threshold {ledger.ThresholdMs:F1} ms");
    }

    // The collector, JIT and the scheduler stop the thread inside some mark; each wins when it covers half the excess over the mean
    [TestCase(20, 0, 0, 0, SpikeLedger.Gc)]
    public void TheDominantPartOfTheFrameIsItsCause(double gcMs, double jitMs, double runQueueMs, double outsideMs,
        string cause)
    {
        var ledger = Settled();
        var top = outsideMs > 30
            ? [(SpikeLedger.Outside, outsideMs), ("rend3D-ret-op", 10.0)]
            : new (string? Name, double Ms)[] { ("rend3D-ret-op", 30.0) };
        _ = ledger.Add(Record(50, outsideMs: outsideMs, gcMs: gcMs, jitMs: jitMs, runQueueMs: runQueueMs), top, true,
            7);
        ledger.Rank();
        Assert.Multiple(() =>
        {
            Assert.That(ledger.Ranked(0).Name, Is.EqualTo(cause));
            Assert.That(ledger.Latest(0).Cause, Is.EqualTo(cause));
            Assert.That(ledger.Latest(0).AtSeconds, Is.EqualTo(7));
        });
    }
}
