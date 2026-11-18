using Komet.Chunks;

namespace Komet.Test.Chunks;

public sealed class JoinPacerTests
{
    [Test]
    public void AChunkFloodWithGcPressureCanSlowToOneSendUnit()
    {
        var scale = JoinPacer.NextScale(0.125, 0.172, 13234);
        Assert.That(Math.Round(192 * scale), Is.EqualTo(1));
        Assert.That(JoinPacer.NextScale(scale, 0.17, 13234), Is.EqualTo(scale));
        Assert.That(JoinPacer.NextScale(scale, 0.01, 500), Is.GreaterThan(scale));
    }
}
