using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Deep chunks hold cave walls nobody sees, a large share of the quads at a long view distance. A chunk Margin below all
// nine columns' surface (min of rain and worldgen height) and farther than Near (FarUnder with the player underground)
// tessellates empty like OccludedChunks. The main thread tracks Surfaces and the player and re-marks chunks changing
// side, with hysteresis, in slices every 20 ms (ChunkBands: full is the near band, culled the far one). Chunks
// tessellated before their columns' surfaces were known are found by a sweep over the map's chunks.
internal static class DeepCulling
{
    private const int Margin = 16, Near = 96, FarUnder = 256, Band = 48, Dimension = 1024, Size = 32;
    private const int EveryMs = 20, MaxMarks = 8, ColumnsPerScan = 24, TrackedPerScan = 2048, SweptPerScan = 192;
    private const byte Full = ChunkBands.Near, Culled = ChunkBands.Far;

    private static readonly ConcurrentDictionary<long, short> Surfaces = new(); // column -> lowest surface block
    private static readonly ChunkBands Table = new(); // deep chunk -> how tessellated

    private static ICoreClientAPI? _api;
    private static long _tick, _columnCursor;
    private static long[] _columns = [], _chunks = [];
    private static int _chunkCursor, _chunkCount, _columnCount;
    private static double _x, _z, _near = double.MaxValue;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static int CulledChunks => Table.FarChunks;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunks")]
    private static extern ref Dictionary<long, ClientChunk>? Chunks(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunksLock")]
    private static extern ref object? ChunksLock(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "MapChunks")]
    private static extern ref Dictionary<long, ClientMapChunk>? MapChunks(ClientWorldMap map);

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger? logger = null)
    {
        Installed = false;
        var (map, pass) = (typeof(ClientWorldMap), AccessTools.DeclaredMethod(typeof(ChunkTesselator),
            nameof(ChunkTesselator.NowProcessChunk), [typeof(int), typeof(int), typeof(int), typeof(TesselatedChunk), typeof(bool)]));
        if (!NotNull(harmony) || !NotNull(api) || pass is null || !TessSeams.Fields(nameof(DeepCulling), logger,
                "deep chunks keep their geometry",
                [
                    (map, "chunks", typeof(Dictionary<long, ClientChunk>)), (map, "chunksLock", typeof(object)),
                    (map, "MapChunks", typeof(Dictionary<long, ClientMapChunk>))
                ])) return;
        _ = harmony.Patch(pass, new HarmonyMethod(typeof(DeepCulling), nameof(Prefix)) { priority = Priority.High });
        _api = api;
        _tick = api.Event.RegisterGameTickListener(Scan, EveryMs);
        Installed = true;
    }

    public static void Stop()
    {
        Komet.Core.Listeners.Unregister(_api?.Event, _tick);
        (_api, _tick, Installed, _near) = (null, 0, false, double.MaxValue);
        (_chunkCursor, _chunkCount, _columnCursor, _columnCount) = (0, 0, 0, 0);
        (_chunks, _columns, _x, _z) = ([], [], 0, 0); // the snapshots (often on the LOH) die with the world
        Surfaces.Clear();
        Table.Clear();
    }

    private static long Column(int cx, int cz) => ((long)cz << 32) | (uint)cx;

    // The chunk's top lies Margin below the surface of the nine columns around it (false where one is not known yet)
    internal static bool Deep(int cx, int cy, int cz)
    {
        if (cy >= Dimension || !Assert(cx >= 0 && cz >= 0)) return false;
        var lowest = int.MaxValue;
        for (var dx = -1; dx <= 1; dx++)
            for (var dz = -1; dz <= 1; dz++)
            {
                if (!Surfaces.TryGetValue(Column(cx + dx, cz + dz), out var surface)) return false;
                lowest = Math.Min(lowest, surface);
            }

        return cy * Size + Size - 1 < lowest - Margin;
    }

    // Culled when deep and the player far; a culled chunk stays so until Band nearer (and the reverse)
    internal static bool Cull(int state, double distance, double near)
    {
        if (!double.IsFinite(near) || !Assert(state is >= 0 and <= Culled)) return false;
        return state == Culled ? distance > near : distance > near + Band;
    }

    // NowProcessChunk: a deep chunk far from the player is left without geometry, as an enclosed one. Every pass answers
    // the chunk's mark; one drawn in full for any reason but distance is no longer tracked, and a pass that ends a
    // culled state is never edge-only.
    private static bool Prefix(ChunkTesselator __instance, int chunkX, int chunkY, int chunkZ, TesselatedChunk tessChunk,
        ref bool skipChunkCenter, ref int __result)
    {
        var key = ChunkKey.Pack(chunkX, chunkY, chunkZ);
        if (key < 0 || !Installed) return true;
        _ = Table.Pending.TryRemove(key, out _);
        if (!NotNull(tessChunk)) return true;
        if (!Enabled || !OccludedChunks.Installed || OccludedChunks.StoodDown || !Deep(chunkX, chunkY, chunkZ))
        {
            // a culled chunk leaves the table drawn whole: an edge-only pass would keep its empty centre untracked
            if (Table.Bands.TryRemove(key, out var held) && held == Culled) skipChunkCenter = false;
            return true;
        }

        var state = Table.Bands.TryGetValue(key, out var was) ? was : 0;
        var distance = ChunkKey.CentreDistance(chunkX, chunkZ, Volatile.Read(ref _x), Volatile.Read(ref _z));
        var cull = Cull(state, distance, Volatile.Read(ref _near));
        Table.Bands[key] = cull ? Culled : Full;
        if (state == Culled && !cull) skipChunkCenter = false; // an edge-only pass would keep the empty centre
        if (!cull) return true;
        __result = OccludedChunks.Apply(__instance, chunkX, chunkY, chunkZ, tessChunk, skipChunkCenter);
        return false;
    }

    // The main thread: the player, a share of the columns' surfaces, and a slice of the chunks to tessellate again
    private static void Scan(float dt)
    {
        if (_api is not { World.Player.Entity: { } entity } api || api.World is not ClientMain game) return;
        var (px, py, pz) = (entity.Pos.X, entity.Pos.Y, entity.Pos.Z);
        var under = py < SurfaceAt(game, (int)px, (int)pz) - 2;
        Volatile.Write(ref _x, px);
        Volatile.Write(ref _z, pz);
        double near = under ? FarUnder : Near;
        if (!Enabled) near = double.MaxValue;
        Volatile.Write(ref _near, near);
        if (!Enabled && Table.Bands.IsEmpty) return; // off, every chunk drawn in full
        Refresh(game);
        var (now, busy) = (Environment.TickCount64, TessSeams.Backlogged);
        Table.Begin(px, pz, near, now);
        var marks = Slice(game, (px, pz), busy, now);
        if (Enabled && !busy) _ = Sweep(game, px, pz, marks, now);
    }

    // A slice of the round: chunks changing side are marked, up to MaxMarks
    private static int Slice(ClientMain game, (double X, double Z) at, bool busy, long now)
    {
        var marks = 0;
        for (var n = 0; n < TrackedPerScan && marks < MaxMarks && Table.TryNext(game, now, out var key, out var state); n++)
        {
            if (state == 0) continue;
            var (x, y, z) = ChunkKey.Unpack(key);
            // distance first: cheap
            var want = Enabled && Cull(state, ChunkKey.CentreDistance(x, z, at.X, at.Z), _near) && Deep(x, y, z);
            if (want == (state == Culled) && !Enabled) Table.Forget(key); // off: drawn in full, no longer watched
            if (want == (state == Culled) || (want && busy)) continue; // culling waits for the backlog
            marks += Table.Mark(game, x, y, z, now);
        }

        return marks;
    }

    // Chunks tessellated before their surfaces were known (a world's first) are untracked: a share per scan is redone
    private static int Sweep(ClientMain game, double px, double pz, int marks, long now)
    {
        var map = game.WorldMap;
        if (!NotNull(map) || Chunks(map) is not { } chunks || ChunksLock(map) is not { } gate) return marks;
        if (_chunkCursor >= _chunkCount)
        {
            // the chunk loader holds the lock for whole batches at a join: the main thread does not wait, the next scan asks again
            if (!Monitor.TryEnter(gate)) return marks;
            try
            {
                _chunkCount = Snapshot(chunks.Keys, ref _chunks);
            }
            finally
            {
                Monitor.Exit(gate);
            }

            _chunkCursor = 0;
        }

        for (var n = 0; n < SweptPerScan && _chunkCursor < _chunkCount && marks < MaxMarks; n++, _chunkCursor++)
        {
            var pos = map.ChunkPosFromChunkIndex3D(_chunks[_chunkCursor]);
            var key = ChunkKey.Pack(pos.X, pos.Y + pos.Dimension * Dimension, pos.Z);
            if (pos.Dimension != 0 || key < 0 || Table.Bands.ContainsKey(key) || Table.Waiting(key, now) ||
                !Deep(pos.X, pos.Y, pos.Z) || !Cull(0, ChunkKey.CentreDistance(pos.X, pos.Z, px, pz), _near)) continue;
            marks += Table.Mark(game, pos.X, pos.Y, pos.Z, now);
        }

        return marks;
    }

    // The keys copied into a buffer kept for the next snapshot: no new array (on the large object heap) every round.
    // A copy that meets the dictionary growing on another thread is skipped (0), the next scan copies again.
    private static int Snapshot<T>(Dictionary<long, T>.KeyCollection keys, ref long[] buffer)
    {
        var count = keys.Count;
        if (buffer.Length < count + 256) buffer = new long[Math.Max(count + 256, buffer.Length * 2)];
        try
        {
            keys.CopyTo(buffer, 0);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return 0;
        }

        return Math.Min(count, buffer.Length);
    }

    // The surface (lower of rain and worldgen terrain height) at a block column, NaN-free: int.MaxValue where unknown
    private static int SurfaceAt(ClientMain game, int x, int z)
    {
        // a negative coordinate (past the map's edge) would index the wrong map chunk and throw below
        if (x < 0 || z < 0 || game.WorldMap.GetMapChunk(x / Size, z / Size) is not { } map) return int.MaxValue;
        var i = (z % Size) * Size + x % Size;
        var rain = map.RainHeightMap is { Length: Size * Size } r ? r[i] : int.MaxValue;
        var terrain = map.WorldGenTerrainHeightMap is { Length: Size * Size } t ? t[i] : int.MaxValue;
        return Math.Min(rain, terrain);
    }

    // A share of the loaded columns' lowest surface, round robin over a snapshot. The network thread adds map chunks
    // without a lock: a lookup that meets MapChunks growing throws, and the next scan takes a new snapshot.
    private static void Refresh(ClientMain game)
    {
        var map = game.WorldMap;
        if (!NotNull(map) || MapChunks(map) is not { } columns) return;
        if (_columnCursor >= _columnCount) (_columnCount, _columnCursor) = (Snapshot(columns.Keys, ref _columns), 0);
        var sizeX = Math.Max(1, map.ChunkMapSizeX);
        try
        {
            for (var n = 0; n < ColumnsPerScan && _columnCursor < _columnCount; n++, _columnCursor++)
            {
                var key = _columns[_columnCursor];
                if (!columns.TryGetValue(key, out var found) || found is not IMapChunk column) continue;
                var (rain, terrain) = (column.RainHeightMap, column.WorldGenTerrainHeightMap);
                var lowest = int.MaxValue;
                for (var i = 0; i < Size * Size; i++)
                {
                    if (rain is { Length: Size * Size }) lowest = Math.Min(lowest, rain[i]);
                    if (terrain is { Length: Size * Size }) lowest = Math.Min(lowest, terrain[i]);
                }

                if (lowest < int.MaxValue) Surfaces[Column((int)(key % sizeX), (int)(key / sizeX))] = (short)Math.Min(lowest, short.MaxValue);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            _columnCursor = _columnCount;
        }
    }
}
