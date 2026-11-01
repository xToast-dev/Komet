using Komet.Core;

namespace Komet.Test.Core;

// A packed chunk's view reads what the chunk held before it was packed, the chunk stays packed, the same bytes reuse the
// view, and an unpacked chunk is read where it is
public sealed class ChunkViewsTests
{
    private const int Cells = 32 * 32 * 32;

    [Test]
    public void APackedChunkIsReadAsItWasWritten()
    {
        var chunk = ClientChunk.CreateNew(new ClientChunkDataPool(32, null!));
        var data = (ChunkData)chunk.Data;
        var r = new Random(5);
        for (var i = 0; i < Cells; i++)
        {
            if (i < Cells / 2) data[i] = r.Next(1, 40);
            data.SetLight(i, (uint)r.Next(32));
            if (r.Next(9) == 0) data.SetFluid(i, r.Next(1, 4));
        }

        var (blocks, fluids, light) = (new int[Cells], new int[Cells], new uint[Cells]);
        for (var i = 0; i < Cells; i++) (blocks[i], fluids[i], light[i]) = (data.GetBlockId(i, 1), data.GetFluid(i), Light(data, i));
        chunk.lastReadOrWrite = Environment.TickCount - 60_000;
        chunk.TryPackAndCommit(0);
        Assert.That(chunk.IsPacked(), Is.True);

        var view = ChunkViews.Open(chunk);
        Assert.That(view, Is.Not.Null);
        var wrong = Enumerable.Range(0, Cells)
            .Count(i => view!.GetBlockId(i, 1) != blocks[i] || view.GetFluid(i) != fluids[i] || Light(view, i) != light[i]);
        Assert.Multiple(() =>
        {
            Assert.That(wrong, Is.Zero, "cells read differently from the view");
            Assert.That(chunk.IsPacked(), Is.True, "the chunk stays packed");
            Assert.That(ChunkViews.Open(chunk), Is.SameAs(view), "the same bytes reuse the view");
        });

        _ = chunk.Unpack_ReadOnly();
        Assert.That(ChunkViews.Open(chunk), Is.Null, "an unpacked chunk is read where it is");
    }

    private static uint Light(ChunkData data, int i) => ((uint)data.GetBlocklight(i) << 16) | (uint)data.GetSunlight(i);

    // More chunks than a thread keeps views: every view is reused, and none shares a plane with another
    [Test]
    public void ReusedViewsKeepTheirOwnPlanes()
    {
        var pool = new ClientChunkDataPool(32, null!);
        var r = new Random(9);
        var chunks = new List<(ClientChunk Chunk, int[] Blocks)>();
        for (var c = 0; c < 80; c++)
        {
            var chunk = ClientChunk.CreateNew(pool);
            var data = (ChunkData)chunk.Data;
            var blocks = new int[Cells];
            var kinds = 2 + (c % 40);
            for (var i = 0; i < Cells; i++) data[i] = blocks[i] = r.Next(kinds);
            for (var i = 0; i < Cells; i++) blocks[i] = data.GetBlockId(i, 1);
            chunk.lastReadOrWrite = Environment.TickCount - 60_000;
            chunk.TryPackAndCommit(0);
            chunks.Add((chunk, blocks));
        }

        var wrong = 0;
        for (var round = 0; round < 3; round++)
        {
            var views = chunks.Select(c => ChunkViews.Open(c.Chunk)).ToList();
            // the last views of the round are still the thread's: none was overwritten by a later decode
            for (var c = chunks.Count - 20; c < chunks.Count; c++)
                wrong += Enumerable.Range(0, Cells).Count(i => views[c]!.GetBlockId(i, 1) != chunks[c].Blocks[i]);
        }

        Assert.That(wrong, Is.Zero);
    }
}
