using Komet.Diagnostics;
using static Komet.Test.Diagnostics.DiagnosticsTests;

namespace Komet.Test.Diagnostics;

public sealed class RenderCostTests
{
    private static readonly string[] Stages =
        ["OnRenderBefore", "RenderShadow", "OnBeforeRenderOpaque", "RenderOpaque", "RenderOIT", "RenderAfterOIT"];

    [TearDown]
    public void SwitchOff() => RenderCost.Clear();

    private static MethodInfo RenderMesh() =>
        AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderMesh),
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);

    [Test]
    public void TheFirstSwitchOnPatchesEveryTerrainCall()
    {
        using var harmony = new TestHarmony("komet-test-rendercost-on");
        RenderCost.Install(harmony, new QuietLogger());
        RenderCost.Enabled = true;
        Assert.That(RenderCost.Patched, Is.True);
        using (Assert.EnterMultipleScope())
        {
            foreach (var stage in Stages)
            {
                var method = AccessTools.DeclaredMethod(typeof(ChunkRenderer), stage, [typeof(float)]);
                Assert.That(method, Is.Not.Null, $"ChunkRenderer.{stage} is gone");
                Assert.That(PatchedBy(method!, harmony), Is.True, stage);
            }

            Assert.That(PatchedBy(RenderMesh(), harmony), Is.True, "RenderMesh");
        }
    }

}
