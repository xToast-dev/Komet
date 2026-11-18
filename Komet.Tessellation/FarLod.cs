using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Dry chunks past the pixel threshold use a 16³ grid, or an 8³ grid past twice that distance. The engine
// tessellates that grid and we scale its meshes back to chunk size. A coarse chunk draws walls where it meets
// a different resolution. The one-block halo supplies the engine's liquid corner heights, including diagonals,
// so a chunk with liquid anywhere in the extended grid keeps full resolution and the halo samples used by its liquids.
internal static partial class FarLod
{
    public const int MaxPixels = 3;

    private const int Size = 32,
        Ext = 34,
        Cells = 16,
        Near = ChunkBands.Near,
        Far = ChunkBands.Far,
        Farthest = ChunkBands.Farthest,
        FullDetail = ChunkBands.FullDetail,
        Dimension = 1024;

    private const int MaxMarks = 14,
        MaxUnannounced = 256,
        MaxKinds = 1 << 20,
        EveryMs = 100,
        PerScan = 4096,
        MaxRefines = 64,
        RefineMs = 1000,
        MaxReach = 96;

    private const double Band = 48, MinDistance = 256, Ragged = 0.2;
    private const byte Air = 1, Solid = 2, Sparse = 3; // Sparse: leaves and wood, a quarter of a cell is enough
    private static readonly ConcurrentQueue<long> Unannounced = new();

    private static readonly ChunkBands Table = new();

    [ThreadStatic] private static bool _tracked;
    [ThreadStatic] private static int _chunkX, _chunkZ, _chunkY, _band, _targetBand;
    [ThreadStatic] private static Dictionary<BlockPos, BlockEntity>? _entities;
    [ThreadStatic] private static Block[]? _blocks;
    [ThreadStatic] private static int[]? _light;

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
    [field: ThreadStatic] internal static bool Coarse { get; private set; }

    // The kind table as Classify builds it (tests)
    internal static byte[] Kinds { get; set; } = [];

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "vars")]
    private static extern ref TCTCache? Vars(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModeldataByRenderPassByLodLevel")]
    private static extern ref MeshData?[]?[]?[]? Centre(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModeldataByRenderPassByLodLevel")]
    private static extern ref MeshData?[]?[]?[]? Edges(ChunkTesselator tesselator);

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
        var extended = Method(tesselator, "BuildExtendedChunkData", typeof(ClientChunk), typeof(int), typeof(int),
            typeof(int),
            typeof(bool), typeof(bool));
        var polygons = Method(tesselator, nameof(ChunkTesselator.BuildBlockPolygons), typeof(int), typeof(int),
            typeof(int));
        var decor = AccessTools.DeclaredMethod(tesselator, "BuildDecorPolygons");
        if (pass is null || extended is null || polygons is null ||
            decor is null ||
            !Fields(nameof(FarLod), logger, "far chunks are drawn in full",
            [
                (tesselator, "vars", typeof(TCTCache)),
                (tesselator, "centerModeldataByRenderPassByLodLevel", typeof(MeshData[][][])),
                (tesselator, "edgeModeldataByRenderPassByLodLevel", typeof(MeshData[][][])),
                (typeof(TCTCache), "blockEntitiesOfChunk", typeof(Dictionary<BlockPos, BlockEntity>)),
                (tesselator, "currentChunkDrawFluids", typeof(byte[]))
            ]) || !Accessible(nameof(FarLod), logger))
        {
            return;
        }

        _ = harmony.Patch(pass,
            new HarmonyMethod(AccessTools.Method(typeof(FarLod), nameof(Begin))) { priority = Priority.First + 50 },
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
        Listeners.Unregister(_api?.Event, _tick);
        // _air too: a Block holds its api, so the left world's ClientMain would outlive it until the next Install
        (_api, _air, _tick, Installed, Threshold, Kinds, _threshold) =
            (null, null, 0, false, double.NaN, [], double.MaxValue);
        (_refinedX, _refinedZ, _refinedAt) = (double.NaN, double.NaN, 0);
        Table.Clear();
        Unannounced.Clear();
    }

    // Distance in blocks where a block covers `pixels` pixels
    internal static double Distance(double height, double fovDegrees, int pixels) =>
        Assert(pixels is >= 0 and <= MaxPixels)
            ? PixelBand.Distance(height, fovDegrees, pixels, MinDistance)
            : double.NaN;

    // A column's centre distance stretched or shrunk by up to Ragged, fixed per column: the bands' borders fray over a
    // zone instead of drawing one circle around the player where plants and detail end at once
    private static double Spread(int cx, int cz, double x, double z) =>
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

    private static int Resolution(int band) => band == FullDetail ? Near : band;

    private static bool IsCoarse(int band) => band is Far or Farthest;

    // Band wanted at centre distance d given the current band (0: none), with hysteresis: Farthest past twice the
    // threshold, Far past it, else Near
    internal static int Want(double d, int band, double threshold)
    {
        if (band == FullDetail) return FullDetail;

        var outer = band switch { 0 => 0, Farthest => Far, _ => Near };
        if (PixelBand.Want(d, outer, 2 * threshold, Band) == Far) return Farthest;

        var inner = band switch { 0 => 0, Near => Near, _ => Far };
        return PixelBand.Want(d, inner, threshold, Band);
    }

    // Whether a chunk borders one drawn at another resolution: its pass must run, its open sides draw the seam
    public static bool Seam(int cx, int cy, int cz)
    {
        if (Pixels <= 0 || Table.Bands.IsEmpty) return false;

        var band = BandOf(cx, cy, cz);
        return BandOf(cx - 1, cy, cz) != band || BandOf(cx + 1, cy, cz) != band || BandOf(cx, cy, cz - 1) != band ||
               BandOf(cx, cy, cz + 1) != band || BandOf(cx, cy - 1, cz) != band || BandOf(cx, cy + 1, cz) != band;
    }

    // The band a chunk has (tessellated) or would get now
    private static int BandOf(int cx, int cy, int cz)
    {
        if (ChunkKey.Pack(cx, cy, cz) is >= 0 and var key && Table.Bands.TryGetValue(key, out var band)) return Resolution(band);

        var threshold = Volatile.Read(ref _threshold);
        return Want(Spread(cx, cz, Volatile.Read(ref _x), Volatile.Read(ref _z)), 0, threshold);
    }

    // Force a full engine pass when switching resolution. The extended grid decides whether coarsening can
    // actually run, before the engine calculates either solid or fluid face visibility.
    private static void Begin(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, ref bool skipChunkCenter)
    {
        (Coarse, _tracked) = (false, false);
        var (key, threshold) = (ChunkKey.Pack(chunkX, chunkY, chunkZ), Volatile.Read(ref _threshold));
        if (key < 0) return;

        var drawn = Table.DrawnBand(key);
        if (Pixels <= 0 || !Installed || chunkY >= Dimension || threshold >= double.MaxValue ||
            Vars(__instance) is null)
        {
            if (IsCoarse(drawn)) skipChunkCenter = false;

            Table.Forget(key);
            return;
        }

        var held = Table.Bands.GetValueOrDefault(key);
        var band = skipChunkCenter ? held : Resolution(held);
        _band = _targetBand = Want(Spread(chunkX, chunkZ, Volatile.Read(ref _x), Volatile.Read(ref _z)), band, threshold);
        if (IsCoarse(_band) || IsCoarse(drawn)) skipChunkCenter = false;

        (_tracked, _chunkX, _chunkY, _chunkZ) = (true, chunkX, chunkY, chunkZ);
    }

    private static Exception? End(ChunkTesselator __instance, Exception? __exception)
    {
        if (Coarse && Vars(__instance) is { } vars) Entities(vars) = _entities;

        if (_tracked)
        {
            var key = ChunkKey.Pack(_chunkX, _chunkY, _chunkZ);
            if (__exception is null)
            {
                var held = Table.Bands.GetValueOrDefault(key);
                Table.Bands[key] = (byte)_band;
                _ = Table.Drawn.TryRemove(key, out _);
                // A late change (such as a wet halo overriding an announced coarse band) invalidates neighbour seams.
                if (Resolution(_band) != Resolution(held == 0 ? _targetBand : held) &&
                    Unannounced.Count < MaxUnannounced)
                {
                    Unannounced.Enqueue(key);
                }
            }

            _ = Table.Pending.TryRemove(key, out _);
        }

        (Coarse, _tracked, _entities) = (false, false, null);
        return __exception;
    }

    private static bool NoDecor() => !Coarse;
}
