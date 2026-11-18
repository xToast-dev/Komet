using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace Komet.Test.World;

// One region of one world for GenTerra's own column generation, as ColumnNoiseScratchTests and WorldGenSynth build it
internal sealed record GenTerraRegion(GenTerra Terra, IMapRegion Maps);

internal static class GenTerraWorld
{
    private const int MapSizeY = 256, RegionSize = 512;
    private static readonly MethodInfo ColumnGen = AccessTools.Method(typeof(GenTerra), "OnChunkColumnGen");

    // The four maps GenTerra reads, drawn at random but fixed by the seed, and a GenTerra set up by its own initWorldGen. The
    // landform map is the region's, 32 pixels plus the engine's padding of 4, each pixel a landform of the game's. initWorldGen
    // sizes the Parallel.For by the machine's cores; threads fixes it.
    public static GenTerraRegion World(int seed, int threads)
    {
        var api = Server(seed);
        NoiseLandforms.LoadLandforms(api);
        var terra = new GenTerra();
        AccessTools.Field(typeof(GenTerra), "api").SetValue(terra, api);
        terra.initWorldGen();
        AccessTools.Field(typeof(GenTerra), "maxThreads").SetValue(terra, threads);
        var r = new Random(seed);
        var count = NoiseLandforms.landforms.LandFormsByIndex.Length;
        return new GenTerraRegion(terra, Answers.Of<IMapRegion>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_ClimateMap"] = Fixed(Map(r, 16, 2, x => x.Next(0x1000000))),
            ["get_OceanMap"] = Fixed(Map(r, 16, 2, x => x.Next(3) == 0 ? x.Next(0, 255) : 0)),
            ["get_UpheavelMap"] = Fixed(Map(r, 16, 2, x => x.Next(0, 255))),
            ["get_LandformMap"] = Fixed(Map(r, 32, 4, x => x.Next(count)))
        }));
    }

    public static System.Func<object?[]?, object?> Fixed(object value) => _ => value;

    // OnChunkColumnGen for one column: eight chunks over map, chunk i's blocks from blocks(i)
    public static void Generate(GenTerraRegion region, int chunkX, int chunkZ, IMapChunk map,
        System.Func<int, IChunkBlocks> blocks, ushort[]?[] neighbours, bool smooth)
    {
        var chunks = Enumerable.Range(0, MapSizeY / 32)
            .Select(chunk => Answers.Of<IServerChunk>(new Dictionary<string, System.Func<object?[]?, object?>>
                { ["get_Data"] = Fixed(blocks(chunk)), ["get_MapChunk"] = Fixed(map) })).ToArray();
        var request = Answers.Of<IChunkColumnGenerateRequest>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_Chunks"] = Fixed(chunks), ["get_ChunkX"] = Fixed(chunkX), ["get_ChunkZ"] = Fixed(chunkZ),
            ["get_NeighbourTerrainHeight"] = Fixed(neighbours), ["get_RequiresChunkBorderSmoothing"] = Fixed(smooth)
        });
        _ = ColumnGen.Invoke(region.Terra, [request]);
    }

    private static IntDataMap2D Map(Random r, int inner, int padding, System.Func<Random, int> value)
    {
        var size = inner + 2 * padding;
        return new IntDataMap2D
        {
            Data = [.. Enumerable.Range(0, size * size).Select(_ => value(r))], Size = size, TopLeftPadding = padding,
            BottomRightPadding = padding
        };
    }

    // Just enough of a server: initWorldGen reads the world's size and seed and the thread limits, GlobalConfig.GetInstance finds the
    // block ids in the API's object cache before it would look anything up, and LoadLandforms reads the game's own landforms.json
    private static ICoreServerAPI Server(int seed)
    {
        var world = Answers.Of<IWorldManagerAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_MapSizeX"] = Fixed(1024000), ["get_MapSizeY"] = Fixed(MapSizeY), ["get_MapSizeZ"] = Fixed(1024000),
            ["get_RegionSize"] = Fixed(RegionSize), ["get_Seed"] = Fixed(seed)
        });
        var config = new GlobalConfig
            { defaultRockId = 1, waterBlockId = 2, saltWaterBlockId = 3, lakeIceBlockId = 4, mantleBlockId = 5 };
        var cache = new Dictionary<string, object> { [GlobalConfig.cacheKey] = config };
        var landforms = Path.Combine(GameInstall.Assets, "survival", "worldgen", "landforms.json");
        var assets = Answers.Of<IAssetManager>(new Dictionary<string, System.Func<object?[]?, object?>>
            { ["Get"] = args => new Asset(File.ReadAllBytes(landforms), (AssetLocation)args![0]!, null) });
        var server = Answers.Of<IServerAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
            { ["get_Config"] = Fixed(Answers.Of<IServerConfig>([])) });
        return Answers.Of<ICoreServerAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_WorldManager"] = Fixed(world),
            ["get_World"] =
                Fixed(Answers.Of<IServerWorldAccessor>(new Dictionary<string, System.Func<object?[]?, object?>>
                    { ["get_Seed"] = Fixed(seed) })),
            ["get_Server"] = Fixed(server), ["get_ObjectCache"] = Fixed(cache), ["get_Assets"] = Fixed(assets),
            ["get_Event"] = Fixed(Answers.Of<IServerEventAPI>([])),
            ["get_ModLoader"] = Fixed(Answers.Of<IModLoader>([]))
        });
    }
}
