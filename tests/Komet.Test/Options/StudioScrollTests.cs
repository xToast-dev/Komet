using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class StudioScrollTests
{
    [Test]
    public void WheelReachesTheScrollerUnderAButtonAndStopsAtAPopup()
    {
        var hits = new StudioHits();
        var column = new SwallowRegion();
        hits.Region(0, 0, 100, 100, column);
        hits.Add(10, 10, 50, 20, () => { });
        Assert.That(hits.WheelAt(20, 20), Is.SameAs(column));
        hits.Block(0, 0, 100, 100);
        hits.Add(0, 0, 100, 100, () => { });
        Assert.That(hits.WheelAt(20, 20), Is.Null);
    }

}
