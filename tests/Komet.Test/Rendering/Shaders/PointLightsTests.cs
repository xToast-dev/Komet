using Komet.Rendering;

namespace Komet.Test.Rendering;

// The shipped include takes both edits once, in either line ending; anything else is left alone
public sealed class PointLightsTests
{
    private static string Shipped() => File.ReadAllText(Path.Join(GameInstall.Assets, "game", "shaderincludes", PointLights.File));

    [Test]
    public void TheShippedIncludeTakesBothEdits()
    {
        foreach (var code in new[] { Shipped(), Shipped().Replace("\r\n", "\n", StringComparison.Ordinal) })
        {
            var edited = PointLights.Edit(code);
            Assert.That(edited, Is.Not.Null, "fogandlight.vsh changed");
            Assert.That(edited, Does.Contain("length(plc)").And.Contain("kometReach").And.Not.Contain("plc.r / color.r"));
            Assert.That(PointLights.Edit(edited!), Is.Null, "edited twice");
        }

        Assert.That(PointLights.Edit("void main() {}"), Is.Null, "another include");
    }
}
