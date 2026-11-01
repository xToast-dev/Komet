using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;
using Komet.Diagnostics;

namespace Komet.Tessellation;

// Far chunks at half resolution: past where a block covers under Pixels pixels, a chunk is tessellated from a 16³
// grid of 2x2x2 cells and its meshes are scaled twice, a quarter of the faces at a block size nobody sees. The engine's
// own pipeline does the work. After BuildExtendedChunkData the extended arrays of blocks, fluids and light, 34 cells a
// side with the neighbours' layer, hold the coarse grid in their lower octant with air above, so visible faces, light,
// AO, textures and colour maps come out as usual; only cells 0-15 are drawn (the border layer at 16 only hides the
// faces it covers), then BuildBlockPolygons' meshes and bounds are doubled, and the texture coordinates kept, so a
// texture spans two blocks. A cell is its top-most solid block where at least half of it is solid (cubes, topsoil,
// snow layers, leaves, never a block with a block entity), else air; fluids alike; its light the brightest of the
// cell, byte by byte, or where it holds none the light of the first lit cell above. Where a side's neighbour chunk is
// drawn in full the border is air, so the chunk closes itself with a wall instead of a crack; that neighbour opens its
// side facing the coarse chunk. Coarse passes skip decor and block entities, whose lookups go by world position; a pass
// whose band differs from the one drawn (ChunkBands.Drawn, also where Scan announced the new one) is never edge-only.
// Scan re-marks chunks changing band, with hysteresis, and their banded neighbours, a slice every 100 ms (ChunkBands).
// Past twice the distance the farthest chunks halve once more: the 16³ grid is coarsened again to 8³ cells of four
// blocks, meshes scaled four times, so a cell covers as many pixels there as a half-resolution cell where it begins.
// A coarse chunk closes each side whose neighbour is drawn at another resolution, and a finer chunk keeps its side
// facing a coarser one open (Skirted, or air borders in the coarse grid). Dimension 0 only.
internal static class FarLod
{
    public const int MaxPixels = 3;
    private const int Size = 32, Ext = 34, Cells = 16, Near = ChunkBands.Near, Far = ChunkBands.Far, Farthest = ChunkBands.Farthest,
        Dimension = 1024;
    private const int MaxMarks = 12, MaxKinds = 1 << 20, EveryMs = 100, PerScan = 4096, MaxRefines = 64, RefineMs = 1000, MaxReach = 96;
    private const double Band = 48, MinDistance = 256, Ragged = 0.2;
    private const byte Air = 1, Solid = 2, Sparse = 3; // Sparse: leaves and wood, a quarter of a cell is enough

    private static readonly ChunkBands Table = new();

    [ThreadStatic] private static bool _coarse, _skirted;
    [ThreadStatic] private static int _chunkX, _chunkZ, _chunkY, _band;
    [ThreadStatic] private static Dictionary<BlockPos, BlockEntity>? _entities;
    [ThreadStatic] private static Block[]? _blocks, _fluids;
    [ThreadStatic] private static int[]? _light;

    private static byte[] _kinds = [];
    private static double _x, _z, _threshold = double.MaxValue, _refinedX = double.NaN, _refinedZ = double.NaN;
    private static long _refinedAt;
    private static ICoreClientAPI? _api;
    private static Block? _air;
    private static long _tick;

    // 0 off, else the size in pixels below which a block counts as far
    public static int Pixels { get; set; } = 2;
    public static bool Installed { get; private set; }
    public static int FarChunks => Table.FarChunks; // drawn coarse at the last round
    public static int FarthestChunks => Table.FarthestChunks; // of those, at a quarter
    public static double Threshold { get; private set; } = double.NaN;

    // True on a tessellation thread while it builds a coarse chunk (FarFoliage leaves it alone then)
    internal static bool Coarse => _coarse;

    // The kind table as Classify builds it (tests)
    internal static byte[] Kinds { get => _kinds; set => _kinds = value ?? []; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "vars")]
    private static extern ref TCTCache? Vars(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModeldataByRenderPassByLodLevel")]
    private static extern ref MeshData[][][]? Centre(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModeldataByRenderPassByLodLevel")]
    private static extern ref MeshData[][][]? Edges(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockEntitiesOfChunk")]
    private static extern ref Dictionary<BlockPos, BlockEntity>? Entities(TCTCache vars);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkDrawFluids")]
    private static extern ref byte[]? DrawFluids(ChunkTesselator tesselator);

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger? logger = null)
    {
        Installed = false;
        var tesselator = typeof(ChunkTesselator);
        var pass = Method(tesselator, nameof(ChunkTesselator.NowProcessChunk), typeof(int), typeof(int), typeof(int),
            typeof(TesselatedChunk), typeof(bool));
        var extended = Method(tesselator, "BuildExtendedChunkData", typeof(ClientChunk), typeof(int), typeof(int), typeof(int),
            typeof(bool), typeof(bool));
        var polygons = Method(tesselator, nameof(ChunkTesselator.BuildBlockPolygons), typeof(int), typeof(int), typeof(int));
        var decor = AccessTools.DeclaredMethod(tesselator, "BuildDecorPolygons");
        if (!NotNull(harmony) || !NotNull(api) || pass is null || extended is null || polygons is null || decor is null ||
            !Fields(nameof(FarLod), logger, "far chunks are drawn in full",
            [
                (tesselator, "vars", typeof(TCTCache)), (tesselator, "centerModeldataByRenderPassByLodLevel", typeof(MeshData[][][])),
                (tesselator, "edgeModeldataByRenderPassByLodLevel", typeof(MeshData[][][])),
                (typeof(TCTCache), "blockEntitiesOfChunk", typeof(Dictionary<BlockPos, BlockEntity>)),
                (tesselator, "currentChunkDrawFluids", typeof(byte[]))
            ]) || !Accessible(nameof(FarLod), logger)) return;
        _ = harmony.Patch(pass, new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(Begin))) { priority = Priority.First + 50 },
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(End))));
        _ = harmony.Patch(extended, postfix: new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(Coarsen))));
        _ = harmony.Patch(polygons, new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(Inside))),
            new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(Scaled))));
        _ = harmony.Patch(decor, new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(NoDecor))));
        (_api, _air) = (api, api.World.GetBlock(0));
        _tick = api.Event.RegisterGameTickListener(Scan, EveryMs);
        Installed = NotNull(_air);
    }

    public static void Stop()
    {
        Komet.Core.Listeners.Unregister(_api?.Event, _tick);
        // _air too: a Block holds its api, so the left world's ClientMain would outlive it until the next Install
        (_api, _air, _tick, Installed, Threshold, _kinds, _threshold) = (null, null, 0, false, double.NaN, [], double.MaxValue);
        Table.Clear();
    }

    // Distance in blocks where a block covers `pixels` pixels
    internal static double Distance(double height, double fovDegrees, int pixels) =>
        Assert(pixels is >= 0 and <= MaxPixels) ? PixelBand.Distance(height, fovDegrees, pixels, MinDistance) : double.NaN;

    // A column's centre distance stretched or shrunk by up to Ragged, fixed per column: the bands' borders fray over a
    // zone instead of drawing one circle around the player where plants and detail end at once
    internal static double Spread(int cx, int cz, double x, double z) =>
        ChunkKey.CentreDistance(cx, cz, x, z) * (1 + Ragged * Jitter(cx, cz));

    // -1..1, the same for a column every time
    internal static double Jitter(int cx, int cz)
    {
        var h = unchecked((uint)(cx * 73856093) ^ (uint)(cz * 19349663));
        h ^= h >> 13;
        h = unchecked(h * 0x5BD1E995);
        h ^= h >> 15;
        return (h & 0xFFFF) / 32767.5 - 1;
    }

    // Band wanted at centre distance d given the current band (0: none), with hysteresis: Farthest past twice the
    // threshold, Far past it, else Near
    internal static int Want(double d, int band, double threshold)
    {
        var outer = band switch { 0 => 0, Farthest => Far, _ => Near };
        if (PixelBand.Want(d, outer, 2 * threshold, Band) == Far) return Farthest;
        var inner = band switch { 0 => 0, Near => Near, _ => Far };
        return PixelBand.Want(d, inner, threshold, Band);
    }

    // The band a chunk has (tessellated) or would get now
    private static int BandOf(int cx, int cy, int cz)
    {
        if (ChunkKey.Pack(cx, cy, cz) is var key && key >= 0 && Table.Bands.TryGetValue(key, out var band)) return band;
        var threshold = Volatile.Read(ref _threshold);
        return Want(Spread(cx, cz, Volatile.Read(ref _x), Volatile.Read(ref _z)), 0, threshold);
    }

    // NowProcessChunk prefix: the band; a coarse pass, and any pass changing band, is whole and a coarse one sees no
    // block entities. A pass without bands (off, another dimension, no threshold yet) leaves the chunk untracked, and
    // whole where it was drawn coarse.
    private static void Begin(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, ref bool skipChunkCenter)
    {
        (_coarse, _skirted) = (false, false);
        var (key, threshold) = (ChunkKey.Pack(chunkX, chunkY, chunkZ), Volatile.Read(ref _threshold));
        if (key < 0) return;
        if (Pixels <= 0 || !Installed || chunkY >= Dimension || threshold >= double.MaxValue || Vars(__instance) is not { } vars)
        {
            // a chunk drawn coarse leaves its band in full: an edge-only pass would keep the coarse centre untracked
            if (skipChunkCenter && Table.DrawnBand(key) >= Far) skipChunkCenter = false;
            Table.Forget(key);
            return;
        }

        var band = Table.Bands.TryGetValue(key, out var held) ? held : 0;
        var drawn = Table.Drawn.TryRemove(key, out var was) ? was : band; // Bands may hold the band Scan announced
        var want = Want(Spread(chunkX, chunkZ, Volatile.Read(ref _x), Volatile.Read(ref _z)), band, threshold);
        Table.Bands[key] = (byte)want;
        _ = Table.Pending.TryRemove(key, out _);
        (_chunkX, _chunkY, _chunkZ) = (chunkX, chunkY, chunkZ);
        // an edge-only pass (the engine drops a mark while one is queued) would leave the centre in the old band
        if (drawn != 0 && want != drawn) skipChunkCenter = false;
        if (want == Near)
        {
            _skirted = true; // drawn in full: its sides facing coarse neighbours stay open
            return;
        }

        (_coarse, _band, _chunkX, _chunkY, _chunkZ, skipChunkCenter) = (true, want, chunkX, chunkY, chunkZ, false);
        _entities = Entities(vars);
        Entities(vars) = null;
    }

    private static Exception? End(ChunkTesselator __instance, Exception? __exception)
    {
        if (_coarse && Vars(__instance) is { } vars) Entities(vars) = _entities;
        (_coarse, _skirted, _entities) = (false, false, null);
        return __exception;
    }

    private static bool NoDecor() => !_coarse;

    // BuildExtendedChunkData postfix: the coarse grid into the extended arrays; a full chunk's border layer facing a
    // coarse neighbour turned air, so its side is drawn where the coarse cells left air (else a hole into it, black)
    private static void Coarsen(ChunkTesselator __instance)
    {
        if (_skirted && !_coarse && _air is { } plain) Skirted(__instance, plain);
        if (!_coarse || _air is not { } air) return;
        if (BlocksExt(__instance) is not { Length: Ext * Ext * Ext } blocks || FluidsExt(__instance) is not { Length: Ext * Ext * Ext } fluids ||
            RgbsExt(__instance) is not { Length: Ext * Ext * Ext } light)
        {
            _coarse = false; // an unexpected layout: drawn in full, with the block entities Begin took (End restores only coarse)
            if (Vars(__instance) is { } vars) Entities(vars) = _entities;
            return;
        }

        var (sb, sf, sl) = (_blocks ??= new Block[Ext * Ext * Ext], _fluids ??= new Block[Ext * Ext * Ext], _light ??= new int[Ext * Ext * Ext]);
        // walls where a side's neighbour is drawn at another resolution: -x, +x, -z, +z
        var open = (BandOf(_chunkX - 1, _chunkY, _chunkZ) != _band, BandOf(_chunkX + 1, _chunkY, _chunkZ) != _band,
            BandOf(_chunkX, _chunkY, _chunkZ - 1) != _band, BandOf(_chunkX, _chunkY, _chunkZ + 1) != _band);
        var grid = new Grid(blocks, fluids, light, sb, sf, sl);
        Halve(grid, air, Cells, Ext - 1, open);
        if (_band == Farthest) Halve(grid, air, Cells / 2, Cells + 1, open); // the 16³ grid once more, its border at 17
    }

    private readonly record struct Grid(Block[] Blocks, Block[] Fluids, int[] Light, Block[] SourceBlocks, Block[] SourceFluids,
        int[] SourceLight);

    // The grid's cells 0..last halved into cells 0..cells+1 of the same arrays, the rest air
    private static void Halve(Grid grid, Block air, int cells, int last, (bool, bool, bool, bool) open)
    {
        var (blocks, fluids, light, sb, sf, sl) = grid;
        blocks.CopyTo(sb, 0);
        fluids.CopyTo(sf, 0);
        light.CopyTo(sl, 0);
        Array.Fill(blocks, air);
        Array.Fill(fluids, air);
        // a cell lit nowhere (all its blocks solid ones that hold no light, as a border layer inside the neighbour's
        // ground) takes the light of the first lit cell above it, else faces next to it draw black: top-down, each
        // column carries the light of the cell above
        Span<int> above = stackalloc int[(Cells + 2) * (Cells + 2)];
        above.Clear();
        for (var down = 0; down <= Math.Min(cells + 1, Cells + 1); down++)
            for (var z = 0; z <= Math.Min(cells + 1, Cells + 1); z++)
                for (var x = 0; x <= Math.Min(cells + 1, Cells + 1); x++)
                {
                    var (y, column) = (cells + 1 - down, z * (cells + 2) + x);
                    var at = (y * Ext + z) * Ext + x;
                    var lit = Brightest(sl, x, y, z, cells, last);
                    if (lit == 0) lit = above[column];
                    (above[column], light[at]) = (lit, lit);
                    var wall = (x == 0 && open.Item1) || (x == cells + 1 && open.Item2) || (z == 0 && open.Item3) ||
                               (z == cells + 1 && open.Item4);
                    if (wall) continue;
                    blocks[at] = Cell(sb, x, y, z, false, cells, last) ?? air;
                    fluids[at] = Cell(sf, x, y, z, true, cells, last) ?? air;
                }
    }

    // BuildBlockPolygons prefix: a coarse chunk draws its 16³ (farthest 8³) cells only. The border layer past them (the
    // neighbours' cells above, +x and +z) hides the faces it covers but would draw its own inside the neighbour once scaled.
    private static void Inside(ChunkTesselator __instance)
    {
        if (!_coarse) return;
        var cells = _band == Farthest ? Cells / 2 : Cells;
        if (Draw(__instance) is { Length: Size * Size * Size } draw) ClearPastCells(draw, cells);
        if (DrawFluids(__instance) is { Length: Size * Size * Size } fluids) ClearPastCells(fluids, cells);
    }

    // Clears the visible-face bytes, (y * 32 + z) * 32 + x, of every cell with a local x, y or z of `cells` or more
    internal static void ClearPastCells(byte[] draw, int cells = Cells)
    {
        if (!Assert(draw.Length == Size * Size * Size) || !Assert(cells is > 0 and <= Cells)) return;
        draw.AsSpan(cells * Size * Size).Clear(); // y >= cells
        for (var y = 0; y < Math.Min(cells, Cells); y++)
        {
            draw.AsSpan((y * Size + cells) * Size, (Size - cells) * Size).Clear(); // z >= cells
            for (var z = 0; z < Math.Min(cells, Cells); z++) draw.AsSpan((y * Size + z) * Size + cells, Size - cells).Clear(); // x >= cells
        }
    }

    private static void Skirted(ChunkTesselator tesselator, Block air)
    {
        if (BlocksExt(tesselator) is not { Length: Ext * Ext * Ext } blocks || FluidsExt(tesselator) is not { Length: Ext * Ext * Ext } fluids)
            return;
        var sides = (BandOf(_chunkX - 1, _chunkY, _chunkZ) != Near, BandOf(_chunkX + 1, _chunkY, _chunkZ) != Near,
            BandOf(_chunkX, _chunkY, _chunkZ - 1) != Near, BandOf(_chunkX, _chunkY, _chunkZ + 1) != Near);
        if (!sides.Item1 && !sides.Item2 && !sides.Item3 && !sides.Item4) return;
        for (var y = 0; y < Ext; y++)
            for (var i = 0; i < Ext; i++)
            {
                if (sides.Item1) (blocks[(y * Ext + i) * Ext], fluids[(y * Ext + i) * Ext]) = (air, air);
                if (sides.Item2) (blocks[(y * Ext + i) * Ext + Ext - 1], fluids[(y * Ext + i) * Ext + Ext - 1]) = (air, air);
                if (sides.Item3) (blocks[y * Ext * Ext + i], fluids[y * Ext * Ext + i]) = (air, air);
                if (sides.Item4) (blocks[(y * Ext + Ext - 1) * Ext + i], fluids[(y * Ext + Ext - 1) * Ext + i]) = (air, air);
            }
    }

    // Source coordinates of a coarse one on one axis, of `cells` cells halving a grid whose border layer is at `last`:
    // the border layer, or the cell's two
    private static (int From, int Count) Source(int c, int cells, int last)
    {
        if (c == 0) return (0, 1);
        if (c == cells + 1) return (last, 1);
        return (2 * c - 1, 2);
    }

    // The cell's top-most block of a kind where at least half of it holds one, else null
    internal static Block? Cell(Block[] source, int x, int y, int z, bool fluid, int cells = Cells, int last = Ext - 1)
    {
        var ((x0, nx), (y0, ny), (z0, nz)) = (Source(x, cells, last), Source(y, cells, last), Source(z, cells, last));
        var (count, sparse, total) = (0, 0, nx * ny * nz);
        Span<int> layers = stackalloc int[6]; // solid blocks per layer: y 0, 1; z 0, 1; x 0, 1
        layers.Clear();
        Block? top = null;
        for (var dy = 0; dy < Math.Min(ny, 2); dy++)
            for (var dz = 0; dz < Math.Min(nz, 2); dz++)
                for (var dx = 0; dx < Math.Min(nx, 2); dx++)
                {
                    var block = source[((y0 + dy) * Ext + z0 + dz) * Ext + x0 + dx];
                    if (block is null || !(fluid ? block.BlockId != 0 && block.IsLiquid() : Solidish(block))) continue;
                    count++;
                    (layers[dy], layers[2 + dz], layers[4 + dx]) = (layers[dy] + 1, layers[2 + dz] + 1, layers[4 + dx] + 1);
                    if (!fluid && KindOf(block) == Sparse) sparse++;
                    top = block; // y runs upward last-written wins: the top-most
                }

        // a border cell sees only the neighbour's adjacent layer, where the neighbour's own cell weighs two: solid only
        // when that layer is all solid (then the neighbour's cell is too), else this side's face is drawn, never a hole
        if (nx == 1 || ny == 1 || nz == 1) return count == total ? top : null;
        // a trunk or a thin crown fills a quarter of a cell: kept, or far trees float as loose leaves; a whole layer (a
        // ground one block thick over a cave, a thin wall) keeps the cell too, else one looks into the dark below
        var layered = false;
        foreach (var n in layers.Bounded(6)) layered |= n == 4;
        return (2 * count >= total || (sparse > 0 && 4 * count >= total) || layered) ? top : null;
    }

    // Brightest of the cell's light, byte by byte
    internal static int Brightest(int[] source, int x, int y, int z, int cells = Cells, int last = Ext - 1)
    {
        var ((x0, nx), (y0, ny), (z0, nz)) = (Source(x, cells, last), Source(y, cells, last), Source(z, cells, last));
        uint best = 0;
        for (var dy = 0; dy < Math.Min(ny, 2); dy++)
            for (var dz = 0; dz < Math.Min(nz, 2); dz++)
                for (var dx = 0; dx < Math.Min(nx, 2); dx++)
                {
                    var v = (uint)source[((y0 + dy) * Ext + z0 + dz) * Ext + x0 + dx];
                    best = Math.Max(best & 0xFF, v & 0xFF) | Math.Max(best & 0xFF00, v & 0xFF00) |
                           Math.Max(best & 0xFF0000, v & 0xFF0000) | Math.Max(best & 0xFF000000, v & 0xFF000000);
                }

        return (int)best;
    }

    // Blocks a coarse cell may become: whole cubes, topsoil, snow layers, leaves; nothing with a block entity
    private static bool Solidish(Block block) => KindOf(block) is Solid or Sparse;

    private static byte KindOf(Block block)
    {
        var id = block.BlockId;
        if (id == 0) return Air;
        var kinds = _kinds;
        if ((uint)id >= (uint)kinds.Length) kinds = Classify(id);
        return (uint)id < (uint)kinds.Length ? kinds[id] : Air;
    }

    private static byte[] Classify(int id)
    {
        if (_api?.World?.Blocks is not { } all || !Index(id, MaxKinds)) return [];
        var kinds = new byte[Math.Max(all.Count, id + 1)];
        for (var i = 0; i < Math.Min(all.Count, MaxKinds); i++)
            if (all[i] is { } b && b.BlockId == i) kinds[i] = Kind(b);
        _kinds = kinds;
        return kinds;
    }

    internal static byte Kind(Block b)
    {
        if (b.BlockId == 0 || b.EntityClass is not null) return Air;
        var leaves = b.DrawType == EnumDrawType.JSON && b.BlockMaterial == EnumBlockMaterial.Leaves;
        var solid = b.DrawType is EnumDrawType.Cube or EnumDrawType.TopSoil ||
                    (b.DrawType is >= EnumDrawType.BlockLayer_1 and <= EnumDrawType.BlockLayer_7);
        if (leaves || (solid && b.BlockMaterial == EnumBlockMaterial.Wood)) return Sparse;
        return solid ? Solid : Air;
    }

    // BuildBlockPolygons postfix: the coarse meshes and bounds at full size
    private static void Scaled(ChunkTesselator __instance)
    {
        if (!_coarse || Vars(__instance) is not { } vars) return;
        var f = _band == Farthest ? 4 : 2;
        Scale(Centre(__instance), f);
        Scale(Edges(__instance), f);
        (vars.xMin, vars.xMax, vars.yMin, vars.yMax, vars.zMin, vars.zMax) =
            (vars.xMin * f, vars.xMax * f, vars.yMin * f, vars.yMax * f, vars.zMin * f, vars.zMax * f);
    }

    private static void Scale(MeshData[][][]? byLod, int factor)
    {
        if (byLod is null) return;
        foreach (var byPass in byLod.Bounded(8))
        {
            if (byPass is null) continue;
            foreach (var byAtlas in byPass.Bounded(64))
            {
                if (byAtlas is null) continue;
                foreach (var mesh in byAtlas.Bounded(64))
                {
                    if (mesh?.xyz is not { } xyz) continue;
                    var n = Math.Min(3 * mesh.VerticesCount, xyz.Length);
                    for (var i = 0; i < Math.Min(n, 3 << 22); i++) xyz[i] *= factor;
                }
            }
        }
    }

    // Main thread: the player and threshold, then a slice of the round re-marked where a chunk's band changed. The
    // band is announced at the mark, so neighbours tessellated before the chunk's own pass close their sides to it,
    // while the table keeps the band drawn until that pass.
    private static void Scan(float dt)
    {
        if (_api is not { World.Player.Entity: { } entity } api || api.World is not ClientMain game) return;
        var threshold = Distance(api.Render.FrameHeight * FrameSettings.Ssaa, FrameSettings.FieldOfView, Pixels);
        var (px, pz) = (entity.Pos.X, entity.Pos.Z);
        Threshold = threshold;
        Volatile.Write(ref _x, px);
        Volatile.Write(ref _z, pz);
        var published = double.IsFinite(threshold) ? threshold : double.MaxValue;
        Volatile.Write(ref _threshold, published);
        if (Pixels <= 0 && Table.Bands.IsEmpty) return; // off, every chunk drawn in full
        var (marks, now, busy) = (0, Environment.TickCount64, TessSeams.Backlogged);
        if (Pixels > 0) Refine(game, px, pz, threshold, now);
        Table.Begin(px, pz, published, now);
        for (var n = 0; n < PerScan && marks < MaxMarks && Table.TryNext(game, now, out var key, out var band); n++)
        {
            if (band == 0) continue;
            var (x, y, z) = ChunkKey.Unpack(key);
            var want = Pixels > 0 ? Want(Spread(x, z, px, pz), band, threshold) : Near;
            if (want == band && Pixels <= 0) Table.Forget(key); // off: drawn in full, no longer watched
            if (want == band || (want > band && busy)) // coarser waits for the backlog
            {
                // a mark whose pass never came: neighbours face the band drawn again
                if (Table.Drawn.ContainsKey(key)) Table.Announce(key, band);
                continue;
            }

            Table.Announce(key, (byte)want);
            if (Table.Mark(game, x, y, z, now) == 0) continue;
            // the neighbours facing it follow its change: a coarse one's walls, a full one's open side. While off a
            // near neighbour may already be forgotten this round, so all four are marked to close their sides.
            marks += 1 + Neighbour(game, x - 1, y, z, now) + Neighbour(game, x + 1, y, z, now) +
                     Neighbour(game, x, y, z - 1, now) + Neighbour(game, x, y, z + 1, now);
        }
    }

    // Close terrain first: the round walks every tracked chunk in no order and over a far view takes seconds, so a chunk
    // the player came near stayed coarse that long. Columns inside the near disc are checked ring by ring from the
    // player, each one drawn coarser than wanted re-marked at once on a budget of its own; settled until the player
    // moves a few blocks (or a second passes), again next tick while the budget ran out.
    private static void Refine(ClientMain game, double px, double pz, double threshold, long now)
    {
        if (!double.IsFinite(threshold) || threshold <= 0) return;
        var moved = !(Math.Abs(px - _refinedX) < Band / 4 && Math.Abs(pz - _refinedZ) < Band / 4);
        if (!moved && now - _refinedAt < RefineMs) return;
        var (reach, height) = ((int)Math.Min(Math.Ceiling(threshold / (1 - Ragged) / Size) + 1, MaxReach), game.WorldMap.ChunkMapSizeY);
        var (ox, oz, marks) = ((int)Math.Floor(px / Size), (int)Math.Floor(pz / Size), 0);
        for (var r = 0; r <= Math.Min(reach, MaxReach) && marks < MaxRefines; r++)
            for (var j = 0; j < Math.Min(2 * r + 1, 2 * MaxReach + 1) && marks < MaxRefines; j++)
                for (var side = 0; side < 4 && marks < MaxRefines; side++)
                {
                    var i = j - r;
                    // the ring's four sides; the corners belong to the first two only
                    if (r > 0 && side >= 2 && (i == -r || i == r)) continue;
                    var (cx, cz) = side switch { 0 => (ox + i, oz - r), 1 => (ox + i, oz + r), 2 => (ox - r, oz + i), _ => (ox + r, oz + i) };
                    if (r == 0 && side > 0) break;
                    var d = Spread(cx, cz, px, pz);
                    if (d >= threshold) continue;
                    for (var cy = 0; cy < Math.Min(height, Dimension) && marks < MaxRefines; cy++)
                        marks += Refined(game, cx, cy, cz, d, threshold, now);
                }

        if (marks < MaxRefines) (_refinedX, _refinedZ, _refinedAt) = (px, pz, now);
    }

    // A chunk drawn coarser than wanted at distance d marked with its banded neighbours: the marks made
    private static int Refined(ClientMain game, int x, int y, int z, double d, double threshold, long now)
    {
        var key = ChunkKey.Pack(x, y, z);
        if (key < 0 || !Table.Bands.ContainsKey(key) || Table.Waiting(key, now)) return 0;
        var drawn = Table.DrawnBand(key);
        if (drawn <= Near || Want(d, drawn, threshold) is var want && want >= drawn) return 0;
        Table.Announce(key, (byte)want);
        if (Table.Mark(game, x, y, z, now) == 0) return 0;
        return 1 + Neighbour(game, x - 1, y, z, now) + Neighbour(game, x + 1, y, z, now) +
               Neighbour(game, x, y, z - 1, now) + Neighbour(game, x, y, z + 1, now);
    }

    private static int Neighbour(ClientMain game, int x, int y, int z, long now) =>
        ChunkKey.Pack(x, y, z) is var key && key >= 0 && (Pixels <= 0 || Table.Bands.ContainsKey(key))
            ? Table.Mark(game, x, y, z, now)
            : 0;
}
