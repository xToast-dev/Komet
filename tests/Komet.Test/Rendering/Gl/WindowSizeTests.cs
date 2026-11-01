using Komet.Rendering;

namespace Komet.Test.Rendering;

// Every frame method that reads the window size is found and rewritten
[NonParallelizable]
public sealed class WindowSizeTests
{
    [Test]
    public void EveryFrameMethodIsRewritten()
    {
        using var harmony = new TestHarmony("komet-test-windowsize");
        WindowSize.Install(harmony);
        Assert.That(WindowSize.Rewritten, Is.True);
    }
}
