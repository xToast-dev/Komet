using Komet.Api;
using Komet.Rendering;

namespace Komet.Test.Rendering.Shaders;

// A pass's source: Komet's marks first, then the defines of its own mod (a pack and the same mod's code are one mod),
// only those given to every program or named "pass:<id>", then the prelude with the mod's line numbers
public sealed class ModPassesTests
{
    [TearDown]
    public void Clear() => ModShaders.Clear();

    [Test]
    public void APassGetsKometsMarksAndItsModsDefines()
    {
        using var own = KometShaders.Define("myshaders", "MYSHADERS_CODE", () => "1", "pass:haze");
        using var everywhere = KometShaders.Define("shaderpack:myshaders", "MYSHADERS_QUALITY", () => "1");
        using var other = KometShaders.Define("othermod", "OTHER", () => "1");
        var haze = ModPasses.Source(new KometPass("shaderpack:myshaders", "haze", KometPassStage.Final, "void main() {}"));
        var grade = ModPasses.Source(new KometPass("shaderpack:myshaders", "grade", KometPassStage.Final, "void main() {}"));
        Assert.Multiple(() =>
        {
            Assert.That(haze, Does.StartWith("#version 330 core\n#define KOMET 1\n"));
            Assert.That(haze, Does.Contain("#define MYSHADERS_CODE 1").And.Contain("#define MYSHADERS_QUALITY 1"));
            Assert.That(grade, Does.Not.Contain("MYSHADERS_CODE").And.Contain("#define MYSHADERS_QUALITY 1"));
            Assert.That(haze, Does.Not.Contain("OTHER"));
            Assert.That(haze, Does.EndWith("#line 1\nvoid main() {}"));
        });
    }
}
