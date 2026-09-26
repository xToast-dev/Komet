using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// ClientWorldMap.GetChunk and GetChunkAtBlockPos take chunksLock on every call, from the block accessors of every client thread. A
// ConcurrentDictionary mirror of ClientWorldMap.chunks answers them without it. The mirror is written only where the engine mutates the
// dictionary, on the main thread (loadChunkMT, OverloadChunkMT, HandleChunkUnload), and only for the dictionary it was filled from:
// OnMapSizeReceived replaces the dictionary, and lookups on another one go to the engine. A chunk the mirror lacks is a miss, answered
// by the engine, never a wrong chunk.
internal static class ChunkLookup
{
    private const int MaxChunks = 1 << 18;
    private static readonly ConcurrentDictionary<long, ClientChunk> Mirror = new();

    private static readonly string?[] Prefixes =
        [nameof(ByIndex), nameof(ByCoord), nameof(AtBlockPos), null, nameof(Overloading), nameof(Unloading)];

    private static readonly string?[] Postfixes = [null, null, null, nameof(Loaded), nameof(Overloaded), null];
    private static Dictionary<long, ClientChunk>? _chunks; // the dictionary the mirror stands in for

    // Totals while Counting.Hud; Interlocked: every client thread that looks a chunk up counts it
    private static long _hits, _misses;

    public static bool Enabled { get; set; } = true;
    public static long Hits => Interlocked.Read(ref _hits);
    public static long Misses => Interlocked.Read(ref _misses);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunks")]
    private static extern ref Dictionary<long, ClientChunk> Chunks(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunksLock")]
    private static extern ref object Gate(ClientWorldMap map);

    // All or nothing: a mirror that misses an unload or an overload would hand out chunks the engine no longer has
    public static void Install(Harmony harmony)
    {
        Clear();
        var map = typeof(ClientWorldMap);
        MethodInfo?[] targets =
        [
            AccessTools.Method(map, "GetChunk", [typeof(long)]),
            AccessTools.Method(map, "GetChunk", [typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(map, "GetChunkAtBlockPos", [typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(map, "loadChunkMT"),
            AccessTools.Method(map, "OverloadChunkMT"),
            AccessTools.Method(typeof(SystemUnloadChunks), "HandleChunkUnload")
        ];
        if (!NotNull(harmony) || !Assert(Array.TrueForAll(targets, target => target is not null))) return;
        if (!Assert(AccessTools.DeclaredField(map, "chunks")?.FieldType == typeof(Dictionary<long, ClientChunk>)) ||
            !Assert(AccessTools.DeclaredField(map, "chunksLock")?.FieldType == typeof(object))) return;
        for (var i = 0; i < Math.Min(targets.Length, Prefixes.Length); i++)
            if (!Binds(targets[i]!, Prefixes[i]) || !Binds(targets[i]!, Postfixes[i]))
                return;
        for (var i = 0; i < Math.Min(targets.Length, Prefixes.Length); i++)
            _ = NotNull(harmony.Patch(targets[i], Patch(Prefixes[i]), Patch(Postfixes[i])));
    }

    private static HarmonyMethod? Patch(string? name)
    {
        return name is null ? null : new HarmonyMethod(typeof(ChunkLookup), name);
    }

    private static bool Binds(MethodInfo target, string? name)
    {
        return name is null || Assert(Il.Binds(target, AccessTools.Method(typeof(ChunkLookup), name)));
    }

    public static void Clear()
    {
        Mirror.Clear();
        _chunks = null;
    }

    private static long Key(ClientWorldMap map, int chunkX, int chunkY, int chunkZ)
    {
        return Assert(map.index3dMulX > 0) && Assert(map.index3dMulZ > 0)
            ? MapUtil.Index3dL(chunkX, chunkY, chunkZ, map.index3dMulX, map.index3dMulZ)
            : long.MinValue;
    }

    private static bool Hit(ClientWorldMap map, long key, out ClientChunk chunk)
    {
        chunk = null!;
        if (!Enabled || !NotNull(map) || !ReferenceEquals(Chunks(map), _chunks) || key == long.MinValue) return false;
        if (!Mirror.TryGetValue(key, out var found) || !NotNull(found))
        {
            if (Counting.Hud) _ = Interlocked.Increment(ref _misses);
            return false;
        }

        chunk = found;
        if (Counting.Hud) _ = Interlocked.Increment(ref _hits);
        return true;
    }

    // On the main thread, like the engine's own inserts and removals, so a fill never races a removal. loadChunkMT may replace a chunk:
    // past MaxChunks (more than any view distance loads) the key is dropped instead, and its lookups miss.
    private static void Store(ClientWorldMap map, Packet_ServerChunk p, ClientChunk chunk)
    {
        if (!Enabled || !NotNull(map) || !NotNull(p) || !NotNull(chunk)) return;
        var chunks = Chunks(map);
        if (!ReferenceEquals(chunks, _chunks))
        {
            Mirror.Clear();
            _chunks = chunks;
        }

        var key = Key(map, p.X, p.Y, p.Z);
        if (!NotNull(chunks) || !Assert(key != long.MinValue)) return;
        if (Assert(chunks.Count <= MaxChunks)) Mirror[key] = chunk;
        else _ = Mirror.TryRemove(key, out _);
    }

    // An overload may re-enqueue itself instead of inserting, so afterwards the mirror takes whatever the dictionary actually holds
    private static void Sync(ClientWorldMap map, long key)
    {
        var chunks = Chunks(map);
        var gate = Gate(map);
        if (!NotNull(chunks) || !NotNull(gate) || !Assert(key != long.MinValue)) return;
        lock (gate)
        {
            if (chunks.TryGetValue(key, out var current) && NotNull(current)) Mirror[key] = current;
            else _ = Mirror.TryRemove(key, out _);
        }
    }

    // Harmony injects the instance, the arguments and the result by name
    // ReSharper disable once InconsistentNaming
    private static bool ByIndex(ClientWorldMap __instance, long index3d, ref IWorldChunk __result)
    {
        if (!Hit(__instance, index3d, out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static bool ByCoord(ClientWorldMap __instance, int chunkX, int chunkY, int chunkZ, ref IWorldChunk __result)
    {
        if (!NotNull(__instance) || !Hit(__instance, Key(__instance, chunkX, chunkY, chunkZ), out var chunk))
            return true;
        __result = chunk;
        return false;
    }

    // The engine shifts instead of dividing here, so negative coordinates land one chunk lower
    private static bool AtBlockPos(ClientWorldMap __instance, int posX, int posY, int posZ, ref ClientChunk __result)
    {
        if (!NotNull(__instance) ||
            !Hit(__instance, Key(__instance, posX >> 5, posY >> 5, posZ >> 5), out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static void Loaded(ClientWorldMap __instance, Packet_ServerChunk p, ClientChunk chunk)
    {
        if (NotNull(p) && NotNull(chunk)) Store(__instance, p, chunk);
    }

    private static void Overloading(ClientWorldMap __instance, Packet_ServerChunk p)
    {
        if (!NotNull(__instance) || !NotNull(p)) return;
        var key = Key(__instance, p.X, p.Y, p.Z);
        if (Assert(key != long.MinValue)) _ = Mirror.TryRemove(key, out _);
    }

    private static void Overloaded(ClientWorldMap __instance, Packet_ServerChunk p, ClientChunk newchunk)
    {
        if (NotNull(__instance) && NotNull(p) && NotNull(newchunk)) Sync(__instance, Key(__instance, p.X, p.Y, p.Z));
    }

    // A packet lists every chunk the server unloaded that tick, however many: one the mirror cannot read whole empties it, so lookups
    // go to the engine instead of finding a disposed chunk
    private static void Unloading(ClientMain ___game, Packet_Server packet)
    {
        var (chunks, map) = (packet?.UnloadChunk, ___game?.WorldMap);
        if (!NotNull(chunks) || !NotNull(map) || chunks.GetXCount() == 0) return;
        var count = chunks.GetXCount();
        if (chunks.X is not { } xs || chunks.Y is not { } ys || chunks.Z is not { } zs ||
            !Assert(count is > 0 and <= MaxChunks) ||
            !Assert(xs.Length >= count && ys.Length >= count && zs.Length >= count))
        {
            Mirror.Clear();
            return;
        }

        for (var i = 0; i < Math.Min(count, MaxChunks); i++) _ = Mirror.TryRemove(Key(map, xs[i], ys[i], zs[i]), out _);
    }
}
