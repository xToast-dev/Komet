using Komet.World;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// The engine's 200 map pieces a tick become Komet's share: the rewrite finds its constant in ChunkMapLayer.OnTick
[NonParallelizable]
public sealed class MapPieceBudgetTests
{
    [TearDown]
    public void Restore() => MapPieceBudget.Enabled = true;

    [Test]
    public void TheTickTakesKometsShare()
    {
        Assert.That(typeof(ChunkMapLayer).Assembly, Is.Not.Null); // Komet finds the layer by name once VSEssentials is loaded
        using var harmony = new TestHarmony("komet-test-mappiecebudget");
        MapPieceBudget.Install(harmony);
        Assert.That(MapPieceBudget.Rewritten, Is.True, "ChunkMapLayer.OnTick has no single ldc.i4 200");
        Assert.That(MapPieceBudget.Pieces(), Is.EqualTo(MapPieceBudget.PerTick));
        MapPieceBudget.Enabled = false;
        Assert.That(MapPieceBudget.Pieces(), Is.EqualTo(200), "switched off: the engine's");
    }
}
