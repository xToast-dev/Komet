using Komet.Chunks;

namespace Komet.Test.Chunks;

// Random block ticks skip a chunk only when neither its block nor its fluid palette holds a block that can take one, read
// from the packed bytes (palette alone) or from the unpacked layers
public sealed class PackedTicksTests
{
    private const int Cells = 32 * 32 * 32;

    [Test]
    public void OnlyChunksWithoutTickingBlocksAreQuiet()
    {
        var snowy = new Block { BlockId = 3 };
        snowy.snowCovered1 = snowy;
        Block[] blocks = [new() { BlockId = 0 }, new() { BlockId = 1 }, new Growing { BlockId = 2 }, snowy];
        Assert.That(PackedTicks.Ready(blocks), Is.True);

        var rock = Chunk(data => data[5] = 1, true);
        var grass = Chunk(data => data[5] = 2, true);
        var water = Chunk(data => data.SetFluid(7, 2), true);
        var snow = Chunk(data => data[9] = 3, true);
        var looseRock = Chunk(data => data[5] = 1, false);
        var looseGrass = Chunk(data => data[5] = 2, false);
        Assert.Multiple(() =>
        {
            Assert.That(rock.IsPacked() && grass.IsPacked() && !looseRock.IsPacked(), Is.True);
            Assert.That(PackedTicks.Quiet(rock), Is.True, "packed rock");
            Assert.That(PackedTicks.Quiet(rock), Is.True, "packed rock, kept verdict");
            Assert.That(PackedTicks.Quiet(grass), Is.False, "packed block that ticks");
            Assert.That(PackedTicks.Quiet(water), Is.False, "packed fluid that ticks");
            Assert.That(PackedTicks.Quiet(snow), Is.False, "its own snow cover");
            Assert.That(PackedTicks.Quiet(looseRock), Is.True, "unpacked rock");
            Assert.That(PackedTicks.Quiet(looseGrass), Is.False, "unpacked block that ticks");
            Assert.That(rock.IsPacked(), Is.True, "read without unpacking");
        });
    }

    // The tick loop's one chunk MarkFresh becomes Fresh, the map chunk's stays; tickChunk is there to skip
    [Test]
    public void TheEnginesLoopIsRewritten()
    {
        var seams = PackedTicks.Seams();
        Assert.That(seams, Has.All.Not.Null);
        var code = PackedTicks.FreshIl(PatchProcessor.GetOriginalInstructions(seams[1]!));
        var fresh = AccessTools.Method(typeof(PackedTicks), nameof(PackedTicks.Fresh));
        Assert.Multiple(() =>
        {
            Assert.That(PackedTicks.Rewritten, Is.True);
            Assert.That(code.Count(c => c.Calls(fresh)), Is.EqualTo(1));
            Assert.That(code.Count(c => c.operand is MethodInfo { Name: "MarkFresh" }), Is.EqualTo(1),
                "the map chunk's");
        });
    }

    private static ClientChunk Chunk(Action<ChunkData> fill, bool pack)
    {
        var chunk = ClientChunk.CreateNew(new ClientChunkDataPool(32, null!));
        var data = (ChunkData)chunk.Data;
        for (var i = 0; i < Cells; i += 97)
        {
            data[i] = 1; // rock all through: a palette of more than one entry
        }

        fill(data);
        if (!pack) return chunk;

        chunk.lastReadOrWrite = Environment.TickCount - 60_000;
        chunk.TryPackAndCommit(0);
        return chunk;
    }

    private sealed class Growing : Block
    {
        public override bool ShouldReceiveServerGameTicks(IWorldAccessor world, BlockPos pos, Random offThreadRandom,
            out object extra)
        {
            extra = null!;
            return true;
        }
    }
}
