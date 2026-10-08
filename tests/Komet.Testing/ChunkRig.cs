using System.Collections;
using Vintagestory.API.Client.Tesselation;

namespace Komet.Testing;

// The engine's ChunkTesselator with the ClientMain and ClientWorldMap around it, built without their constructors: just the state
// BeginProcessChunk and NowProcessChunk touch for a chunk (face culling, extended arrays, climate map placeholder, empty mesh pools),
// and chunks on the engine's own storage (ClientChunk.CreateNew over a ClientChunkDataPool) in the map's dictionary. Nothing here
// tessellates a visible face: that would need the block tesselators, atlases and shapes, which the golden tests never reach.
public sealed class ChunkRig : IDisposable
{
    public const int Size = 32, Volume = Size * Size * Size, ChunksX = 4, ChunksY = 4, ChunksZ = 4, Mul = 2097152;

    // Block ids. Stone and granite are opaque cubes; glass lets faces through; a slab is opaque only below; snow is an opaque
    // JSONAndSnowLayer block, merge an opaque block with FaceCullMode.Merge, jsonwater an opaque JSONAndWater block; water is a
    // fluid with the liquid face cull mode
    public const int Air = 0, Stone = 1, Granite = 2, Glass = 3, Slab = 4, Snow = 5, Water = 6, Merge = 7,
        JsonWater = 8, Count = 9;

    // TileSideEnum.MoveIndex as ChunkTesselator.Start sets it for the 34^3 extended arrays: N E S W U D
    public static readonly int[] Moves = [-34, 1, 34, -1, 1156, -1156];

    private readonly MeshDataRecycler? _recycler;

    // extra: that many more opaque cube block types after the named ones (ids Count and up), for palettes of more than 5 bits
    public ChunkRig(int extra = 0)
    {
        _recycler = MeshData.Recycler;
        Moves.CopyTo(TileSideEnum.MoveIndex, 0);
        Blocks = MakeBlocks(extra);
        Game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        Game.Blocks = [.. Blocks];
        Game.BlockAtlasManager =
            (BlockTextureAtlasManager)RuntimeHelpers.GetUninitializedObject(typeof(BlockTextureAtlasManager));
        Pool = new ClientChunkDataPool(Size, Game);
        Map = (ClientWorldMap)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldMap));
        Set(Map, "game", Game);
        Set(Map, "mapsize", new Vec3i(ChunksX * Size, ChunksY * Size, ChunksZ * Size));
        (Map.index3dMulX, Map.index3dMulZ, Map.chunkMapSizeY, Map.ClientChunkSize, Map.ServerChunkSize,
            Map.regionSize) = (Mul, Mul, ChunksY, Size, Size, 16 * Size);
        Set(Map, "chunks", new Dictionary<long, ClientChunk>());
        Set(Map, "chunksLock", new object());
        // of an internal type: made by reflection
        var regions = AccessTools.Field(typeof(ClientWorldMap), "MapRegions");
        regions.SetValue(Map, Activator.CreateInstance(regions.FieldType));
        Set(Map, "LerpedClimateMaps", new LimitedDictionary<long, int[]>(10));
        Set(Map, "LerpedClimateMapsLock", new object());
        Set(Map, "regionMapSizeX", 4);
        Set(Map, "regionMapSizeY", 1);
        Set(Map, "regionMapSizeZ", 4);
        var empty = ClientChunk.CreateNew(Pool);
        empty.Lighting.FillWithSunlight(24);
        empty.Empty = true;
        Set(Map, "EmptyChunk", empty);
        Game.WorldMap = Map;
        Tesselator = Rigged(Game, Blocks);
        MeshData.Recycler = new MeshDataRecycler(Game);
    }

    public Block[] Blocks { get; }
    public ClientMain Game { get; }
    public ClientWorldMap Map { get; }
    public ChunkTesselator Tesselator { get; }
    public ClientChunkDataPool Pool { get; }

    public byte[] Draw => (byte[])Get(Tesselator, "currentChunkDraw32");
    public byte[] DrawFluids => (byte[])Get(Tesselator, "currentChunkDrawFluids");
    public Block[] BlocksExt => (Block[])Get(Tesselator, "currentChunkBlocksExt");
    public Block[] FluidsExt => (Block[])Get(Tesselator, "currentChunkFluidBlocksExt");
    public int[] RgbsExt => (int[])Get(Tesselator, "currentChunkRgbsExt");

    private Dictionary<long, ClientChunk> Chunks => (Dictionary<long, ClientChunk>)Get(Map, "chunks");

    public void Dispose() => MeshData.Recycler = _recycler;

    public static int Local(int x, int y, int z) => (y * Size + z) * Size + x;

    // Another tesselator on the same game and map, as a tessellation worker has one
    public ChunkTesselator NewTesselator() => Rigged(Game, Blocks);

    // A chunk whose block at (x, y, z) is block(x, y, z), in the map at the chunk position; fluid ids go into the fluid layer
    public ClientChunk Put(int cx, int cy, int cz, System.Func<int, int, int, int> block,
        System.Func<int, int, int, int>? fluid = null, bool empty = false)
    {
        var chunk = ClientChunk.CreateNew(Pool);
        for (var i = 0; i < Volume; i++)
        {
            int x = i % Size, z = i / Size % Size, y = i / Size / Size;
            var id = block(x, y, z);
            if (id != Air) chunk.Data[i] = id;
            var f = fluid?.Invoke(x, y, z) ?? Air;
            if (f != Air) chunk.Data.SetFluid(i, f);
        }

        chunk.Empty = empty;
        Set(chunk, "loadedFromServer", true);
        Chunks[Key(cx, cy, cz)] = chunk;
        return chunk;
    }

    public void Remove(int cx, int cy, int cz) => _ = Chunks.Remove(Key(cx, cy, cz));

    // The map region around chunk column (cx, cz) with a climate and an ocean map, as the server sends it: BeginProcessChunk then
    // lerps a climate map (and caches it) and reads ocean corners instead of falling back to placeholders
    public void AddRegion(int cx, int cz, int salt)
    {
        const int size = 18; // 16 inner values and a padding of 1

        IntDataMap2D Values(int offset) => new()
        {
            Data = [.. Enumerable.Range(0, size * size).Select(i => (i * 7919 + offset * 104729) % 251)],
            Size = size, TopLeftPadding = 1, BottomRightPadding = 1
        };

        var region =
            RuntimeHelpers.GetUninitializedObject(AccessTools.TypeByName("Vintagestory.Client.ClientMapRegion"));
        Set(region, "ClimateMap", Values(salt));
        Set(region, "OceanMap", Values(salt + 1));
        ((IDictionary)Get(Map, "MapRegions"))[Map.MapRegionIndex2DFromClientChunkCoord(cx, cz)] = region;
    }

    public ClientChunk? At(int cx, int cy, int cz) => Chunks.GetValueOrDefault(Key(cx, cy, cz));

    public TesselatedChunk Tess(int cx, int cy, int cz)
    {
        var tess = new TesselatedChunk();
        Set(tess, "chunk", At(cx, cy, cz)!);
        Set(tess, "positionX", cx * Size);
        Set(tess, "positionYAndDimension", cy * Size);
        Set(tess, "positionZ", cz * Size);
        return tess;
    }

    public static TesselatedChunkPart[]? CenterParts(TesselatedChunk tess) =>
        (TesselatedChunkPart[]?)Get(tess, "centerParts");

    public static TesselatedChunkPart[]? EdgeParts(TesselatedChunk tess) =>
        (TesselatedChunkPart[]?)Get(tess, "edgeParts");

    public static Sphere Bounds(TesselatedChunk tess) => (Sphere)Get(tess, "boundingSphere");

    // The engine's face culling for the chunk: BeginProcessChunk (neighbours, extended arrays, visible faces), then the down faces of
    // the world's bottom layer cleared as BuildBlockPolygons does. True when no position of the chunk would be tessellated.
    public bool EngineDrawsNothing(int cx, int cy, int cz)
    {
        _ = Tesselator.BeginProcessChunk(cx, cy, cz, At(cx, cy, cz)!, false);
        byte[] draw = Draw, fluids = DrawFluids;
        for (var i = 0; i < Volume; i++)
        {
            var bottom = cy == 0 && i < Size * Size ? 223 : 255;
            if ((draw[i] & bottom) != 0 || (fluids[i] & bottom) != 0) return false;
        }

        return true;
    }

    public static long Key(int cx, int cy, int cz) => MapUtil.Index3dL(cx, cy, cz, Mul, Mul);

    private static ChunkTesselator Rigged(ClientMain game, Block[] blocks)
    {
        var tess = (ChunkTesselator)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselator));
        const int ext = 34 * 34 * 34;
        Set(tess, "game", game);
        Set(tess, "vars", new TCTCache(tess));
        Set(tess, "currentChunkRgbsExt", new int[ext]);
        Set(tess, "currentChunkBlocksExt", new Block[ext]);
        Set(tess, "currentChunkFluidBlocksExt", new Block[ext]);
        Set(tess, "chunksNearby", new ClientChunk[27]);
        var datas = AccessTools.Field(typeof(ChunkTesselator), "chunkdatasNearby");
        datas.SetValue(tess, Array.CreateInstance(datas.FieldType.GetElementType()!, 27));
        Set(tess, "currentChunkDraw32", new byte[Volume]);
        Set(tess, "currentChunkDrawFluids", new byte[Volume]);
        Set(tess, "blocksFast", blocks);
        // Every light level its own brightness, so two different light values never pack to the same rgba
        var levels = Enumerable.Range(0, 32).Select(level => level / 32f).ToArray();
        var hues = Enumerable.Range(0, 64).Select(hue => (byte)(hue * 4)).ToArray();
        var sats = Enumerable.Range(0, 8).Select(sat => (byte)(sat * 32)).ToArray();
        Set(tess, "lightConverter", new ColorUtil.LightUtil(levels, levels, hues, sats));
        Set(tess, "mapsizex", ChunksX * Size);
        Set(tess, "mapsizey", ChunksY * Size);
        Set(tess, "mapsizez", ChunksZ * Size);
        Set(tess, "mapsizeChunksx", ChunksX);
        Set(tess, "mapsizeChunksy", ChunksY);
        Set(tess, "mapsizeChunksz", ChunksZ);
        Set(tess, "started", true);
        Set(tess, "reloadTesselatorOnTesselationThread", false);
        Set(tess, "ReloadLock", new object());
        Set(tess, "tmpPos", new BlockPos(0));
        Set(tess, "emptyParts", Array.Empty<TesselatedChunkPart>());
        var passes = Enum.GetValues<EnumChunkRenderPass>();
        Set(tess, "passes", passes);
        Set(tess, "quantityAtlasses", 1);
        Set(tess, "TextureIdToReturnNum", new int[1]); // one atlas, texture id 0
        Set(tess, "ret", new TesselatedChunkPart[passes.Length]);
        Set(tess, "centerModeldataByRenderPassByLodLevel", Pools(passes.Length));
        Set(tess, "edgeModeldataByRenderPassByLodLevel", Pools(passes.Length));
        return tess;
    }

    private static MeshData[][][] Pools(int passes)
    {
        var lods = new MeshData[4][][];
        for (var lod = 0; lod < lods.Length; lod++)
        {
            lods[lod] = new MeshData[passes][];
            for (var pass = 0; pass < passes; pass++) lods[lod][pass] = [new MeshData(16, 16)];
        }

        return lods;
    }

    private static Block[] MakeBlocks(int extra)
    {
        var blocks = new Block[Count + extra];
        for (var id = 0; id < blocks.Length; id++)
            blocks[id] = new Block
            {
                BlockId = id, Code = new AssetLocation("komet", "tess" + id), DrawType = EnumDrawType.Cube,
                FaceCullMode = EnumFaceCullMode.Default, BlockMaterial = EnumBlockMaterial.Stone
            };
        (blocks[Air].DrawType, blocks[Air].BlockMaterial) = (EnumDrawType.Empty, EnumBlockMaterial.Air);
        blocks[Air].AllSidesOpaque = false;
        blocks[Air].SideSolid = new SmallBoolArray(0);
        blocks[Glass].AllSidesOpaque = false;
        blocks[Slab].SideOpaque = new SmallBoolArray(1 << 5); // the down side only
        blocks[Snow].DrawType = EnumDrawType.JSONAndSnowLayer;
        blocks[Merge].FaceCullMode = EnumFaceCullMode.Merge;
        blocks[JsonWater].DrawType = EnumDrawType.JSONAndWater;
        (blocks[Water].DrawType, blocks[Water].FaceCullMode, blocks[Water].BlockMaterial) =
            (EnumDrawType.Liquid, EnumFaceCullMode.Liquid, EnumBlockMaterial.Water);
        blocks[Water].AllSidesOpaque = false;
        return blocks;
    }

    public static void Set(object target, string field, object value)
    {
        var info = AccessTools.Field(target.GetType(), field) ??
                   throw new MissingFieldException(target.GetType().Name, field);
        info.SetValue(target, value);
    }

    public static object Get(object target, string field)
    {
        var info = AccessTools.Field(target.GetType(), field) ??
                   throw new MissingFieldException(target.GetType().Name, field);
        return info.GetValue(target)!;
    }
}
