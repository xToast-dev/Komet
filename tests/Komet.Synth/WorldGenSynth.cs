using Komet.Test.World;

namespace Komet.Test.Synth;

// GenTerra's own chunk column generation, set up as ColumnNoiseScratchTests set it up (initWorldGen against a stand-in server with the
// game's landforms.json), on one thread: the Parallel.For body then runs on the measuring thread, whose allocations Synth counts
[Category(Synth.Category)]
[NonParallelizable]
public sealed class WorldGenSynth
{
    // A new chunk column per op: 1024 block columns, each a ColumnNoise of nine octaves
    [Test]
    public void ColumnNoiseScratch()
    {
        GameInstall.RequireAssets();
        var region = GenTerraWorld.World(5, 1);
        var rain = new ushort[1024];
        var at = 0;
        long sum = 0;
        using (new FeatureRig("ColumnNoiseScratch"))
        {
            Assert.That(Komet.World.ColumnNoiseScratch.Rewritten, Is.True);
        }

        _ = Synth.Run("ColumnNoiseScratch", "chunkColumn", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var n = at++ % 16;
                Generate(region, rain, 5 + n % 4, 6 + n / 4);
                sum += rain[(i * 37) & 1023];
            }
        }, 4, () => sum, () =>
        {
            (at, sum) = (0, 0);
            Array.Clear(rain);
        });
    }

    // Eight chunks whose blocks take every write and keep none
    private static void Generate(GenTerraRegion region, ushort[] rain, int chunkX, int chunkZ)
    {
        var map = Answers.Of<IMapChunk>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_MapRegion"] = GenTerraWorld.Fixed(region.Maps), ["get_RainHeightMap"] = GenTerraWorld.Fixed(rain),
            ["get_WorldGenTerrainHeightMap"] = GenTerraWorld.Fixed(new ushort[1024])
        });
        var blocks = Answers.Of<IChunkBlocks>([]);
        GenTerraWorld.Generate(region, chunkX, chunkZ, map, _ => blocks, new ushort[8][], false);
    }
}
