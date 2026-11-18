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
        Assert.That(typeof(WeatherSimulationSnowAccum).Assembly,
            Is.Not.Null); // Komet finds it by name once VSEssentials is loaded
        using var harmony = new TestHarmony("komet-test-snowpeek");
        SnowPeek.Install(harmony);
        Assert.That(SnowPeek.Rewritten, Is.True,
            "UpdateSnowLayer no longer reads its blocks through four GetLocalBlockAtBlockPos");
    }

    // A packed chunk's blocks and fluids read without unpacking as the engine's own ChunkData reads them, for palettes of
    // one to eight bits
    [TestCase(1)]
    [TestCase(40)]
    [TestCase(300)]
    public void APackedChunkReadsAsTheEngineReadsIt(int kinds)
    {
        using var harmony = new TestHarmony("komet-test-snowpeek");
        SnowPeek.Install(harmony);
        const int fluids = 400;
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.Blocks = [.. Enumerable.Range(0, fluids + 3).Select(id => new Block { BlockId = id })];
        var chunk = ClientChunk.CreateNew(new ClientChunkDataPool(32, null!));
        var data = (ChunkData)chunk.Data;
        var random = new Random(kinds);
        for (var i = 0; i < 32768; i++)
        {
            if (random.Next(3) > 0) data.SetBlockUnsafe(i, 1 + random.Next(kinds));

            if (random.Next(8) == 0) data.SetFluid(i, fluids + random.Next(3));
        }

        var expected = Enumerable.Range(0, 32768 / 7 + 1)
            .Select(n => (data.GetBlockId(7 * n, 0), data.GetBlockId(7 * n, 2))).ToArray();
        chunk.lastReadOrWrite = Environment.TickCount - 60_000;
        chunk.TryPackAndCommit(0);
        Assert.That(chunk.IsPacked(), Is.True, "precondition");
        for (var i = 0; i < 32768; i += 7)
        {
            var (x, y, z) = (i % 32, i / 1024, i / 32 % 32);
            Assert.That(SnowPeek.Read(chunk, game, x, y, z, 0).BlockId, Is.EqualTo(expected[i / 7].Item1),
                $"layer 0 at {i}");
            Assert.That(SnowPeek.Read(chunk, game, x, y, z, 2).BlockId, Is.EqualTo(expected[i / 7].Item2),
                $"fluid at {i}");
        }

        Assert.That(chunk.IsPacked(), Is.True, "read without unpacking");
    }
}
