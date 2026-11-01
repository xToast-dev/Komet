using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;
using Komet.Diagnostics;

namespace Komet.Tessellation;

// Past where a block covers under Pixels pixels (window height x resolution scale, FOV), leaves thin to a 3D
// checkerboard and plants (Cross, collision-free JSON plants) drop; leaf blocks are two crossed quads two blocks wide,
// so the outline stays at half the triangles and overdraw (far foliage is over half of OpaqueNoCull at a long view
// distance). The band comes from the chunk centre distance at pass start; edge-only passes keep it. Only chunks with
// foliage are tracked. Scan re-marks chunks whose band no longer fits, with hysteresis, a slice every 50 ms
// (ChunkBands). Dimension 0.
internal static class FarFoliage
{
    public const int MaxPixels = 2, Engine = 0;
    private const int None = 0, Leaves = 1, Plant = 2, Near = ChunkBands.Near, Far = ChunkBands.Far, Dimension = 1024;
    private const int MaxKinds = 1 << 20, EveryMs = 50, PerScan = 2048, ScanMarks = 26; // about 500 marks a second at most
    internal const double Band = 32;
    private const double MinDistance = 128;
    private const int ThinnedCounter = 0, DroppedCounter = 1, MarkedCounter = 2, Counters = 3;

    private static readonly Tally Counts = new(Counters);
    private static readonly ChunkBands Table = new();

    [ThreadStatic] private static bool _active, _far, _seen;
    [ThreadStatic] private static long _key;

    private static byte[] _kinds = [];
    private static double _x, _z, _threshold = double.MaxValue;
    private static ICoreClientAPI? _api;
    private static long _tick;

    // 0 off, else the size in pixels below which a block counts as far
    public static int Pixels { get; set; } = 1;
    public static bool Installed { get; private set; }

    public static long Thinned => Counts.Total(ThinnedCounter);
    public static long Dropped => Counts.Total(DroppedCounter);
    public static long Marked => Counts.Total(MarkedCounter);
    // Of the chunks with foliage, those drawn far at the last scan
    public static int FarChunks => Table.FarChunks;

    // Where the far band begins this frame, in blocks; NaN while off
    public static double Threshold { get; private set; } = double.NaN;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "vars")]
    private static extern ref TCTCache? Vars(ChunkTesselator tesselator);

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger? logger = null)
    {
        Installed = false;
        var tesselator = typeof(ChunkTesselator);
        var pass = Method(tesselator, nameof(ChunkTesselator.NowProcessChunk), typeof(int), typeof(int), typeof(int),
            typeof(TesselatedChunk), typeof(bool));
        var block = Method(tesselator, "TesselateBlock", typeof(Block), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int));
        if (!NotNull(harmony) || !NotNull(api) || pass is null || block is null ||
            !Fields(nameof(FarFoliage), logger, "foliage is drawn in full at every distance",
                [(tesselator, "vars", typeof(TCTCache))]) ||
            !Accessible(nameof(FarFoliage), logger)) return;
        _ = harmony.Patch(pass, new HarmonyMethod(Begin) { priority = Priority.First }, new HarmonyMethod(End));
        _ = harmony.Patch(block, new HarmonyMethod(Prefix));
        _api = api;
        _tick = api.Event.RegisterGameTickListener(Scan, EveryMs);
        Installed = true;
    }

    public static void Stop()
    {
        Komet.Core.Listeners.Unregister(_api?.Event, _tick);
        (_api, _tick, _kinds, Installed, Threshold, _threshold) = (null, 0, [], false, double.NaN, double.MaxValue);
        Table.Clear();
    }

    // Distance in blocks where a block covers `pixels` pixels
    internal static double Distance(double height, double fovDegrees, int pixels) =>
        Assert(pixels <= MaxPixels) ? PixelBand.Distance(height, fovDegrees, pixels, MinDistance) : double.NaN;

    // NowProcessChunk's prefix and postfix, on the tessellation thread
    private static void Begin(int chunkX, int chunkY, int chunkZ, bool skipChunkCenter)
    {
        (_key, _seen) = (ChunkKey.Pack(chunkX, chunkY, chunkZ), false);
        var threshold = Volatile.Read(ref _threshold);
        _active = Pixels > 0 && Installed && chunkY < Dimension && _key >= 0 && threshold < double.MaxValue;
        if (!_active) return;
        if (skipChunkCenter && Table.Bands.TryGetValue(_key, out var band))
        {
            _far = band == Far;
            return;
        }

        var d = ChunkKey.CentreDistance(chunkX, chunkZ, Volatile.Read(ref _x), Volatile.Read(ref _z));
        _far = PixelBand.Want(d, 0, threshold, Band) == Far;
    }

    private static void End(bool skipChunkCenter)
    {
        if (_active && (_seen || (skipChunkCenter && Table.Bands.ContainsKey(_key))))
            Table.Bands[_key] = _far ? ChunkBands.Far : ChunkBands.Near;
        else _ = Table.Bands.TryRemove(_key, out _);
        _ = Table.Pending.TryRemove(_key, out _);
        _active = false;
    }

    // TesselateBlock prefix: far plants, and far leaves on odd 3D checkerboard cells, are not drawn
    private static bool Prefix(ChunkTesselator __instance, Block block, int posX, int posZ)
    {
        if (!_active || block is null || FarLod.Coarse) return true; // a coarse chunk is thinned already
        var kind = KindOf(block);
        if (kind == None) return true;
        _seen = true;
        if (!_far) return true;
        if (kind == Leaves && (Vars(__instance) is not { } vars || ((posX + vars.posY + posZ) & 1) == 0)) return true;
        if (Counting.On) Counts.Add(kind == Leaves ? ThinnedCounter : DroppedCounter, 1);
        return false;
    }

    private static int KindOf(Block block)
    {
        var kinds = _kinds;
        var id = block.BlockId;
        if ((uint)id >= (uint)kinds.Length) kinds = Classify(id);
        return (uint)id < (uint)kinds.Length ? kinds[id] : None;
    }

    // Every block's kind, rebuilt when an id lies past the table (swapped whole; threads read either)
    private static byte[] Classify(int id)
    {
        if (_api?.World?.Blocks is not { } blocks || !Index(id, MaxKinds)) return [];
        var kinds = new byte[Math.Max(blocks.Count, id + 1)];
        for (var i = 0; i < Math.Min(blocks.Count, MaxKinds); i++)
            if (blocks[i] is { } b && b.BlockId == i) kinds[i] = (byte)Kind(b);
        _kinds = kinds;
        return kinds;
    }

    internal static int Kind(Block b)
    {
        if (b.RenderPass != EnumChunkRenderPass.OpaqueNoCull) return None;
        if (b.DrawType == EnumDrawType.JSON && b.BlockMaterial == EnumBlockMaterial.Leaves) return Leaves;
        if (b.DrawType == EnumDrawType.Cross) return Plant;
        var walkable = b.CollisionBoxes is { Length: > 0 };
        return b.DrawType == EnumDrawType.JSON && b.BlockMaterial == EnumBlockMaterial.Plant && !walkable
            ? Plant
            : None;
    }

    // The main thread's half: publish the player and the thresholds, then mark the chunks whose band no longer fits
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
        if (Pixels <= 0 && Table.Bands.IsEmpty) return; // off, all foliage drawn in full
        var (marks, now, busy) = (0, Environment.TickCount64, TessSeams.Backlogged);
        Table.Begin(px, pz, published, now);
        for (var n = 0; n < PerScan && marks < ScanMarks && Table.TryNext(game, now, out var key, out var band); n++)
        {
            if (band == 0) continue;
            var (x, y, z) = ChunkKey.Unpack(key);
            var d = ChunkKey.CentreDistance(x, z, px, pz);
            var want = Pixels > 0 ? PixelBand.Want(d, band, threshold, Band) : Near;
            if (want == band && Pixels <= 0) Table.Forget(key); // off: drawn in full, no longer watched
            if (want == band || (want == Far && busy)) continue; // leaner waits for the backlog
            marks += Table.Mark(game, x, y, z, now);
        }

        if (marks > 0 && Counting.On) Counts.Add(MarkedCounter, marks);
    }
}
