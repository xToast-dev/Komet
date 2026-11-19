using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// A firepit's or forge's contents are skipped only when their cell, widened by the item's reach and the shader's warps, lies
// wholly outside the Opaque clip volume; a skipped draw leaves the state the drawn one leaves. A firepit holding nothing or a
// mod's content renderer, and a forge with neither work item nor fuel, are the engine's.
[NonParallelizable]
public sealed class HearthCullingTests
{
    private static readonly object Mesh = new();
    private readonly List<string> _state = [];
    private TestHarmony _harmony = null!;

    [SetUp]
    public void Install()
    {
        _harmony = new TestHarmony("komet-test-hearthculling");
        HearthCulling.Install(_harmony, new QuietLogger());
    }

    [TearDown]
    public void Restore()
    {
        _harmony.Dispose();
        HearthCulling.Enabled = true;
        Counting.Hud = false;
    }

    [Test]
    public void CullsOnlyWhatCannotReachTheView()
    {
        var (client, ahead, behind) = (Client(), new BlockPos(0, 0, -10), new BlockPos(0, 0, 40));
        var stack = new ItemStack();
        Assert.Multiple(() =>
        {
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, ahead, null, EnumRenderStage.Opaque), Is.True,
                "firepit ahead");
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, behind, null, EnumRenderStage.Opaque), Is.False,
                "firepit behind");
            Assert.That(HearthCulling.ForgeFrame(stack, 0, client, ahead, EnumRenderStage.Opaque), Is.True,
                "forge ahead");
            Assert.That(HearthCulling.ForgeFrame(null, 1, client, behind, EnumRenderStage.Opaque), Is.False,
                "forge behind");
        });
    }

    [Test]
    public void EverythingItCannotJudgeIsLeftToTheEngine()
    {
        var (client, behind) = (Client(), new BlockPos(0, 0, 40));
        Assert.Multiple(() =>
        {
            Assert.That(HearthCulling.FirepitFrame(null, client, behind, null, EnumRenderStage.Opaque), Is.True,
                "empty firepit");
            Assert.That(HearthCulling.FirepitFrame(null, client, behind, new object(), EnumRenderStage.Opaque), Is.True,
                "a mod's content renderer");
            Assert.That(HearthCulling.ForgeFrame(null, 0, client, behind, EnumRenderStage.Opaque), Is.True,
                "cold empty forge");
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, behind, null, EnumRenderStage.OIT), Is.True,
                "another stage");
            HearthCulling.Enabled = false;
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, behind, null, EnumRenderStage.Opaque), Is.True,
                "switched off");
        });
    }

    private ICoreClientAPI Client() => AnimatableCullingTests.StateClient(_state);
}
