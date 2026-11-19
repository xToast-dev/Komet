using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// PotInFirepitRenderer skips only the draws of a pot whose block cell, widened by the lid and the shader's warps, lies wholly
// outside the Opaque stage's clip volume, and a skipped draw leaves face culling off and blending on in the standard mode, as the
// engine's drawn one does. Everything it cannot judge is left to the engine.
[NonParallelizable]
public sealed class PotCullingTests
{
    private readonly List<string> _state = [];
    private TestHarmony _harmony = null!;

    [SetUp]
    public void Install()
    {
        _harmony = new TestHarmony("komet-test-potculling");
        PotCulling.Install(_harmony, new QuietLogger());
    }

    [TearDown]
    public void Restore()
    {
        _harmony.Dispose();
        PotCulling.Enabled = true;
        Counting.Hud = false;
    }

    // In front of the camera and beside it, just past the edge, it draws; behind it, and far to the side, it does not
    [Test]
    public void CullsOnlyWhatCannotReachTheView()
    {
        var client = Client();
        Assert.Multiple(() =>
        {
            Assert.That(PotCulling.Frame(new BlockPos(0, 0, -10), client, EnumRenderStage.Opaque), Is.True, "ahead");
            Assert.That(PotCulling.Frame(new BlockPos(0, 0, 40), client, EnumRenderStage.Opaque), Is.False, "behind");
            Assert.That(PotCulling.Frame(new BlockPos(0, 0, 0), client, EnumRenderStage.Opaque), Is.True,
                "at the camera");
            Assert.That(PotCulling.Frame(new BlockPos(1, 0, 1), client, EnumRenderStage.Opaque), Is.True,
                "its cell behind, the lid's reach in front of the near plane's corner");
            Assert.That(PotCulling.Frame(new BlockPos(200, 0, -10), client, EnumRenderStage.Opaque), Is.False,
                "far aside");
        });
    }

    [Test]
    public void EverythingItCannotJudgeIsLeftToTheEngine()
    {
        var (client, behind) = (Client(), new BlockPos(0, 0, 40));
        Assert.Multiple(() =>
        {
            Assert.That(PotCulling.Frame(behind, client, EnumRenderStage.OIT), Is.True, "another stage");
            Assert.That(PotCulling.Frame(null!, client, EnumRenderStage.Opaque), Is.True, "no position");
            Assert.That(PotCulling.Frame(behind, null!, EnumRenderStage.Opaque), Is.True, "no client");
            PotCulling.Enabled = false;
            Assert.That(PotCulling.Frame(behind, client, EnumRenderStage.Opaque), Is.True, "switched off");
        });
    }

    private ICoreClientAPI Client() => AnimatableCullingTests.StateClient(_state);
}
