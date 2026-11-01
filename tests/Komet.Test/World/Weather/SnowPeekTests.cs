using Komet.World;
using Vintagestory.GameContent;

namespace Komet.Test.World;

// The snow layer scan's four block reads become Komet's in the engine's own UpdateSnowLayer
[NonParallelizable]
public sealed class SnowPeekTests
{
    [Test]
    public void TheScansReadsAreRewritten()
    {
        Assert.That(typeof(WeatherSimulationSnowAccum).Assembly, Is.Not.Null); // Komet finds it by name once VSEssentials is loaded
        using var harmony = new TestHarmony("komet-test-snowpeek");
        SnowPeek.Install(harmony);
        Assert.That(SnowPeek.Rewritten, Is.True, "UpdateSnowLayer no longer reads its blocks through four GetLocalBlockAtBlockPos");
    }

    // A chunk's blocks and fluids compressed by the engine read back as the engine's own ChunkData reads them, for palettes
    // of one to eight bits
    [TestCase(1)]
    [TestCase(40)]
    [TestCase(300)]
    public void APackedChunkReadsAsTheEngineReadsIt(int kinds)
    {
        using var harmony = new TestHarmony("komet-test-snowpeek");
        SnowPeek.Install(harmony);
        var pool = new ChunkDataPool(32, null!);
        var data = ChunkData.CreateNew(32, pool);
        var random = new Random(kinds);
        for (var i = 0; i < 32768; i++)
        {
            if (random.Next(3) > 0) data.SetBlockUnsafe(i, 1 + random.Next(kinds));
            if (random.Next(8) == 0) data.SetFluid(i, 5000 + random.Next(3));
        }

        object?[] packed = [null, null, null, null, 2];
        _ = typeof(ChunkData).GetMethod("CompressInto", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(data, packed);
        var (blocks, fluids) = ((byte[])packed[0]!, (byte[]?)packed[3]);
        for (var i = 0; i < 32768; i += 7)
        {
            Assert.That(SnowPeek.PeekId(blocks, fluids, i, 0), Is.EqualTo(data.GetBlockId(i, 0)), $"layer 0 at {i}");
            Assert.That(SnowPeek.PeekId(blocks, fluids, i, 2), Is.EqualTo(data.GetBlockId(i, 2)), $"fluid at {i}");
        }
    }
}
