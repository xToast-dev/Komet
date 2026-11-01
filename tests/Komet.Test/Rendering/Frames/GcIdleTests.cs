using Komet.Rendering;

namespace Komet.Test.Rendering;

// GcIdle times SwapBuffers (its waits are what VSync leaves over) and, with no time to spare, collects nothing
[NonParallelizable]
public sealed class GcIdleTests
{
    [Test]
    public void NoSpareTimeMovesNothing()
    {
        using var harmony = new TestHarmony("komet-test-gcidle");
        GcIdle.Install(harmony);
        Assert.That(GcIdle.Patched, Is.True, "GameWindow.SwapBuffers is not patched");
        var before = GcIdle.Moved;
        for (var i = 0; i < 200; i++) GcIdle.FrameStart(0.5); // half a millisecond left: far less than a pause
        Assert.That(GcIdle.Moved, Is.EqualTo(before));
    }
}
