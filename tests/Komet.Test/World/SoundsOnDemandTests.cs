using Komet.World;

namespace Komet.Test.World;

// LoadSoundsSlow's one OptimizeRamMode read is found in the engine's body
[NonParallelizable]
public sealed class SoundsOnDemandTests
{
    [Test]
    public void TheJoinReadsTheModeOnce()
    {
        using var harmony = new TestHarmony("komet-test-soundsondemand");
        SoundsOnDemand.Install(harmony);
        Assert.That(SoundsOnDemand.Rewritten, Is.True);
    }
}
