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
    public void ACellIsItsTopSolidBlockWhereHalfIsSolid()
    {
        var (air, stone, grass) = (new Block { BlockId = 0 }, new Block { BlockId = 1, DrawType = EnumDrawType.Cube },
            new Block { BlockId = 2, DrawType = EnumDrawType.TopSoil });
        FarLod.Kinds = [1, 2, 2];
        var source = Enumerable.Repeat(air, Ext * Ext * Ext).ToArray();
        // coarse cell (1, 1, 1) covers ext 1..2 on every axis: stone below, grass on one top block
        foreach (var (x, z) in new[] { (1, 1), (2, 1), (1, 2), (2, 2) })
        {
            source[At(x, 1, z)] = stone;
        }

        source[At(1, 2, 1)] = grass;
        Assert.That(FarLod.Cell(source, 1, 1, 1), Is.SameAs(grass));
        // three of eight: air (two of eight would do for leaves or wood)
        var thin = Enumerable.Repeat(air, Ext * Ext * Ext).ToArray();
        foreach (var (x, z) in new[] { (1, 1), (2, 1), (1, 2) })
        {
            thin[At(x, 1, z)] = stone;
        }

        Assert.That(FarLod.Cell(thin, 1, 1, 1), Is.Null);
        // the +x border reads the neighbour's layer (ext 33): solid only where all four of its blocks hold
        source[At(33, 1, 1)] = stone;
        source[At(33, 2, 1)] = stone;
        Assert.That(FarLod.Cell(source, 17, 1, 1), Is.Null);
        source[At(33, 1, 2)] = stone;
        source[At(33, 2, 2)] = stone;
        Assert.That(FarLod.Cell(source, 17, 1, 1), Is.SameAs(stone));
    }

    [Test]
    public void WetFarChunksKeepBothSidesOfTheEngineWaterBoundary()
    {
        using var rig = new ChunkRig();
        using var harmony = new TestHarmony("komet-test-farlod-water");
        var world = Answers.Of<IClientWorldAccessor>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            // ReSharper disable once AccessToDisposedClosure - asked only while rig lives
            ["GetBlock"] = _ => rig.Blocks[ChunkRig.Air]
        });
        var api = Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_World"] = _ => world,
            ["get_Event"] = _ => Answers.Of<IClientEventAPI>([])
        });
        for (var x = 1; x <= 2; x++)
        {
            _ = rig.Put(x, 1, 1, static (_, _, _) => ChunkRig.Air,
                static (_, y, _) => y == 12 ? ChunkRig.Water : ChunkRig.Air);
        }

        var original = new byte[2][];
        for (var x = 1; x <= 2; x++)
        {
            _ = rig.Tesselator.BeginProcessChunk(x, 1, 1, rig.At(x, 1, 1)!, false);
            original[x - 1] = (byte[])rig.DrawFluids.Clone();
        }

        var pixels = FarLod.Pixels;
        try
        {
            FarLod.Pixels = 2;
            FarLod.Install(harmony, api);
            Assert.That(FarLod.Installed, Is.True);
            AccessTools.Field(typeof(FarLod), "_threshold").SetValue(null, 1d);
            _ = harmony.Patch(AccessTools.Method(typeof(ChunkTesselator), nameof(ChunkTesselator.BuildBlockPolygons)),
                new HarmonyMethod(typeof(FarLodTests), nameof(SkipMeshing)));
            for (var x = 1; x <= 2; x++)
            {
                _ = rig.Tesselator.NowProcessChunk(x, 1, 1, rig.Tess(x, 1, 1), false);
                Assert.That(rig.DrawFluids, Is.EqualTo(original[x - 1]), $"water face mask at chunk {x}");
                Assert.That(rig.FluidsExt[At(x == 1 ? 33 : 0, 13, 12)], Is.SameAs(rig.Blocks[ChunkRig.Water]));
            }
        }
        finally
        {
            FarLod.Stop();
            FarLod.Pixels = pixels;
        }
    }

    private static bool SkipMeshing() => false;

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

    private static int Want(double d, int band, double threshold) =>
        PixelBand.Want(d, band, threshold, FarFoliage.Band);
}
