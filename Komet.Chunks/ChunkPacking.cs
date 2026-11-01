using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Server;

namespace Komet.Chunks;

// Client chunks stay unpacked until SystemCompressChunks packs one per 20 ms (max 50/s) after 8 s untouched
// (optimizeRamMode 1; 4 s in 2): flying leaves thousands unpacked (100-250 KB vs a few KB). Levels pack PerTick a tick
// (1: 4 = 200/s; 2: 8, half the time; 3: 16, a quarter, data pool <= 500 spares vs the engine's 1500-5000, its caps back
// below 3) with the engine's own Pack and TryCommitPackAndFree; chunks within two of the player stay unpacked. Reads
// unpack again: more CPU flying, less RAM.
// The singleplayer server packs its chunk copies the same way (ServerSystemCompressChunks, MagicNum.UncompressedChunkTTL
// 15 s), but its scan spares |dx| < 2 OR |dz| < 2 of a player, a cross through the whole loaded area (meant AND): 28000
// server vs 17000 client chunk datas at view distance 1536. FindFreeableMemory is replaced with AND and the level's time.
// A server chunk farther than FarColumns from every player is only read again by worldgen next to it (it was sent, and
// the client holds its own): it is packed after FarTtlMs untouched (heap dump 2026-10-30, flying: 8200 unpacked server
// chunk datas, ~330 MB of bit planes, against 134 MB for all 92000 packed chunks of client and server together).
internal static class ChunkPacking
{
    private const int MaxLevel = 3, MaxTicks = 64, MinTtlMs = 1000, MaxClients = 1024, MaxScanned = 1 << 22;
    private const int FarColumns = 4, FarTtlMs = 5000;
    private const int CommitMs = 4000; // the client commits its packs every 4 s (SystemCompressChunks, PackCommit)
    private const int SpareCache = 500, SpareSlowDispose = 350; // level 3's caps on the data pool
    private const string Compress = "Vintagestory.Client.NoObf.SystemCompressChunks"; // internal
    private const string ServerCompress = "Vintagestory.Server.ServerSystemCompressChunks"; // internal
    private static readonly int[] PerTick = [1, 4, 8, 16];
    private static readonly int[] Divisor = [1, 1, 2, 4];

    [ThreadStatic] private static bool _inside, _serverInside;
    private static Action<object, float>? _tick;
    private static Action<object>? _serverTick;
    private static AccessTools.FieldRef<object, int[]>? _ttls;
    private static AccessTools.FieldRef<object, Queue<long>>? _queue;
    private static AccessTools.FieldRef<object, object>? _queueLock;
    private static int[]? _engineTtls;
    private static int _appliedLevel = -1;
    private static (int Cache, int SlowDispose)? _engineSpares; // the data pool's caps before level 3 lowered them

    // Server main thread: Environment.TickCount of the last scan (Scanning), once there was one in this world
    private static int _serverScanAt;
    private static bool _serverScanned;

    public static int Level { get; set; } = 1;
    public static int Max => MaxLevel;
    public static bool Installed { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkDataPool")]
    private static extern ref ClientChunkDataPool? DataPool(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "server")]
    private static extern ref ServerMain? ServerOf(ServerSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "loadedChunks")]
    private static extern ref Dictionary<long, ServerChunk>? Loaded(ServerMain server);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "loadedChunksLock")]
    private static extern ref FastRWLock LoadedLock(ServerMain server);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var type = AccessTools.TypeByName(Compress);
        var tick = type is null ? null : AccessTools.DeclaredMethod(type, "OnSeperateThreadGameTick", [typeof(float)]);
        if (!NotNull(harmony) || type is null || tick is null || AccessTools.DeclaredField(type, "ttlsByRamMode") is null) return;
        // the patched method itself (its postfix sees _inside), compiled once: no allocation per call
        var (system, dt) = (Expression.Parameter(typeof(object)), Expression.Parameter(typeof(float)));
        _tick = Expression.Lambda<Action<object, float>>(
            Expression.Call(Expression.Convert(system, type), tick, dt), system, dt).Compile();
        _ttls = AccessTools.FieldRefAccess<object, int[]>(AccessTools.DeclaredField(type, "ttlsByRamMode"));
        _ = harmony.Patch(tick, postfix: new HarmonyMethod(typeof(ChunkPacking), nameof(Ticked)));
        (_engineTtls, _appliedLevel, Installed) = (null, -1, true);
        InstallServer(harmony); // the in-process server's chunks too (singleplayer)
        PackCommit.Install(harmony); // the 4 s commit and scan without chunksLock
        // the commit rechecks the time (8 s client, 15 s server): same divisor, or early packs are discarded
        var commit = AccessTools.DeclaredMethod(typeof(WorldChunk), "TryCommitPackAndFree", [typeof(int)]);
        if (commit is not null) _ = harmony.Patch(commit, new HarmonyMethod(typeof(ChunkPacking), nameof(Committing)));
    }

    public static void Stop() =>
        (_tick, _ttls, _engineTtls, _appliedLevel, _engineSpares, _serverScanned, Installed) =
        (null, null, null, -1, null, false, false);

    // Level 3: cap the data pool's spare chunk datas, its own caps back below 3; on the main thread
    public static void Spares(ClientMain game)
    {
        if (game.WorldMap is not { } map || DataPool(map) is not { } pool || !Assert(pool.CacheSize >= 0)) return;
        if (Installed && Level >= MaxLevel)
        {
            // the engine's caps are 1000 or more: anything above the clamp is a fresh value from its RAM-mode setting
            if (_engineSpares is null || pool.CacheSize > SpareCache || pool.SlowDisposeThreshold > SpareSlowDispose)
                _engineSpares = (pool.CacheSize, pool.SlowDisposeThreshold);
            (pool.CacheSize, pool.SlowDisposeThreshold) =
                (Math.Min(pool.CacheSize, SpareCache), Math.Min(pool.SlowDisposeThreshold, SpareSlowDispose));
        }
        else if (_engineSpares is { } engine)
        {
            (pool.CacheSize, pool.SlowDisposeThreshold, _engineSpares) = (engine.Cache, engine.SlowDispose, null);
        }
    }

    // Separate-thread tick: the engine packed one chunk, this packs PerTick - 1 more the same way
    private static void Ticked(object __instance, float dt)
    {
        if (_inside || !Installed) return;
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (level != _appliedLevel) Apply(__instance, level);
        if (_tick is not { } tick || level == 0) return;
        _inside = true;
        try
        {
            for (var i = 1; i < Math.Min(PerTick[level], MaxTicks); i++) tick(__instance, dt);
        }
        catch (Exception)
        {
            Level = 0; // the engine's own tick threw: it packs at its own pace from now on
            throw;
        }
        finally
        {
            _inside = false;
        }
    }

    // The commit's time is also the engine's only guard against a pack older than a write after it (Pack keeps the
    // planes, a write leaves the packed copy as it was). A client pack waits up to the 4 s commit interval: the divided
    // time stays at least that plus the shortest scan time, so an untouched chunk still commits and one touched since its
    // pack does not (level 3's 2 s put back blocks changed after the pack). A server pack waits for the whole queue: a
    // chunk read or written since the scan that queued it stays unpacked, else a far one commits at the far time. Only
    // FreeMemory commits at UncompressedChunkTTL; TryPackAndCommit (8 s, chunk supply) packs right before its commit.
    private static void Committing(WorldChunk __instance, ref int chunkTTL)
    {
        var level = Installed ? Math.Clamp(Level, 0, MaxLevel) : 0;
        var divided = Divisor[level] > 1 ? Math.Max(MinTtlMs, chunkTTL / Divisor[level]) : chunkTTL;
        if (level == 0 || __instance is not ServerChunk)
            chunkTTL = Math.Min(chunkTTL, Math.Max(CommitMs + MinTtlMs, divided)); // never above the caller's
        else if (_serverScanned && chunkTTL == MagicNum.UncompressedChunkTTL && __instance.lastReadOrWrite - _serverScanAt >= 0)
            chunkTTL = int.MaxValue;
        else chunkTTL = Math.Min(divided, FarTtlMs);
    }

    // Times untouched: the engine's divided by the level's divisor
    private static void Apply(object system, int level)
    {
        if (_ttls?.Invoke(system) is not { Length: 3 } ttls) return;
        _engineTtls ??= [.. ttls];
        for (var i = 0; i < Math.Min(ttls.Length, 3); i++) ttls[i] = Math.Max(MinTtlMs, _engineTtls[i] / Divisor[level]);
        _appliedLevel = level;
    }

    // The server scan Scanning skips (prefix returning false)
    internal static System.Reflection.MethodBase?[] Replaced =>
        AccessTools.TypeByName(ServerCompress) is { } type ? [AccessTools.DeclaredMethod(type, "FindFreeableMemory")] : [];

    private static void InstallServer(Harmony harmony)
    {
        _serverScanned = false;
        var type = AccessTools.TypeByName(ServerCompress);
        var scan = type is null ? null : AccessTools.DeclaredMethod(type, "FindFreeableMemory");
        var tick = type is null ? null : AccessTools.DeclaredMethod(type, "OnSeparateThreadTick", []);
        var (queue, gate) = type is null ? (null, null) :
            (AccessTools.DeclaredField(type, "compactableChunks"), AccessTools.DeclaredField(type, "compactableChunksLock"));
        if (!NotNull(harmony) || type is null || scan is null || tick is null || queue is null || gate is null ||
            !typeof(ServerSystem).IsAssignableFrom(type)) return;
        (_queue, _queueLock) = (AccessTools.FieldRefAccess<object, Queue<long>>(queue), AccessTools.FieldRefAccess<object, object>(gate));
        var system = Expression.Parameter(typeof(object));
        _serverTick = Expression.Lambda<Action<object>>(
            Expression.Call(Expression.Convert(system, type), tick), system).Compile();
        _ = harmony.Patch(scan, new HarmonyMethod(typeof(ChunkPacking), nameof(Scanning)));
        _ = harmony.Patch(tick, postfix: new HarmonyMethod(typeof(ChunkPacking), nameof(ServerTicked)));
    }

    // FindFreeableMemory: chunks untouched for the level's time (far ones for FarTtlMs), except the 3x3 columns around a player
    private static bool Scanning(object __instance)
    {
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (level == 0 || __instance is not ServerSystem own || ServerOf(own) is not { } server || Loaded(server) is not { } loaded ||
            _queue?.Invoke(__instance) is not { } queue || _queueLock?.Invoke(__instance) is not { } gate) return true;
        var near = new List<(int X, int Z)>();
        // ChunkPos's own column math, without its throw for a playing client whose entity is not there yet
        using (var clients = server.Clients.Values.GetEnumerator())
            for (var i = 0; i < MaxClients && clients.MoveNext(); i++)
                if (clients.Current is { State: EnumClientState.Playing, Position: { } at })
                    near.Add(((int)at.X / 32, (int)at.Z / 32));
        var ttl = Math.Max(MinTtlMs, MagicNum.UncompressedChunkTTL / Divisor[level]);
        var now = Environment.TickCount;
        (_serverScanAt, _serverScanned) = (now, true); // before the queue: Committing judges each pack by it
        lock (gate)
        {
            ref var chunksLock = ref LoadedLock(server);
            chunksLock.AcquireReadLock();
            try
            {
                using var chunks = loaded.GetEnumerator(); // no copy of 50000 entries every scan
                for (var i = 0; i < MaxScanned && chunks.MoveNext(); i++)
                {
                    var (key, chunk) = chunks.Current;
                    if (chunk is null || chunk.IsPacked() || now - chunk.lastReadOrWrite <= Math.Min(ttl, FarTtlMs)) continue;
                    var pos = server.WorldMap.ChunkPosFromChunkIndex3D(key);
                    if (!chunk.Empty && pos.Dimension == 0 && Near(near, pos.X, pos.Z, 2)) continue;
                    if (now - chunk.lastReadOrWrite <= ttl && (pos.Dimension != 0 || Near(near, pos.X, pos.Z, FarColumns))) continue;
                    queue.Enqueue(key);
                }
            }
            finally
            {
                chunksLock.ReleaseReadLock();
            }
        }

        return false; // queued: the engine's own scan would queue again
    }

    // Within `columns` of a player's chunk column (no closure: the scan sees every loaded chunk)
    private static bool Near(List<(int X, int Z)> near, int x, int z, int columns)
    {
        for (var j = 0; j < Math.Min(near.Count, MaxClients); j++)
            if (Math.Abs(near[j].X - x) < columns && Math.Abs(near[j].Z - z) < columns) return true;
        return false;
    }

    private static void ServerTicked(object __instance)
    {
        var level = Math.Clamp(Level, 0, MaxLevel);
        if (_serverInside || level == 0 || _serverTick is not { } tick) return;
        _serverInside = true;
        try
        {
            for (var i = 1; i < Math.Min(PerTick[level], MaxTicks); i++) tick(__instance);
        }
        finally
        {
            _serverInside = false;
        }
    }
}
