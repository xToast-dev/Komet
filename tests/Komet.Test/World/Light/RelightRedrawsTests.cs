using Komet.World;

namespace Komet.Test.World;

// A lighting task is dropped only when it would relight nothing: no emitting block before or after, the same absorption
public sealed class RelightRedrawsTests
{
    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-relightredraws");
        RelightRedraws.Install(harmony);
        Assert.That(RelightRedraws.Installed, Is.True);
    }

    [Test]
    public void OnlyALightlessChangeIsDropped()
    {
        var pos = new BlockPos(1, 2, 3, 0);
        Assert.Multiple(() =>
        {
            Assert.That(RelightRedraws.Unlit(Made(0, 1), Made(0, 1), null!, pos), Is.True, "snow on grass");
            Assert.That(RelightRedraws.Unlit(Made(14, 0), Made(0, 0), null!, pos), Is.False, "a torch taken");
            Assert.That(RelightRedraws.Unlit(Made(0, 0), Made(7, 0), null!, pos), Is.False, "a lantern placed");
            Assert.That(RelightRedraws.Unlit(Made(0, 0), Made(0, 99), null!, pos), Is.False, "air to rock");
        });
    }

    // HSV value is the third byte
    private static Block Made(int value, int absorption) => new() { LightHsv = new ThreeBytes(value << 16), LightAbsorption = absorption };
}
