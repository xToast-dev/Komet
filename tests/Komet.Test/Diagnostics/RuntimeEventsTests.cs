using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

// The runtime's allocation samples reach the capture with the type and the allocating thread's group, and the stability
// figures count a frame's step against its predecessor
[NonParallelizable]
public sealed class RuntimeEventsTests
{
    private static byte[]? _kept;

    [Test]
    public void AllocationsArriveWithTheirType()
    {
        var events = RuntimeEvents.Start();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (events.Sampled < 4 << 20 && waited.Elapsed < TimeSpan.FromSeconds(10))
            for (var i = 0; i < 64; i++) _kept = new byte[64 << 10];
        var snapshot = events.Stop();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Sampled, Is.GreaterThan(1 << 20));
            Assert.That(snapshot.Types.Select(t => t.Type), Does.Contain("System.Byte[]"));
            Assert.That(snapshot.Threads.Select(t => t.Thread), Does.Contain("main"), "the test's own thread counts as main");
        });
        Assert.That(_kept, Is.Not.Null);
    }

    [Test]
    public void AStepOverTwiceItsPredecessorIsAHitch()
    {
        DebugFrame[] frames = [Frame(6), Frame(6), Frame(18), Frame(6), Frame(9), Frame(6)];
        var (jump95, _, hitches) = DebugFrames.Steps(frames);
        Assert.Multiple(() =>
        {
            Assert.That(hitches, Is.EqualTo(1), "6 → 18 alone: 6 → 9 is not twice, 18 → 6 a drop");
            Assert.That(jump95, Is.EqualTo(12).Within(0.01));
        });
    }

    private static DebugFrame Frame(float ms) => new(0, ms, 0, 0, 0, 0, 0, 0, 0, true);
}
