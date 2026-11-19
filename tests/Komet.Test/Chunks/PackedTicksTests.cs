using Komet.Chunks;

namespace Komet.Test.Chunks;

// Random block ticks skip a chunk only when neither its block nor its fluid palette holds a block that can take one, read
// from the packed bytes (palette alone) or from the unpacked layers
public sealed class PackedTicksTests
{
    private const int Cells = 32 * 32 * 32;

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
