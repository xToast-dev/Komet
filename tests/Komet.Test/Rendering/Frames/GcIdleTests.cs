using Komet.Rendering;

namespace Komet.Test.Rendering;

[NonParallelizable]
public sealed class GcIdleTests
{
    [Test]
    public void UncappedFramesMoveNoCollections()
    {
        GcIdle.Install();
        var before = GcIdle.Moved;
        for (var i = 0; i < 200; i++)
        {
            GcIdle.FrameStart(double.NaN);
        }

        Assert.That(GcIdle.Moved, Is.EqualTo(before));
    }

    [TestCase(16, 0, 8, 8, false)]
    [TestCase(16, 8, 0, 0, false)]
    [TestCase(16, 8, 5, 8, false)]
    [TestCase(16, 14, 8, 8, false)]
    [TestCase(10, 4, 8, 8, false)]
    [TestCase(16, 8, 8, 8, true)]
    public void AnEarlyCollectionRequiresTimeAndAllocationHistory(double left, double pause, int filled, int budget,
        bool expected) =>
        Assert.That(GcIdle.Fits(left, pause, filled << 20, budget << 20), Is.EqualTo(expected));
}
