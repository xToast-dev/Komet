using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Solid rock runs NowProcessChunk's whole pipeline for zero vertices. Under FaceCullMode Default (other modes,
// JSONAndWater 0x40, up-side snow excluded) a face needs a neighbour side not opaque toward it: if the chunk and its
// six touching slabs are all opaque that way, it gets the shared empty parts and start bounds. Ids >= Blocks.Count are
// air, indices past paletteCount unknown, missing or Empty neighbours fail. Leave reproduces state later passes read.
internal static class OccludedChunks
{
    private const int Words = Size * Size, MaxBits = 15, MaxIndices = 1 << MaxBits, MaxBad = 32, AllSides = -1;

    private const float Start = 32f, End = 0f;

    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x1F5A2B6552C0CD27UL;

    // Face neighbours in TileSideEnum order (N -z, E +x, S +z, W -x, up, down); slab words: first, step, count, mask
    private static readonly (int Dx, int Dy, int Dz)[] Offsets =
        [(0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0), (0, 1, 0), (0, -1, 0)];

    private static readonly (int First, int Step, int Count, int Mask)[] Slabs =
    [
        (31, 32, 32, -1), (0, 1, Words, 1), (0, 32, 32, -1), (0, 1, Words, int.MinValue), (0, 1, 32, -1),
        (992, 1, 32, -1)
    ];

    private static readonly Tally Counts = new(1);
    private static MethodBase?[] _skipped = [];

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Another mod patches the pipeline this skips: the engine runs every pass
    public static bool StoodDown { get; private set; }

    public static long Hits => Counts.Total(0);

    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape)
    {
        (Installed, StoodDown) = (false, false);
        var shaped = Shaped();
        if (!NotNull(harmony) || !NotNull(shaped[0]) || !Accessible(nameof(OccludedChunks), logger) ||
            !EngineShape.Matches(shaped, shape, nameof(OccludedChunks), logger)) return;
        if (AccessTools.DeclaredField(typeof(ChunkTesselator), "mapsizeChunksy")?.FieldType != typeof(int))
        {
            logger?.Warning(
                "Komet OccludedChunks: ChunkTesselator.mapsizeChunksy is missing, the engine tessellates every chunk");
            return;
        }

        _skipped = shaped;
        _ = harmony.Patch(shaped[0], new HarmonyMethod(Prefix));
        Installed = true;
        Recheck();
    }

    // The prefix replaces NowProcessChunk's body (its prefixes/postfixes still run) and the rest of Shaped()
    internal static void Recheck()
    {
        if (!Installed) return;
        // Komet's own change nothing for a faceless chunk (no part for FaceSorting)
        var foreign = EngineShape.Foreign(_skipped.AsSpan(0, 1), PatchKinds.Body, null) ||
                      EngineShape.Foreign(_skipped.AsSpan(1), PatchKinds.All, null, typeof(TessSafety),
                          typeof(Rendering.FaceSorting));
        StoodDown = EngineShape.Report(Logger, nameof(OccludedChunks), StoodDown, foreign);
    }

    // NowProcessChunk, then the bodies whose result it returns: BeginProcessChunk (Leave), extended copy with palette
    // clearing and neighbour lookup, face culling, polygon walk, part population, and the readers Fits reproduces:
    // the row copies and their decoders, GetOne and GetSolidBlock (east/west slabs)
    internal static MethodBase?[] Shaped()
    {
        var (tesselator, i, @out) = (typeof(ChunkTesselator), typeof(int), typeof(int).MakeByRefType());
        MethodBase?[] methods =
        [
            Method(tesselator, nameof(ChunkTesselator.NowProcessChunk), i, i, i, typeof(TesselatedChunk), typeof(bool)),
            Method(tesselator, nameof(ChunkTesselator.BeginProcessChunk), i, i, i, typeof(ClientChunk), typeof(bool)),
            Method(tesselator, "BuildExtendedChunkData", typeof(ClientChunk), i, i, i, typeof(bool), typeof(bool)),
            Method(typeof(ChunkDataLayer), nameof(ChunkDataLayer.ClearPaletteOutsideMaxValue), i),
            Method(typeof(ClientWorldMap), "GetNeighbouringChunks", typeof(ClientChunk[]), i, i, i),
            Method(tesselator, nameof(ChunkTesselator.CalculateVisibleFaces), typeof(bool), i, i, i),
            Method(tesselator, nameof(ChunkTesselator.CalculateVisibleFaces_Fluids), typeof(bool), i, i, i),
            Method(tesselator, nameof(ChunkTesselator.BuildBlockPolygons), i, i, i),
            Method(tesselator, nameof(ChunkTesselator.BuildBlockPolygons_EdgeOnly), i, i, i),
            Method(tesselator, "TesselateBlock", i, i, i, i, i),
            Method(tesselator, "populateTesselatedChunkPart", typeof(MeshData[][][]),
                typeof(TesselatedChunkPart[]).MakeByRefType()),
            .. Readers(),
            Method(ClientData, "GetOne", typeof(ushort).MakeByRefType(), @out, @out, i),
            Method(typeof(ChunkData), nameof(ChunkData.GetSolidBlock), i)
        ];
        return methods;
    }

    // ClientChunkData's row copies, the layer reads and block decoders behind them, and BuildFastBlockAccessArray
    // (fills the decoders' table); this order is part of Shape
    private static MethodBase?[] Readers()
    {
        var (i, data, layer) = (typeof(int), typeof(ChunkData), typeof(ChunkDataLayer));
        var (blocks, light) = (typeof(BlockChunkDataLayer), typeof(ColorUtil.LightUtil));
        Type[] row = [typeof(Block[]), typeof(Block[]), typeof(int[]), i, i, i, typeof(Block[]), light];
        MethodBase?[] methods =
        [
            Method(ClientData, "GetRange_Faster", row), Method(ClientData, "GetRange", row),
            Method(ClientData, "Light2", i), Method(data, "Light", i), Method(data, nameof(ChunkData.GetFluid), i),
            Method(layer, nameof(ChunkDataLayer.GetUnsafe), i),
            Method(light, nameof(ColorUtil.LightUtil.ToRgba), typeof(uint), i),
            Method(layer, "GetFromBits0", i), Method(layer, "GetFromBits1", i), Method(layer, "GetFromBits2", i),
            Method(layer, "GetFromBits3", i), Method(layer, "GetFromBits4", i), Method(layer, "GetFromBits5", i),
            Method(layer, "GetGeneralCase", i), Method(ClientData, "getBlockAir", i),
            Method(blocks, "getBlockOne", i), Method(blocks, "getBlockTwo", i), Method(blocks, "getBlockThree", i),
            Method(blocks, "getBlockFour", i), Method(blocks, "getBlockFive", i),
            Method(blocks, "getBlockGeneralCase", i),
            Method(ClientData, "BuildFastBlockAccessArray", typeof(Block[]))
        ];
        return methods;
    }

    private static bool Prefix(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ,
        TesselatedChunk tessChunk, bool skipChunkCenter, ref int __result)
    {
        if (!Enabled || StoodDown || !NotNull(tessChunk)) return true;
        if (!Enclosed(__instance, chunkX, chunkY, chunkZ, tessChunk)) return true;
        __result = Apply(__instance, chunkX, chunkY, chunkZ, tessChunk, skipChunkCenter);
        if (Counting.Hud) Counts.Add(0, 1);
        return false;
    }

    // What NowProcessChunk leaves for a chunk without a single visible face, in the chunk and in the tesselator
    internal static int Apply(ChunkTesselator tesselator, int x, int y, int z, TesselatedChunk tessChunk,
        bool skipChunkCenter)
    {
        var empty = EmptyParts(tesselator);
        if (!NotNull(empty) || !Assert(empty.Length == 0)) empty = [];
        if (!skipChunkCenter) CenterParts(tessChunk) = empty;
        EdgeParts(tessChunk) = empty;
        // BeginProcessChunk's start values, which no tesselator moved
        SetBounds(tessChunk, Start, End, Start, End, Start, End);
        Leave(tesselator, x, y, z);
        return 0;
    }

    // Next pass reads state this one leaves: BeginProcessChunk's climate map (filling the lerped cache) and ocean
    // corners, overwritten only where an ocean map exists (a later chunk without one uses them for sea-level water),
    // and tmpPos's dimension, set after BeginProcessChunk and read by the next face culling.
    private static void Leave(ChunkTesselator tesselator, int x, int y, int z)
    {
        var map = Game(tesselator)?.WorldMap;
        if (!NotNull(map)) return;
        ClimateMap(tesselator) = map.LoadOrCreateLerpedClimateMapOffthread(x, z);
        if (map.LoadOceanityCorners(x, z) is { Length: >= 4 } corners)
            (OceanTl(tesselator), OceanTr(tesselator), OceanBl(tesselator), OceanBr(tesselator)) =
                (corners[0], corners[1], corners[2], corners[3]);
        _ = TmpPos(tesselator)?.SetDimension(y / 1024);
    }

    internal static bool Enclosed(ChunkTesselator tesselator, int x, int y, int z, TesselatedChunk tessChunk)
    {
        if (!Started(tesselator) || Reload(tesselator)) return false;
        var (blocks, game) = (BlocksFast(tesselator), Game(tesselator));
        var (map, count) = (game?.WorldMap, game?.Blocks?.Count ?? 0);
        if (blocks is not { Length: > 0 } || map is null || !Assert(count <= blocks.Length)) return false;
        if (x < 0 || y < 0 || z < 0 || x >= MapX(tesselator) || y >= MapY(tesselator) ||
            z >= MapZ(tesselator)) return false;
        var chunk = Chunk(tessChunk);
        if (chunk is not { Empty: false } || !NoDecors(chunk) ||
            !ReferenceEquals(map.GetChunk(x, y, z), chunk)) return false;
        if (Read(chunk) is not { } data || !Fits(data.blocksLayer, blocks, count, AllSides) ||
            !Fits(data.fluidsLayer, blocks, count, AllSides, true)) return false;
        for (var side = 0; side < Faces; side++)
        {
            // BuildBlockPolygons clears the down faces of the world's bottom layer
            if (side == Down && y == 0) continue;
            var (dx, dy, dz) = Offsets[side];
            if (map.GetChunk(x + dx, y + dy, z + dz) is not ClientChunk { Empty: false } neighbour) return false;
            if (Read(neighbour) is not { } near || !Fits(near.blocksLayer, blocks, count, side)) return false;
        }

        return true;
    }

    // A packed chunk through this thread's view (the build reuses it), else unpacked as the engine's build would
    private static ChunkData? Read(ClientChunk chunk)
    {
        if (NeighbourReads.Packed && ChunkViews.Open(chunk) is { } view) return view;
        _ = chunk.Unpack_ReadOnly();
        return chunk.Data as ChunkData;
    }

    private static bool NoDecors(ClientChunk chunk)
    {
        var decors = chunk.Decors;
        if (decors is null) return true;
        lock (decors) return decors.Count == 0; // the engine reads them under this lock
    }

    // No index failing Good in the layer (AllSides) or touching slab (0-5); fluids: no used index maps to non-air
    internal static bool Fits(ChunkDataLayer? layer, Block[] blocks, int count, int side, bool fluids = false)
    {
        if (layer?.palette is not { } palette) return fluids;
        Span<int> bad = stackalloc int[MaxBad];
        layer.readWriteLock.AcquireReadLock();
        try
        {
            var (bits, planes) = (Bitsize(layer), DataBits(layer));
            if (bits <= 0) return fluids;
            if (!Planes(planes, bits)) return false;
            var n = Bad(palette, layer.paletteCount, bits, blocks, count, side, fluids, bad);
            var (first, step, words, mask) = side == AllSides ? (0, 1, Words, -1) : Slabs[side];
            return n >= 0 && !Uses(planes!, bits, bad[..n], first, step, words, mask);
        }
        finally
        {
            layer.readWriteLock.ReleaseReadLock();
        }
    }

    // Palette indices failing Good (fluids: not air), or -1 when more than the scan takes
    private static int Bad(int[] palette, int used, int bits, Block[] blocks, int count, int side, bool fluids,
        Span<int> bad)
    {
        var n = 0;
        for (var i = 0; i < Math.Min(1 << bits, MaxIndices); i++)
        {
            var value = i < palette.Length ? palette[i] : -1;
            if (fluids
                    ? value >= 0 && value < blocks.Length && ReferenceEquals(blocks[value], blocks[0])
                    : i < used && i < palette.Length && Good(Lookup(blocks, value, count), side)) continue;
            if (n >= MaxBad) return -1;
            bad[n++] = i;
        }

        return n;
    }

    // A palette value's block in the extended arrays: values >= block count become 0 (ClearPaletteOutsideMaxValue)
    private static Block? Lookup(Block[] blocks, int value, int count)
    {
        if (value < 0 || value >= count) return blocks[0];
        return Index(value, blocks.Length) ? blocks[value] : null;
    }

    // AllSides: neither draws a face nor lets one be drawn toward it. Side: hides the face; on up, no snow layer
    internal static bool Good(Block? block, int side)
    {
        if (block is null) return false;
        if (side != AllSides)
            return block.SideOpaque[TileSideEnum.GetOpposite(side)] &&
                   (side != Up || block.DrawType != EnumDrawType.JSONAndSnowLayer);
        return block.FaceCullMode == EnumFaceCullMode.Default && block.SideOpaque.All &&
               block.DrawType is not (EnumDrawType.JSONAndWater or EnumDrawType.JSONAndSnowLayer);
    }

    private static bool Planes(int[]?[]? planes, int bits)
    {
        if (planes is null || !Assert(bits is > 0 and <= MaxBits) || planes.Length < bits) return false;
        for (var l = 0; l < Math.Min(bits, MaxBits); l++)
            if (planes[l] is not { Length: >= Words }) return false;
        return true;
    }

    // Whether any word holds a bad palette index at a mask bit; Planes checked every plane
    private static bool Uses(int[]?[] planes, int bits, ReadOnlySpan<int> bad, int first, int step, int words, int mask)
    {
        if (bad.IsEmpty) return false;
        for (var k = 0; k < Math.Min(words, Words); k++)
        {
            var w = first + k * step;
            for (var b = 0; b < Math.Min(bad.Length, MaxBad); b++)
            {
                var hit = mask;
                for (var l = 0; l < Math.Min(bits, MaxBits) && hit != 0; l++)
                    hit &= ((bad[b] >> l) & 1) != 0 ? planes[l]![w] : ~planes[l]![w];
                if (hit != 0) return true;
            }
        }

        return false;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "reloadTesselatorOnTesselationThread")]
    private static extern ref bool Reload(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mapsizeChunksx")]
    private static extern ref int MapX(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mapsizeChunksy")]
    private static extern ref int MapY(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "mapsizeChunksz")]
    private static extern ref int MapZ(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentClimateRegionMap")]
    private static extern ref int[]? ClimateMap(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentOceanityMapTL")]
    private static extern ref float OceanTl(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentOceanityMapTR")]
    private static extern ref float OceanTr(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentOceanityMapBL")]
    private static extern ref float OceanBl(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentOceanityMapBR")]
    private static extern ref float OceanBr(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "emptyParts")]
    private static extern ref TesselatedChunkPart[]? EmptyParts(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunk")]
    private static extern ref ClientChunk? Chunk(TesselatedChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerParts")]
    private static extern ref TesselatedChunkPart[]? CenterParts(TesselatedChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeParts")]
    private static extern ref TesselatedChunkPart[]? EdgeParts(TesselatedChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetBounds")]
    private static extern void SetBounds(TesselatedChunk chunk, float xMin, float xMax, float yMin, float yMax,
        float zMin, float zMax);
}
