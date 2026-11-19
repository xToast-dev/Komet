using Komet.Core;
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
    public void PassEditsTargetTheirOwnerAndRunInRegistrationOrder()
    {
        var pass = new KapiPass("grade", "original");
        var first = ShaderHub.EditPass("addon", "sheydermod", "grade", source => source + " first");
        using var second = ShaderHub.EditPass("addon", "sheydermod", "grade", source => source + " second");
        Assert.That(ModPasses.Source("shaderpack:sheydermod", pass), Does.EndWith("original first second"));
        Assert.That(ModPasses.Source("other", new KapiPass("grade", "other")),
            Does.EndWith("other"));
        first.Dispose();
        Assert.That(ModPasses.Source("shaderpack:sheydermod", pass), Does.EndWith("original second"));
    }

}
