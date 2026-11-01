using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace Komet.Test.Synth;

// GenTerra's own chunk column generation, set up as ColumnNoiseScratchTests set it up (initWorldGen against a stand-in server with the
// game's landforms.json), on one thread: the Parallel.For body then runs on the measuring thread, whose allocations Synth counts
[Category(Synth.Category)]
[NonParallelizable]
public sealed class WorldGenSynth
{
    private const int MapSizeY = 256, RegionSize = 512;
    private static readonly MethodInfo ColumnGen = AccessTools.Method(typeof(GenTerra), "OnChunkColumnGen");

    // A new chunk column per op: 1024 block columns, each a ColumnNoise of nine octaves
    [Test]
    public void ColumnNoiseScratch()
    {
        GameInstall.RequireAssets();
        var (terra, maps) = World(5);
        var rain = new ushort[1024];
        var at = 0;
        long sum = 0;
        using (new FeatureRig("ColumnNoiseScratch")) Assert.That(global::Komet.World.ColumnNoiseScratch.Rewritten, Is.True);
        _ = Synth.Run("ColumnNoiseScratch", "chunkColumn", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var n = at++ % 16;
                Generate(terra, maps, rain, 5 + n % 4, 6 + n / 4);
                sum += rain[(i * 37) & 1023];
            }
        }, 4, () => sum, () =>
        {
            (at, sum) = (0, 0);
            Array.Clear(rain);
        });
    }

    private static (GenTerra, IMapRegion) World(int seed)
    {
        var api = Server(seed);
        NoiseLandforms.LoadLandforms(api);
        var terra = new GenTerra();
        AccessTools.Field(typeof(GenTerra), "api").SetValue(terra, api);
        terra.initWorldGen();
        AccessTools.Field(typeof(GenTerra), "maxThreads").SetValue(terra, 1);
        var r = new Random(seed);
        var count = NoiseLandforms.landforms.LandFormsByIndex.Length;
        return (terra, Answers.Of<IMapRegion>(new()
        {
            ["get_ClimateMap"] = Fixed(Map(r, 16, 2, x => x.Next(0x1000000))),
            ["get_OceanMap"] = Fixed(Map(r, 16, 2, x => x.Next(3) == 0 ? x.Next(0, 255) : 0)),
            ["get_UpheavelMap"] = Fixed(Map(r, 16, 2, x => x.Next(0, 255))),
            ["get_LandformMap"] = Fixed(Map(r, 32, 4, x => x.Next(count)))
        }));
    }

    private static System.Func<object?[]?, object?> Fixed(object value) => _ => value;

    private static IntDataMap2D Map(Random r, int inner, int padding, System.Func<Random, int> value)
    {
        var size = inner + 2 * padding;
        return new IntDataMap2D
        {
            Data = [.. Enumerable.Range(0, size * size).Select(_ => value(r))], Size = size, TopLeftPadding = padding,
            BottomRightPadding = padding
        };
    }

    // Eight chunks whose blocks take every write and keep none
    private static void Generate(GenTerra terra, IMapRegion maps, ushort[] rain, int chunkX, int chunkZ)
    {
        var terrain = new ushort[1024];
        var map = Answers.Of<IMapChunk>(new()
        {
            ["get_MapRegion"] = Fixed(maps), ["get_RainHeightMap"] = Fixed(rain), ["get_WorldGenTerrainHeightMap"] = Fixed(terrain)
        });
        var blocks = Answers.Of<IChunkBlocks>([]);
        var chunks = Enumerable.Range(0, MapSizeY / 32)
            .Select(_ => Answers.Of<IServerChunk>(new() { ["get_Data"] = Fixed(blocks), ["get_MapChunk"] = Fixed(map) })).ToArray();
        var request = Answers.Of<IChunkColumnGenerateRequest>(new()
        {
            ["get_Chunks"] = Fixed(chunks), ["get_ChunkX"] = Fixed(chunkX), ["get_ChunkZ"] = Fixed(chunkZ),
            ["get_NeighbourTerrainHeight"] = Fixed(new ushort[8][]), ["get_RequiresChunkBorderSmoothing"] = Fixed(false)
        });
        _ = ColumnGen.Invoke(terra, [request]);
    }

    private static ICoreServerAPI Server(int seed)
    {
        var world = Answers.Of<IWorldManagerAPI>(new()
        {
            ["get_MapSizeX"] = Fixed(1024000), ["get_MapSizeY"] = Fixed(MapSizeY), ["get_MapSizeZ"] = Fixed(1024000),
            ["get_RegionSize"] = Fixed(RegionSize), ["get_Seed"] = Fixed(seed)
        });
        var config = new GlobalConfig
            { defaultRockId = 1, waterBlockId = 2, saltWaterBlockId = 3, lakeIceBlockId = 4, mantleBlockId = 5 };
        var cache = new Dictionary<string, object> { [GlobalConfig.cacheKey] = config };
        var landforms = Path.Combine(GameInstall.Assets, "survival", "worldgen", "landforms.json");
        var assets = Answers.Of<IAssetManager>(new()
            { ["Get"] = args => new Asset(File.ReadAllBytes(landforms), (AssetLocation)args![0]!, null) });
        var server = Answers.Of<IServerAPI>(new() { ["get_Config"] = Fixed(Answers.Of<IServerConfig>([])) });
        return Answers.Of<ICoreServerAPI>(new()
        {
            ["get_WorldManager"] = Fixed(world),
            ["get_World"] = Fixed(Answers.Of<IServerWorldAccessor>(new() { ["get_Seed"] = Fixed(seed) })),
            ["get_Server"] = Fixed(server), ["get_ObjectCache"] = Fixed(cache), ["get_Assets"] = Fixed(assets),
            ["get_Event"] = Fixed(Answers.Of<IServerEventAPI>([])), ["get_ModLoader"] = Fixed(Answers.Of<IModLoader>([]))
        });
    }
}
