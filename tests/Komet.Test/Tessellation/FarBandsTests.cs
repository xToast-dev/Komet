using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// Coarse cells, light, band and the engine seams FarLod hangs on
[NonParallelizable]
public sealed class FarLodTests
{
    private const int Ext = 34;

    private static int At(int x, int y, int z) => (y * Ext + z) * Ext + x;

    [Test]
    public void TheSeamsAreThere()
    {
        var tesselator = typeof(ChunkTesselator);
        Assert.That(AccessTools.DeclaredMethod(tesselator, "BuildExtendedChunkData"), Is.Not.Null);
        Assert.That(AccessTools.DeclaredMethod(tesselator, nameof(ChunkTesselator.BuildBlockPolygons)), Is.Not.Null);
        Assert.That(AccessTools.DeclaredMethod(tesselator, "BuildDecorPolygons"), Is.Not.Null);
        Assert.That(AccessTools.DeclaredField(tesselator, "centerModeldataByRenderPassByLodLevel"), Is.Not.Null);
        Assert.That(AccessTools.DeclaredField(typeof(TCTCache), "blockEntitiesOfChunk"), Is.Not.Null);
    }

    [Test]
    public void TheBorderFraysPerColumn()
    {
        var jitter = Enumerable.Range(-20, 40).Select(i => FarLod.Jitter(i, 3 * i)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(jitter, Has.All.InRange(-1.0, 1.0));
            Assert.That(jitter.Max() - jitter.Min(), Is.GreaterThan(1), "columns spread over the zone");
        });
    }

    [Test]
    public void ACellIsItsTopSolidBlockWhereHalfIsSolid()
    {
        var (air, stone, grass) = (new Block { BlockId = 0 }, new Block { BlockId = 1, DrawType = EnumDrawType.Cube },
            new Block { BlockId = 2, DrawType = EnumDrawType.TopSoil });
        FarLod.Kinds = [1, 2, 2];
        var source = Enumerable.Repeat(air, Ext * Ext * Ext).ToArray();
        // coarse cell (1, 1, 1) covers ext 1..2 on every axis: stone below, grass on one top block
        foreach (var (x, z) in new[] { (1, 1), (2, 1), (1, 2), (2, 2) }) source[At(x, 1, z)] = stone;
        source[At(1, 2, 1)] = grass;
        Assert.That(FarLod.Cell(source, 1, 1, 1, false), Is.SameAs(grass));
        // three of eight: air (two of eight would do for leaves or wood)
        var thin = Enumerable.Repeat(air, Ext * Ext * Ext).ToArray();
        foreach (var (x, z) in new[] { (1, 1), (2, 1), (1, 2) }) thin[At(x, 1, z)] = stone;
        Assert.That(FarLod.Cell(thin, 1, 1, 1, false), Is.Null);
        // the +x border reads the neighbour's layer (ext 33): solid only where all four of its blocks hold
        source[At(33, 1, 1)] = stone;
        source[At(33, 2, 1)] = stone;
        Assert.That(FarLod.Cell(source, 17, 1, 1, false), Is.Null);
        source[At(33, 1, 2)] = stone;
        source[At(33, 2, 2)] = stone;
        Assert.That(FarLod.Cell(source, 17, 1, 1, false), Is.SameAs(stone));
    }

    [Test]
    public void LightIsTheBrightestByteByByte()
    {
        var light = new int[Ext * Ext * Ext];
        light[At(1, 1, 1)] = 0x0A000020;
        light[At(2, 2, 2)] = 0x01300010;
        Assert.That(FarLod.Brightest(light, 1, 1, 1), Is.EqualTo(0x0A300020));
    }

    [Test]
    public void BandsHoldTheirSide()
    {
        var d = FarLod.Distance(1440, 70, 2);
        Assert.That(d, Is.GreaterThan(256));
        Assert.That(FarLod.Distance(1440, 70, 0), Is.NaN);
        Assert.That(FarLod.Want(d, 0, d), Is.EqualTo(1));
        Assert.That(FarLod.Want(d + 200, 0, d), Is.EqualTo(2));
        Assert.That(FarLod.Want(d + 70, 2, d), Is.EqualTo(2)); // inside the hysteresis: stays coarse
        Assert.That(FarLod.Want(d + 70, 1, d), Is.EqualTo(1)); // and full
        Assert.That(FarLod.Want(2 * d + 200, 0, d), Is.EqualTo(3)); // past twice the threshold: farthest
        Assert.That(FarLod.Want(2 * d + 70, 3, d), Is.EqualTo(3));
        Assert.That(FarLod.Want(2 * d + 70, 2, d), Is.EqualTo(2));
        Assert.That(FarLod.Want(d + 70, 3, d), Is.EqualTo(2)); // back inside: half resolution again
        Assert.That((ChunkKey.Unpack(ChunkKey.Pack(5, 7, 9)), ChunkKey.Pack(-1, 7, 9)), Is.EqualTo(((5, 7, 9), -1L)));
    }

    // A coarse pass draws its cells 0-15 only: the border layer at local 16 would draw inside the neighbour once doubled
    [Test]
    public void ACoarsePassDrawsNothingPastSixteen()
    {
        var draw = Enumerable.Repeat((byte)0x3F, 32 * 32 * 32).ToArray();
        FarLod.ClearPastCells(draw);
        Assert.Multiple(() =>
        {
            Assert.That(draw[ChunkRig.Local(15, 15, 15)], Is.EqualTo(0x3F));
            Assert.That(new[] { draw[ChunkRig.Local(16, 0, 0)], draw[ChunkRig.Local(0, 16, 0)], draw[ChunkRig.Local(0, 0, 16)] },
                Is.All.Zero);
            Assert.That(draw.Count(b => b != 0), Is.EqualTo(16 * 16 * 16));
        });
        FarLod.ClearPastCells(draw, 8); // the farthest: cells 0-7
        Assert.That(draw.Count(b => b != 0), Is.EqualTo(8 * 8 * 8));
    }

    // A band announced at a mark keeps the band drawn until the pass; announcing the drawn band again forgets it
    [Test]
    public void AnAnnouncedBandKeepsTheDrawnOne()
    {
        var table = new ChunkBands();
        table.Bands[7] = ChunkBands.Far;
        table.Announce(7, ChunkBands.Near);
        Assert.That((table.Bands[7], table.DrawnBand(7)), Is.EqualTo((ChunkBands.Near, ChunkBands.Far)));
        table.Announce(7, ChunkBands.Far);
        Assert.That((table.Bands[7], table.Drawn.IsEmpty), Is.EqualTo((ChunkBands.Far, true)));
        table.Announce(7, ChunkBands.Near);
        table.Forget(7);
        Assert.That(table.DrawnBand(7), Is.Zero);
    }
}

public sealed class FarFoliageTests
{
    // 1440 px at 70 degrees: a block is one pixel at about 1028 blocks; a chunk switches far only past the band and back only inside it
    [Test]
    public void ThresholdAndHysteresis()
    {
        var d = FarFoliage.Distance(1440, 70, 1);
        Assert.That(d, Is.EqualTo(1440 / (2 * Math.Tan(35 * Math.PI / 180))).Within(1e-9));
        Assert.That(FarFoliage.Distance(1440, 70, 0), Is.NaN);
        Assert.That(FarFoliage.Distance(1440, 70, 2), Is.EqualTo(d / 2).Within(1e-9));
        Assert.Multiple(() =>
        {
            Assert.That(Want(d, 0, d), Is.EqualTo(1));
            Assert.That(Want(d + 100, 0, d), Is.EqualTo(2));
            Assert.That(Want(d + 80, 1, d), Is.EqualTo(1));
            Assert.That(Want(d + 80, 2, d), Is.EqualTo(2));
            Assert.That(Want(d + 50, 2, d), Is.EqualTo(1));
            Assert.That(Want(d + 90, 1, d), Is.EqualTo(2));
            Assert.That(Want(5000, 2, double.NaN), Is.EqualTo(1));
        });
    }

    private static int Want(double d, int band, double threshold) => PixelBand.Want(d, band, threshold, FarFoliage.Band);
}

public sealed class DeepCullingTests
{
    // A deep chunk is culled past the near distance plus the band and kept culled until it comes inside the near distance
    [Test]
    public void CullsWithHysteresis()
    {
        Assert.Multiple(() =>
        {
            Assert.That(DeepCulling.Cull(0, 200, 96), Is.True);
            Assert.That(DeepCulling.Cull(0, 120, 96), Is.False);
            Assert.That(DeepCulling.Cull(2, 120, 96), Is.True);
            Assert.That(DeepCulling.Cull(2, 90, 96), Is.False);
            Assert.That(DeepCulling.Cull(1, 130, 96), Is.False);
            Assert.That(DeepCulling.Cull(0, 5000, double.NaN), Is.False);
        });
    }
}
