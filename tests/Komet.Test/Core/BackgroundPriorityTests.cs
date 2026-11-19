using Komet.Core;

namespace Komet.Test.Core;

// Background work is recognised by its thread names; the frame's threads are not
public sealed class BackgroundPriorityTests
{
    [TestCase("SingleplayerSer", true)]
    [TestCase("komet-worker-3", true)]
    public void ThreadNamesAreTold(string name, bool background) =>
        Assert.That(BackgroundPriority.Background.IsMatch(name), Is.EqualTo(background));
}
