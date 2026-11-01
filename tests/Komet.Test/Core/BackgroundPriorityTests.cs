using Komet.Core;

namespace Komet.Test.Core;

// Background work is recognised by its thread names; the frame's threads are not
public sealed class BackgroundPriorityTests
{
    [TestCase("SingleplayerSer", true)]
    [TestCase("komet-worker-3", true)]
    [TestCase("chunkdbthread", true)]
    [TestCase("Vintagesto:sh0", true)]
    [TestCase("Vintagestory", false)]
    [TestCase("komet-vulkan-re", false)]
    [TestCase("komet-frame-bri", false)]
    [TestCase("chunkculling", false)]
    public void ThreadNamesAreTold(string name, bool background) =>
        Assert.That(BackgroundPriority.IsBackground(name), Is.EqualTo(background));
}
