using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// GetChunk, GetClientChunk, GetChunkAtBlockPos and GetNeighbouringChunks (27 a chunk built) take chunksLock on every
// call from every thread (tessellation waited on it a second in 30 s of a join); a ConcurrentDictionary mirror of
// ClientWorldMap.chunks answers without it. Written only where the engine mutates it on the main thread (loadChunkMT,
// OverloadChunkMT, HandleChunkUnload), for the dictionary it was filled from. Missing = engine lookup, never a wrong
// chunk. Main thread also answers unloaded keys (Whole): renderers ask for many per frame. The after-change patches are
// finalizers: a mod's chunk-loaded handler that throws must not leave the mirror stale or _changing set for good.
internal static class ChunkLookup
{
    private const int MaxChunks = 1 << 18, HitCounter = 0, MissCounter = 1;
    private static readonly ConcurrentDictionary<long, ClientChunk> Mirror = new();
    private static readonly Tally Counts = new(2); // Counting.Hud totals: every client thread counts

    private static readonly string?[] Prefixes =
        [nameof(ByIndex), nameof(ByCoord), nameof(AtBlockPos), nameof(ClientByIndex), nameof(ClientByCoord), nameof(Neighbours),
            nameof(Loading), nameof(Overloading), nameof(Unloading)];

    private static readonly string?[] Finalizers =
        [null, null, null, null, null, null, nameof(Loaded), nameof(Overloaded), nameof(Unloaded)];

    private static Dictionary<long, ClientChunk>? _chunks;

    // Main thread: mirror entry count, and whether an engine change to the dictionary is under way
    private static int _count;
    private static bool _changing;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static long Hits => Counts.Total(HitCounter);
    public static long Misses => Counts.Total(MissCounter);
    public static int Count => _count; // the chunks mirrored (the engine's own, no copies)

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunks")]
    internal static extern ref Dictionary<long, ClientChunk> Chunks(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "EmptyChunk")]
    private static extern ref ClientChunk? EmptyChunk(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunksLock")]
    private static extern ref object Gate(ClientWorldMap map);

    // All or nothing: a mirror missing an unload or overload would hand out chunks the engine no longer has
    public static void Install(Harmony harmony)
    {
        Clear();
        Installed = false;
        var map = typeof(ClientWorldMap);
        MethodInfo?[] targets =
        [
            AccessTools.Method(map, "GetChunk", [typeof(long)]),
            AccessTools.Method(map, "GetChunk", [typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(map, "GetChunkAtBlockPos", [typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(map, "GetClientChunk", [typeof(long)]),
            AccessTools.Method(map, "GetClientChunk", [typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(map, "GetNeighbouringChunks", [typeof(ClientChunk[]), typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(map, "loadChunkMT"), AccessTools.Method(map, "OverloadChunkMT"),
            AccessTools.Method(typeof(SystemUnloadChunks), "HandleChunkUnload")
        ];
        if (!NotNull(harmony) || !Assert(Array.TrueForAll(targets, target => target is not null))) return;
        if (!Assert(AccessTools.DeclaredField(map, "chunks")?.FieldType == typeof(Dictionary<long, ClientChunk>)) ||
            !Assert(AccessTools.DeclaredField(map, "chunksLock")?.FieldType == typeof(object))) return;
        for (var i = 0; i < Prefixes.Length; i++)
            if (!Binds(targets[i]!, Prefixes[i]) || !Binds(targets[i]!, Finalizers[i])) return;
        for (var i = 0; i < Prefixes.Length; i++)
            _ = harmony.Patch(targets[i], Patch(Prefixes[i]), finalizer: Patch(Finalizers[i]));
        Installed = true;
    }

    private static HarmonyMethod? Patch(string? name) => name is null ? null : new(typeof(ChunkLookup), name);

    private static bool Binds(MethodInfo target, string? name) =>
        name is null || Assert(Il.Binds(target, AccessTools.Method(typeof(ChunkLookup), name)));

    public static void Clear()
    {
        Mirror.Clear();
        (_chunks, _count, _changing) = (null, 0, false);
    }

    private static long Key(ClientWorldMap map, int chunkX, int chunkY, int chunkZ) =>
        Assert(map.index3dMulX > 0) && Assert(map.index3dMulZ > 0)
            ? MapUtil.Index3dL(chunkX, chunkY, chunkZ, map.index3dMulX, map.index3dMulZ)
            : long.MinValue;

    // From the mirror: its chunk, or none when the key is not loaded (Whole); false leaves the lookup to the engine
    private static bool Hit(ClientWorldMap map, long key, out ClientChunk? chunk)
    {
        chunk = null;
        if (!Enabled || !ReferenceEquals(Chunks(map), _chunks) || key == long.MinValue) return false;
        var found = Mirror.TryGetValue(key, out chunk);
        if (!found && !Whole(map))
        {
            if (Counting.Hud) Counts.Add(MissCounter, 1);
            return false;
        }

        if (Counting.Hud) Counts.Add(HitCounter, 1);
        return true;
    }

    // Main thread between engine changes: equal counts mean equal entries (loads after insert, unloads before)
    private static bool Whole(ClientWorldMap map)
    {
        if (_changing || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return false;
        var chunks = Chunks(map);
        return NotNull(chunks) && _count == chunks.Count;
    }

    private static bool ByIndex(ClientWorldMap __instance, long index3d, ref IWorldChunk? __result)
    {
        if (!Hit(__instance, index3d, out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static bool ByCoord(ClientWorldMap __instance, int chunkX, int chunkY, int chunkZ, ref IWorldChunk? __result)
    {
        if (!Hit(__instance, Key(__instance, chunkX, chunkY, chunkZ), out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static bool ClientByIndex(ClientWorldMap __instance, long index3d, ref ClientChunk? __result)
    {
        if (!Hit(__instance, index3d, out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static bool ClientByCoord(ClientWorldMap __instance, int chunkX, int chunkY, int chunkZ, ref ClientChunk? __result)
    {
        if (!Hit(__instance, Key(__instance, chunkX, chunkY, chunkZ), out var chunk)) return true;
        __result = chunk;
        return false;
    }

    // The 27 chunks around one, as the engine fills them (an empty one as its EmptyChunk), all from the mirror; one not in
    // it (not loaded, or being loaded right now) leaves the whole lookup to the engine under chunksLock
    private static bool Neighbours(ClientWorldMap __instance, ClientChunk[] neibchunks, int chunkX, int chunkY, int chunkZ)
    {
        if (!Enabled || !ReferenceEquals(Chunks(__instance), _chunks) || neibchunks is not { Length: >= 27 } ||
            EmptyChunk(__instance) is not { } empty) return true;
        var n = 0;
        for (var i = -1; i <= 1; i++)
            for (var j = -1; j <= 1; j++)
                for (var k = -1; k <= 1; k++)
                {
                    if (!Mirror.TryGetValue(Key(__instance, chunkX + i, chunkY + j, chunkZ + k), out var chunk)) return true;
                    if (chunk.Empty) chunk = empty;
                    if (!chunk.ChunkHasData()) return true; // the engine throws as it would
                    neibchunks[n++] = chunk;
                }

        return false;
    }

    // The engine shifts instead of dividing, so negative coordinates land one chunk lower
    private static bool AtBlockPos(ClientWorldMap __instance, int posX, int posY, int posZ, ref ClientChunk? __result)
    {
        if (!Hit(__instance, Key(__instance, posX >> 5, posY >> 5, posZ >> 5), out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static void Put(long key, ClientChunk chunk)
    {
        if (Mirror.TryAdd(key, chunk)) _count++;
        else Mirror[key] = chunk;
    }

    private static void Drop(long key)
    {
        if (Mirror.TryRemove(key, out _)) _count--;
    }

    private static void Empty()
    {
        Mirror.Clear();
        _count = 0;
    }

    // Prefix on loadChunkMT: the engine inserts first thing, then Loaded mirrors it (also when the rest threw)
    private static void Loading()
    {
        _changing = true;
    }

    // Past MaxChunks (beyond any view distance) the key is dropped instead and its lookups miss
    private static void Loaded(ClientWorldMap __instance, Packet_ServerChunk p, ClientChunk chunk)
    {
        _changing = false;
        if (!NotNull(p) || !NotNull(chunk)) return;
        var chunks = Chunks(__instance);
        if (!ReferenceEquals(chunks, _chunks))
        {
            Empty();
            _chunks = chunks;
        }

        var key = Key(__instance, p.X, p.Y, p.Z);
        if (!NotNull(chunks) || !Assert(key != long.MinValue)) return;
        // dropped even when switched off: loadChunkMT may have replaced the mirrored chunk
        if (Enabled && Assert(chunks.Count <= MaxChunks)) Put(key, chunk);
        else Drop(key);
    }

    private static void Overloading(ClientWorldMap __instance, Packet_ServerChunk p)
    {
        _changing = true;
        if (!NotNull(p)) return;
        var key = Key(__instance, p.X, p.Y, p.Z);
        if (Assert(key != long.MinValue)) Drop(key);
    }

    // An overload may re-enqueue itself instead of inserting, so the mirror then takes what the dictionary holds
    private static void Overloaded(ClientWorldMap __instance, Packet_ServerChunk p)
    {
        _changing = false;
        if (!NotNull(p)) return;
        var (key, chunks, gate) = (Key(__instance, p.X, p.Y, p.Z), Chunks(__instance), Gate(__instance));
        if (!NotNull(chunks) || !NotNull(gate) || !Assert(key != long.MinValue)) return;
        lock (gate)
        {
            if (chunks.TryGetValue(key, out var current) && NotNull(current)) Put(key, current);
            else Drop(key);
        }
    }

    // An unload packet not readable whole empties the mirror: lookups go to the engine, never to a disposed chunk
    private static void Unloading(ClientMain ___game, Packet_Server packet)
    {
        _changing = true;
        var (chunks, map) = (packet?.UnloadChunk, ___game?.WorldMap);
        if (!NotNull(chunks) || !NotNull(map) || chunks.GetXCount() == 0) return;
        var count = chunks.GetXCount();
        if (chunks.X is not { } xs || chunks.Y is not { } ys || chunks.Z is not { } zs ||
            !Assert(count is > 0 and <= MaxChunks) ||
            !Assert(xs.Length >= count && ys.Length >= count && zs.Length >= count))
        {
            Empty();
            return;
        }

        for (var i = 0; i < Math.Min(count, MaxChunks); i++) Drop(Key(map, xs[i], ys[i], zs[i]));
    }

    private static void Unloaded()
    {
        _changing = false;
    }
}
