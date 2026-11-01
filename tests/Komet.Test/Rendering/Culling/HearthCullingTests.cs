using Komet.Core;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// A firepit's or forge's contents are skipped only when their cell, widened by the item's reach and the shader's warps, lies
// wholly outside the Opaque clip volume; a skipped draw leaves the state the drawn one leaves. A firepit holding nothing or a
// mod's content renderer, and a forge with neither work item nor fuel, are the engine's.
[NonParallelizable]
public sealed class HearthCullingTests
{
    private readonly List<string> _state = [];
    private static readonly object Mesh = new();

    [TearDown]
    public void Restore()
    {
        HearthCulling.Enabled = true;
        Counting.Hud = false;
    }

    [Test]
    public void Installs()
    {
        using var harmony = new TestHarmony("komet-test-hearthculling");
        HearthCulling.Install(harmony, new QuietLogger());
        Assert.Multiple(() =>
        {
            Assert.That(HearthCulling.Matched, Is.True, "the firepit's and forge's OnRenderFrame are not the bodies verified");
            Assert.That(HearthCulling.Blocked, Is.False);
            Assert.That(HearthCulling.Seams().Select(s => Harmony.GetPatchInfo(s)?.Prefixes.Count), Is.EqualTo([1, 1]));
        });
    }

    [Test]
    public void CullsOnlyWhatCannotReachTheView()
    {
        using var harmony = new TestHarmony("komet-test-hearthculling");
        HearthCulling.Install(harmony, new QuietLogger());
        var (client, ahead, behind) = (Client(), new BlockPos(0, 0, -10), new BlockPos(0, 0, 40));
        var stack = new ItemStack();
        Assert.Multiple(() =>
        {
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, ahead, null, EnumRenderStage.Opaque), Is.True, "firepit ahead");
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, behind, null, EnumRenderStage.Opaque), Is.False, "firepit behind");
            Assert.That(HearthCulling.ForgeFrame(stack, 0, client, ahead, EnumRenderStage.Opaque), Is.True, "forge ahead");
            Assert.That(HearthCulling.ForgeFrame(null, 1, client, behind, EnumRenderStage.Opaque), Is.False, "forge behind");
        });
    }

    [Test]
    public void EverythingItCannotJudgeIsLeftToTheEngine()
    {
        using var harmony = new TestHarmony("komet-test-hearthculling");
        HearthCulling.Install(harmony, new QuietLogger());
        var (client, behind) = (Client(), new BlockPos(0, 0, 40));
        Assert.Multiple(() =>
        {
            Assert.That(HearthCulling.FirepitFrame(null, client, behind, null, EnumRenderStage.Opaque), Is.True, "empty firepit");
            Assert.That(HearthCulling.FirepitFrame(null, client, behind, new object(), EnumRenderStage.Opaque), Is.True,
                "a mod's content renderer");
            Assert.That(HearthCulling.ForgeFrame(null, 0, client, behind, EnumRenderStage.Opaque), Is.True, "cold empty forge");
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, behind, null, EnumRenderStage.OIT), Is.True, "another stage");
            HearthCulling.Enabled = false;
            Assert.That(HearthCulling.FirepitFrame(Mesh, client, behind, null, EnumRenderStage.Opaque), Is.True, "switched off");
        });
    }

    // A firepit leaves culling off and standard blending, a forge culling off alone
    [Test]
    public void ASkippedDrawLeavesTheStateADrawnOneLeaves()
    {
        using var harmony = new TestHarmony("komet-test-hearthculling");
        HearthCulling.Install(harmony, new QuietLogger());
        var behind = new BlockPos(0, 0, 40);
        _state.Clear();
        Assert.That(HearthCulling.FirepitFrame(Mesh, Client(), behind, null, EnumRenderStage.Opaque), Is.False);
        Assert.That(_state, Is.EqualTo(["cull off", "blend True Standard"]));
        _state.Clear();
        Assert.That(HearthCulling.ForgeFrame(null, 1, Client(), behind, EnumRenderStage.Opaque), Is.False);
        Assert.That(_state, Is.EqualTo(["cull off"]));
    }

    private ICoreClientAPI Client()
    {
        var culler = AnimatableCullingTests.Culler();
        var render = Answers.Of<IRenderAPI>(new()
        {
            ["get_DefaultFrustumCuller"] = _ => culler, ["get_ShaderUniforms"] = _ => new DefaultShaderUniforms(),
            ["GlToggleBlend"] = a => Record($"blend {a![0]} {a[1]}"), ["GlDisableCullFace"] = _ => Record("cull off"),
            ["GlEnableCullFace"] = _ => Record("cull on")
        });
        return Answers.Of<ICoreClientAPI>(new() { ["get_Render"] = _ => render });
    }

    private object? Record(string call)
    {
        _state.Add(call);
        return null;
    }
}
