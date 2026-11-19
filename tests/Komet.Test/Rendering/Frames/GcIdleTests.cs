using Komet.Rendering;

namespace Komet.Test.Rendering;

[NonParallelizable]
public sealed class GcIdleTests
{

    [TestCase(16, 0, 8, 8, false)]
    [TestCase(16, 8, 0, 0, false)]
    public void AnEarlyCollectionRequiresTimeAndAllocationHistory(double left, double pause, int filled, int budget,
        bool expected) =>
        Assert.That(GcIdle.Fits(left, pause, filled << 20, budget << 20), Is.EqualTo(expected));
}
