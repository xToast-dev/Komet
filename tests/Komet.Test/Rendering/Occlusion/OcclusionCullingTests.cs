using Komet.Rendering;

namespace Komet.Test.Rendering;

// The culling's CPU side: what it patches, the draws IndirectDraw hands it, and the ranges a pool without its boxes goes up as
public sealed class OcclusionCullingTests
{
    [TearDown]
    public void SwitchOff()
    {
        OcclusionCulling.Clear();
        OcclusionCulling.Enabled = false;
    }

    private static MethodBase[] Seams() =>
        [
            AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque), [typeof(float)]),
            AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render))
        ];

    [Test]
    public void InstallPatchesTheOpaqueTerrainAndTakesTheDraws()
    {
        using var harmony = new TestHarmony("komet-test-culling");
        OcclusionCulling.Install(harmony, Answers.NullClient, new QuietLogger());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(OcclusionCulling.Installed, Is.True);
            foreach (var seam in Seams())
            {
                Assert.That(Harmony.GetPatchInfo(seam)?.Owners.Contains(harmony.Id), Is.True, seam.Name);
            }

            Assert.That(IndirectDraw.Intercept, Is.Not.Null);
        }
    }

    [Test]
    public void ClearGivesTheDrawsBack()
    {
        using var harmony = new TestHarmony("komet-test-culling-clear");
        OcclusionCulling.Install(harmony, Answers.NullClient, new QuietLogger());
        OcclusionCulling.Clear();
        Assert.Multiple(() =>
        {
            Assert.That(IndirectDraw.Intercept, Is.Null);
            Assert.That(OcclusionCulling.Installed, Is.False);
        });
    }

}
