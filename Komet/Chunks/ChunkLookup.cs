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
// A chunk that is not loaded is answered too, on the main thread: the mirror only ever holds chunks the dictionary holds, so outside
// the engine's own changes, as many entries as the dictionary means the same ones, and a key it lacks the dictionary lacks. The
// render systems ask for many such chunks every frame (GetEntitiesAround), each a take of chunksLock the chunk threads contend for.
internal static class ChunkLookup
{
    private const int MaxChunks = 1 << 18;
    private static readonly ConcurrentDictionary<long, ClientChunk> Mirror = new();

    private static readonly string?[] Prefixes =
        [nameof(ByIndex), nameof(ByCoord), nameof(AtBlockPos), nameof(Loading), nameof(Overloading), nameof(Unloading)];

    private static readonly string?[] Postfixes =
        [null, null, null, nameof(Loaded), nameof(Overloaded), nameof(Unloaded)];

    private static Dictionary<long, ClientChunk>? _chunks;

    // Main thread: the mirror's entries, and whether one of the engine's changes to the dictionary is under way
    private static int _count;
    private static bool _changing;

    // Totals while Counting.Hud, in a Tally: every client thread that looks a chunk up counts it, too often for one shared line
    private const int HitCounter = 0, MissCounter = 1;
    private static readonly Tally Counts = new(2);

    public static bool Enabled { get; set; } = true;
    public static long Hits => Counts.Total(HitCounter);
    public static long Misses => Counts.Total(MissCounter);
    public static int Count => Assert(_count >= 0) ? _count : 0; // the chunks mirrored (the engine's own, no copies)

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
            AccessTools.Method(map, "loadChunkMT"), AccessTools.Method(map, "OverloadChunkMT"),
            AccessTools.Method(typeof(SystemUnloadChunks), "HandleChunkUnload")
        ];
        if (!NotNull(harmony) || !Assert(Array.TrueForAll(targets, target => target is not null))) return;
        if (!Assert(AccessTools.DeclaredField(map, "chunks")?.FieldType == typeof(Dictionary<long, ClientChunk>)) ||
            !Assert(AccessTools.DeclaredField(map, "chunksLock")?.FieldType == typeof(object))) return;
        for (var i = 0; i < Prefixes.Length; i++)
            if (!Binds(targets[i]!, Prefixes[i]) || !Binds(targets[i]!, Postfixes[i])) return;
        for (var i = 0; i < Prefixes.Length; i++)
            _ = NotNull(harmony.Patch(targets[i], Patch(Prefixes[i]), Patch(Postfixes[i])));
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

    // Answered from the mirror: its chunk, or none when the key is not loaded (Whole); false leaves the lookup to the engine
    private static bool Hit(ClientWorldMap map, long key, out ClientChunk? chunk)
    {
        chunk = null;
        if (!Enabled || !NotNull(map) || !ReferenceEquals(Chunks(map), _chunks) || key == long.MinValue) return false;
        var found = Mirror.TryGetValue(key, out chunk) && NotNull(chunk);
        if (!found && !Whole(map))
        {
            if (Counting.Hud) Counts.Add(MissCounter, 1);
            return false;
        }

        if (Counting.Hud) Counts.Add(HitCounter, 1);
        return true;
    }

    // Whether the mirror holds every chunk the dictionary does: on the main thread, between the engine's changes, with as many
    // entries (it never holds one the dictionary lacks: loads go in after the engine's insert, unloads out before its removal)
    private static bool Whole(ClientWorldMap map)
    {
        if (_changing || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return false;
        var chunks = Chunks(map);
        return NotNull(chunks) && Assert(_count >= 0) && _count == chunks.Count;
    }

    private static bool ByIndex(ClientWorldMap __instance, long index3d, ref IWorldChunk? __result)
    {
        if (!Hit(__instance, index3d, out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static bool ByCoord(ClientWorldMap __instance, int chunkX, int chunkY, int chunkZ, ref IWorldChunk? __result)
    {
        if (!NotNull(__instance) || !Hit(__instance, Key(__instance, chunkX, chunkY, chunkZ), out var chunk))
            return true;
        __result = chunk;
        return false;
    }

    // The engine shifts instead of dividing here, so negative coordinates land one chunk lower
    private static bool AtBlockPos(ClientWorldMap __instance, int posX, int posY, int posZ, ref ClientChunk? __result)
    {
        if (!NotNull(__instance) ||
            !Hit(__instance, Key(__instance, posX >> 5, posY >> 5, posZ >> 5), out var chunk)) return true;
        __result = chunk;
        return false;
    }

    private static void Put(long key, ClientChunk chunk)
    {
        if (!NotNull(chunk) || !Assert(key != long.MinValue)) return;
        if (Mirror.TryAdd(key, chunk)) _count++;
        else Mirror[key] = chunk;
    }

    private static void Drop(long key)
    {
        if (Mirror.TryRemove(key, out _)) _count--;
        _ = Assert(_count >= 0) && Assert(key != long.MinValue);
    }

    private static void Empty()
    {
        _ = Assert(_count >= 0) && Assert(_count <= MaxChunks);
        Mirror.Clear();
        _count = 0;
    }

    // Prefix on loadChunkMT: the engine inserts before Loaded mirrors it
    private static void Loading()
    {
        _changing = true;
        _ = Assert(_count >= 0) && Assert(_count <= MaxChunks);
    }

    // On the main thread, like the engine's own inserts and removals, so a fill never races a removal. loadChunkMT may replace a chunk:
    // past MaxChunks (more than any view distance loads) the key is dropped instead, and its lookups miss.
    private static void Loaded(ClientWorldMap __instance, Packet_ServerChunk p, ClientChunk chunk)
    {
        _changing = false;
        if (!NotNull(p) || !NotNull(chunk) || !Enabled || !NotNull(__instance)) return;
        var chunks = Chunks(__instance);
        if (!ReferenceEquals(chunks, _chunks))
        {
            Empty();
            _chunks = chunks;
        }

        var key = Key(__instance, p.X, p.Y, p.Z);
        if (!NotNull(chunks) || !Assert(key != long.MinValue)) return;
        if (Assert(chunks.Count <= MaxChunks)) Put(key, chunk);
        else Drop(key);
    }

    private static void Overloading(ClientWorldMap __instance, Packet_ServerChunk p)
    {
        _changing = true;
        if (!NotNull(__instance) || !NotNull(p)) return;
        var key = Key(__instance, p.X, p.Y, p.Z);
        if (Assert(key != long.MinValue)) Drop(key);
    }

    // An overload may re-enqueue itself instead of inserting, so afterwards the mirror takes whatever the dictionary actually holds
    private static void Overloaded(ClientWorldMap __instance, Packet_ServerChunk p, ClientChunk newchunk)
    {
        _changing = false;
        if (!NotNull(__instance) || !NotNull(p) || !NotNull(newchunk)) return;
        var (key, chunks, gate) = (Key(__instance, p.X, p.Y, p.Z), Chunks(__instance), Gate(__instance));
        if (!NotNull(chunks) || !NotNull(gate) || !Assert(key != long.MinValue)) return;
        lock (gate)
        {
            if (chunks.TryGetValue(key, out var current) && NotNull(current)) Put(key, current);
            else Drop(key);
        }
    }

    // A packet lists every chunk the server unloaded that tick, however many: one the mirror cannot read whole empties it, so lookups
    // go to the engine instead of finding a disposed chunk
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
        _ = Assert(_count >= 0) && Assert(_count <= MaxChunks);
    }
}
