using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// ChunkTesselator.NowProcessChunk runs the whole pipeline for solid rock deep underground - 27 chunks unpacked, the extended arrays
// built, six neighbours tested for each of 32 768 blocks, every position walked again in BuildBlockPolygons - to end with zero
// vertices. For FaceCullMode Default, CalculateVisibleFaces gives a block a face only where the neighbour's side toward it is not
// opaque (other modes, JSONAndWater's 0x40 flag and the snow rule on the up side are excluded below). So if every block of the chunk
// and every block touching it across its six faces is opaque on that side, every face flag is 0, BuildBlockPolygons tessellates
// nothing and populateTesselatedChunkPart hands out the tesselator's shared empty part array for the centre and the edge, with the
// start bounds. The prefix hands that back without the pipeline, only when: the tesselator started and has no shape reload pending;
// a dimension-0 chunk inside the map, not Empty, without decors, the one in the map at its position; every block-layer position
// holds a palette index whose block is Default, opaque on all six sides and neither JSONAndWater nor JSONAndSnowLayer (ids at or past
// Blocks.Count are air, as ClearPaletteOutsideMaxValue makes them; indices past the palette count are unknown); its fluid layer holds
// only air; each face neighbour is loaded and not Empty (the engine substitutes air), with every block of the touching slab opaque
// toward it and no JSONAndSnowLayer block above. The world's lowest chunks need no neighbour below: BuildBlockPolygons clears their
// bottom layer's down faces. What the skipped pipeline leaves for later passes to read is reproduced (Leave); what else it would have
// done - unpacking, clearing pools, recycling meshes, the extended arrays and face flags - the next pass redoes, and an edge-only one
// reads only the shell it writes itself.
internal static class OccludedChunks
{
    private const int Words = Size * Size,
        MaxBits = 15,
        MaxIndices = 1 << MaxBits,
        MaxBad = 32,
        Up = 4,
        Down = 5,
        AllSides = -1;

    private const float Start = 32f, End = 0f;

    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x1F5A2B6552C0CD27UL;

    // The face neighbours in TileSideEnum order (north -z, east +x, south +z, west -x, up, down), and the slab of each that touches
    // this chunk as bit-plane words: first word, step, count and the bits of a word that lie in the slab
    private static readonly (int Dx, int Dy, int Dz)[] Offsets =
        [(0, 0, -1), (1, 0, 0), (0, 0, 1), (-1, 0, 0), (0, 1, 0), (0, -1, 0)];

    private static readonly (int First, int Step, int Count, int Mask)[] Slabs =
    [
        (31, 32, 32, -1), (0, 1, Words, 1), (0, 32, 32, -1), (0, 1, Words, int.MinValue), (0, 1, 32, -1),
        (992, 1, 32, -1)
    ];

    private static readonly Tally Counts = new(1); // a total while Counting.Hud, every tessellation thread
    private static MethodBase?[] _skipped = [];

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Another mod patches the pipeline this skips: the engine runs every pass
    public static bool StoodDown { get; private set; }

    public static long Hits => Counts.Total(0); // passes the prefix answered

    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape)
    {
        (Installed, StoodDown) = (false, false);
        var shaped = Shaped();
        if (!NotNull(harmony) || !NotNull(shaped[0]) || !Assert(Slabs.Length == Faces && Offsets.Length == Faces) ||
            !Accessible(nameof(OccludedChunks), logger) ||
            !EngineShape.Matches(shaped, shape, nameof(OccludedChunks), logger)) return;
        if (AccessTools.DeclaredField(typeof(ChunkTesselator), "mapsizeChunksy")?.FieldType != typeof(int))
        {
            logger?.Warning(
                "Komet OccludedChunks: ChunkTesselator.mapsizeChunksy is missing, the engine tessellates every chunk");
            return;
        }

        _skipped = shaped;
        _ = NotNull(harmony.Patch(shaped[0], new HarmonyMethod(typeof(OccludedChunks), nameof(Prefix))));
        Installed = true;
        Recheck();
    }

    // The prefix replaces NowProcessChunk's body (a prefix or postfix there still runs) and the rest of Shaped(), which it reproduces
    internal static void Recheck()
    {
        if (!Installed || !Assert(_skipped.Length > 1)) return;
        var foreign = EngineShape.Foreign(_skipped.AsSpan(0, 1), EngineShape.Kinds.Body, null) ||
                      EngineShape.Foreign(_skipped.AsSpan(1), EngineShape.Kinds.All, null, typeof(ExtendedRows),
                          typeof(VisibleFaces),
                          typeof(TessSafety));
        StoodDown = Report(nameof(OccludedChunks), StoodDown, foreign);
    }

    // NowProcessChunk first, then the bodies whose result it hands back: BeginProcessChunk (the state Leave reproduces), the extended
    // copy with the palette clearing and the neighbour lookup, the face culling, the polygon walk and the part population, and the
    // chunk readers whose output Fits and FluidFree reproduce: ExtendedRows' rows and decoders, and GetOne with GetSolidBlock for the
    // east and west slabs
    internal static MethodBase?[] Shaped()
    {
        var (tesselator, i, @out) = (typeof(ChunkTesselator), typeof(int), typeof(int).MakeByRefType());
        MethodBase?[] methods =
        [
            Method(tesselator, nameof(ChunkTesselator.NowProcessChunk), i, i, i, typeof(TesselatedChunk), typeof(bool)),
            Method(tesselator, nameof(ChunkTesselator.BeginProcessChunk), i, i, i, typeof(ClientChunk), typeof(bool)),
            ExtendedRows.Target(),
            Method(typeof(ChunkDataLayer), nameof(ChunkDataLayer.ClearPaletteOutsideMaxValue), i),
            Method(typeof(ClientWorldMap), "GetNeighbouringChunks", typeof(ClientChunk[]), i, i, i),
            VisibleFaces.Target(),
            Method(tesselator, nameof(ChunkTesselator.CalculateVisibleFaces_Fluids), typeof(bool), i, i, i),
            Method(tesselator, nameof(ChunkTesselator.BuildBlockPolygons), i, i, i),
            Method(tesselator, nameof(ChunkTesselator.BuildBlockPolygons_EdgeOnly), i, i, i),
            Method(tesselator, "TesselateBlock", i, i, i, i, i),
            Method(tesselator, "populateTesselatedChunkPart", typeof(MeshData[][][]),
                typeof(TesselatedChunkPart[]).MakeByRefType()),
            .. ExtendedRows.Shaped()[1..], // [0] is ExtendedRows.Target(), listed above
            Method(ExtendedRows.ClientType, "GetOne", typeof(ushort).MakeByRefType(), @out, @out, i),
            Method(typeof(ChunkData), nameof(ChunkData.GetSolidBlock), i)
        ];
        _ = Assert(methods.Length <= EngineShape.MaxMethods) && Assert(methods.Length > 1);
        return methods;
    }

    // Harmony injects the instance, the arguments and the result by name
    private static bool Prefix(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, TesselatedChunk tessChunk,
        bool skipChunkCenter, ref int __result)
    {
        if (!Enabled || StoodDown || !NotNull(__instance) || !NotNull(tessChunk)) return true;
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
        SetBounds(tessChunk, Start, End, Start, End, Start,
            End); // BeginProcessChunk's start values, which no tesselator moved
        Leave(tesselator, x, y, z);
        return 0;
    }

    // The engine's pass leaves three things in the tesselator that the next pass reads before it sets them itself. BeginProcessChunk
    // fetches the climate map (and fills the map's lerped climate cache) and the ocean map corners, which it overwrites only for a
    // region that has an ocean map, so a later chunk without one tessellates its sea-level water with these. NowProcessChunk sets
    // tmpPos to the chunk's dimension only after BeginProcessChunk, so the next chunk's face culling (AllowSnowCoverage, SideIsSolid)
    // runs with this chunk's dimension.
    private static void Leave(ChunkTesselator tesselator, int x, int y, int z)
    {
        var map = Game(tesselator)?.WorldMap;
        if (!NotNull(map) || !Assert(x >= 0 && y >= 0 && z >= 0)) return;
        ClimateMap(tesselator) = map.LoadOrCreateLerpedClimateMapOffthread(x, z);
        if (map.LoadOceanityCorners(x, z) is { Length: >= 4 } corners)
            (OceanTl(tesselator), OceanTr(tesselator), OceanBl(tesselator), OceanBr(tesselator)) =
                (corners[0], corners[1], corners[2], corners[3]);
        _ = NotNull(TmpPos(tesselator)?.SetDimension(y / 1024));
    }

    internal static bool Enclosed(ChunkTesselator tesselator, int x, int y, int z, TesselatedChunk tessChunk)
    {
        if (!Started(tesselator) || Reload(tesselator) || !NotNull(tessChunk)) return false;
        var blocks = BlocksFast(tesselator);
        var map = Game(tesselator)?.WorldMap;
        var count = Game(tesselator)?.Blocks?.Count ?? 0;
        if (blocks is not { Length: > 0 } || map is null || !Assert(count <= blocks.Length)) return false;
        if (x < 0 || y < 0 || z < 0 || x >= MapX(tesselator) || y >= MapY(tesselator) ||
            z >= MapZ(tesselator)) return false;
        var chunk = Chunk(tessChunk);
        if (chunk is not { Empty: false } || !NoDecors(chunk) ||
            !ReferenceEquals(map.GetChunk(x, y, z), chunk)) return false;
        if (chunk.Data is not ChunkData data || !Fits(data.blocksLayer, blocks, count, AllSides) ||
            !FluidFree(data.fluidsLayer, blocks))
            return false;
        for (var side = 0; side < Faces; side++)
        {
            if (side == Down && y == 0)
                continue; // BuildBlockPolygons clears the down faces of the world's bottom layer
            var (dx, dy, dz) = Offsets[side];
            if (map.GetChunk(x + dx, y + dy, z + dz) is not ClientChunk { Empty: false } neighbour) return false;
            _ = neighbour.Unpack_ReadOnly(); // the engine unpacks it too, and 26 more
            if (neighbour.Data is not ChunkData near || !Fits(near.blocksLayer, blocks, count, side)) return false;
        }

        return Assert(chunk.Data is not null);
    }

    private static bool NoDecors(ClientChunk chunk)
    {
        var decors = NotNull(chunk) ? chunk.Decors : null;
        if (decors is null) return true;
        lock (decors)
        {
            return decors.Count == 0; // the engine reads them under this lock
        }
    }

    // True when no position of the layer (side AllSides) or of the slab touching this chunk (side 0-5, the neighbour across it) holds
    // a palette index whose block fails Good
    internal static bool Fits(ChunkDataLayer? layer, Block[] blocks, int count, int side)
    {
        if (layer?.palette is not { } palette || !Assert(side is >= AllSides and < Faces))
            return false; // no layer: air
        Span<int> bad = stackalloc int[MaxBad];
        layer.readWriteLock.AcquireReadLock();
        try
        {
            var bits = Bitsize(layer);
            var planes = DataBits(layer);
            if (bits <= 0 || !Planes(planes, bits)) return false; // no bits: every position reads 0, air
            var n = Bad(palette, layer.paletteCount, bits, blocks, count, side, bad);
            var (first, step, words, mask) = side == AllSides ? (0, 1, Words, -1) : Slabs[side];
            return n >= 0 && !Uses(planes!, bits, bad[..n], first, step, words, mask);
        }
        finally
        {
            layer.readWriteLock.ReleaseReadLock();
        }
    }

    // True when no position holds a fluid: every index in use maps to the air block, as CalculateVisibleFaces_Fluids compares
    private static bool FluidFree(ChunkDataLayer? layer, Block[] blocks)
    {
        if (layer?.palette is not { } palette || !NotNull(blocks)) return true;
        Span<int> bad = stackalloc int[MaxBad];
        layer.readWriteLock.AcquireReadLock();
        try
        {
            var bits = Bitsize(layer);
            var planes = DataBits(layer);
            if (bits <= 0) return true;
            if (!Planes(planes, bits)) return false;
            var n = 0;
            for (var i = 0; i < Math.Min(1 << bits, MaxIndices); i++)
            {
                var value = i < palette.Length ? palette[i] : -1;
                if (value >= 0 && value < blocks.Length && ReferenceEquals(blocks[value], blocks[0])) continue;
                if (n >= MaxBad) return false;
                bad[n++] = i;
            }

            return !Uses(planes!, bits, bad[..n], 0, 1, Words, -1);
        }
        finally
        {
            layer.readWriteLock.ReleaseReadLock();
        }
    }

    // The palette indices whose block fails Good, or -1 when there are more than the scan takes
    private static int Bad(int[] palette, int used, int bits, Block[] blocks, int count, int side, Span<int> bad)
    {
        if (!Assert(bits is > 0 and <= MaxBits) || !Assert(bad.Length == MaxBad)) return -1;
        var n = 0;
        for (var i = 0; i < Math.Min(1 << bits, MaxIndices); i++)
        {
            if (i < used && i < palette.Length && Good(Lookup(blocks, palette[i], count), side)) continue;
            if (n >= MaxBad) return -1;
            bad[n++] = i;
        }

        return n;
    }

    // The block a palette value stands for in the extended arrays: ClearPaletteOutsideMaxValue makes a value at or past the block
    // count 0, and blocksFast maps it
    private static Block? Lookup(Block[] blocks, int value, int count)
    {
        if (!Assert(blocks.Length > 0)) return null;
        if (value < 0 || value >= count) return blocks[0];
        return Index(value, blocks.Length) ? blocks[value] : null;
    }

    // AllSides: a block of this chunk, which neither draws a face nor lets a neighbour draw one toward it. A side: a block of the
    // neighbour across that side, whose side toward this chunk hides the face; above, not a snow layer block (the engine's
    // AllowSnowCoverage rule on the up side)
    internal static bool Good(Block? block, int side)
    {
        if (block is null || !Assert(side is >= AllSides and < Faces)) return false;
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
            if (planes[l] is not { Length: >= Words })
                return false;
        return true;
    }

    // Whether any word of the set holds, at a bit of the mask, one of the bad palette indices; Planes checked every plane
    private static bool Uses(int[]?[] planes, int bits, ReadOnlySpan<int> bad, int first, int step, int words, int mask)
    {
        if (!Assert(first + (words - 1) * step < Words) || !Assert(bits is > 0 and <= MaxBits)) return true;
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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "started")]
    private static extern ref bool Started(ChunkTesselator tesselator);

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
