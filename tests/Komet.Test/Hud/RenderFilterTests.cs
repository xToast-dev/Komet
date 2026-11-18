using Komet.Hud;

namespace Komet.Test.Hud;

public sealed class RenderFilterTests
{
    [Test]
    public void SearchModStageAndErrorFiltersCombine()
    {
        var filter = new RenderFilter { Query = "FOG", Mod = "shaders", Stage = "Opaque", ErrorsOnly = true };
        Assert.That(filter.Matches("other\nshaders", "Opaque", true, "shaderincludes/fog.fsh"), Is.True);
        Assert.That(filter.Matches("other", "Opaque", true, "fog"), Is.False);
        Assert.That(filter.Matches("shaders", "OIT", true, "fog"), Is.False);
        Assert.That(filter.Matches("shaders", "Opaque", false, "fog"), Is.False);
        Assert.That(filter.Matches("shaders", "Opaque", true, "lighting"), Is.False);
        Assert.That(filter.Matches("shaders", "", true, "fog"), Is.False);
        filter.Clear();
        Assert.That(filter.Matches("game", "", false, "anything"), Is.True);
    }
}
