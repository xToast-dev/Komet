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
        for (var i = 0; i < Cells; i++)
        {
            (blocks[i], fluids[i], light[i]) = (data.GetBlockId(i, 1), data.GetFluid(i), Light(data, i));
        }

        chunk.lastReadOrWrite = Environment.TickCount - 60_000;
        chunk.TryPackAndCommit(0);
        Assert.That(chunk.IsPacked(), Is.True);

        var view = ChunkViews.Open(chunk);
        Assert.That(view, Is.Not.Null);
        var wrong = Enumerable.Range(0, Cells)
            .Count(i => view!.GetBlockId(i, 1) != blocks[i] || view.GetFluid(i) != fluids[i] ||
                        Light(view, i) != light[i]);
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

}
