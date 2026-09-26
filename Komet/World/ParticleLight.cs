using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Common;

namespace Komet.World;

// Particles on the render thread read their light every third physics step: ParticleGeneric.TickNow calls
// BlockAccessorReadLockfree.GetLightRGBsAsInt, which, lock-free in name only, ends in WorldChunk.Unpack_AndReadLight and takes the
// chunk's packUnpackLock. Three client threads hold that lock for a millisecond or more at a time: the visibility calculation
// (ClientChunk.TemporaryUnpack), the chunk compressor (WorldChunk.Pack, zstd under the lock) and the tesselator reading neighbours.
// With ~150 bee particles alive the render thread slept in Monitor.Enter for 8-13 ms of a frame.
//
// A particle's light is shading and nothing else. So on the render thread the read only takes the lock when it is free: when another
// thread holds it, or the chunk is packed (reading would decompress it on the render thread and keep it unpacked), the particle gets
// the last light read in that chunk instead (a neighbour's, for a particle of the same swarm), or else the last read anywhere, for the
// three physics steps until its next read. Monitor.TryEnter is no cheap test either (it spins in TryEnter_Slowpath), so a chunk found
// busy is not tried again for BackoffMs. Every other reader, and every read the lock is free for, runs the engine's method unchanged:
// the prefix holds the lock it took across the original, which enters it again without waiting. Only the render thread reaches the
// state below.
internal static class ParticleLight
{
    public const int BackoffMs = 20;
    private const int Remembered = 8;

    private static readonly Slot[] Slots = new Slot[Remembered]; // the chunks read or found busy lately, round robin
    private static int _next, _last;
    private static bool _hasLast;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static long Reads { get; private set; } // render-thread reads the prefix saw, totals while Counting.Hud
    public static long Busy { get; private set; } // the lock was taken: served the last light instead of waiting

    // The chunk was packed: served the last light instead of unpacking it
    public static long Packed { get; private set; }

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var target = AccessTools.Method(typeof(BlockAccessorReadLockfree),
            nameof(BlockAccessorReadLockfree.GetLightRGBsAsInt), [typeof(int), typeof(int), typeof(int)]);
        if (!NotNull(harmony) || !NotNull(target)) return;
        _ = NotNull(harmony.Patch(target, new HarmonyMethod(Prefix), finalizer: new HarmonyMethod(Finalizer)));
        Installed = true;
    }

    private static bool Prefix(BlockAccessorReadLockfree __instance, int posX, int posY, int posZ, ref int __result,
        out WorldChunk? __state)
    {
        __state = null;
        if (!Enabled || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return true;
        var chunk = Chunk(__instance, posX, posY, posZ);
        // no chunk: the engine's own fallback, no lock involved
        return chunk is null || Enter(chunk, ref __result, out __state);
    }

    // True: the engine reads, and state is the chunk when this took its lock. False: result is a light read before.
    internal static bool Enter(WorldChunk chunk, ref int result, out WorldChunk? state)
    {
        state = null;
        var gate = NotNull(chunk) ? PackUnpackLock(chunk) : null;
        if (!NotNull(gate)) return true;
        if (Counting.Hud) Reads++;
        var slot = Find(chunk);
        if (slot >= 0 && Stopwatch.GetTimestamp() < Slots[slot].BusyUntil)
        {
            if (Counting.Hud) Busy++;
            return Served(slot, ref result);
        }

        if (!Monitor.TryEnter(gate))
        {
            if (Counting.Hud) Busy++;
            slot = Claim(chunk, slot);
            Slots[slot].BusyUntil = Stopwatch.GetTimestamp() + BackoffMs * Stopwatch.Frequency / 1000;
            return Served(slot, ref result);
        }

        if (chunk.IsPacked())
        {
            Monitor.Exit(gate);
            if (Counting.Hud) Packed++;
            return Served(slot, ref result);
        }

        // its lock is held across the original, which takes it again without waiting; the finalizer lets it go
        state = chunk;
        return true;
    }

    // Runs after the original, also when it threw: the lock taken in the prefix is released exactly once
    internal static Exception? Finalizer(Exception? __exception, WorldChunk? __state, int __result)
    {
        if (__state is null) return __exception;
        if (__exception is null)
        {
            (_last, _hasLast) = (__result, true);
            var slot = Claim(__state, Find(__state));
            (Slots[slot].Light, Slots[slot].Known, Slots[slot].BusyUntil) = (__result, true, 0);
        }

        Monitor.Exit(PackUnpackLock(__state));
        return __exception;
    }

    // Leaving the world: no chunk of it stays reachable from here
    internal static void Forget()
    {
        _ = Assert(_hasLast || _last == 0) && Assert(_next is >= 0 and < Remembered);
        Array.Clear(Slots);
        (_next, _last, _hasLast) = (0, 0, false);
    }

    // The chunk's last light, else the last one read anywhere; before the first read there is nothing to serve, so that one waits
    // like the engine's
    private static bool Served(int slot, ref int result)
    {
        if (slot >= 0 && Slots[slot].Known)
        {
            result = Slots[slot].Light;
            return false;
        }

        if (!_hasLast)
        {
            _ = Assert(_last == 0);
            return true;
        }

        result = _last;
        return false;
    }

    private static int Find(WorldChunk chunk)
    {
        for (var i = 0; i < Remembered; i++)
            if (ReferenceEquals(Slots[i].Owner, chunk))
                return i;
        return -1;
    }

    // The chunk's slot, else the next one round robin, emptied for it: a swarm stays within a few chunks
    private static int Claim(WorldChunk chunk, int slot)
    {
        if (slot >= 0) return slot;
        var i = _next;
        _next = (_next + 1) % Remembered;
        Slots[i] = new Slot { Owner = chunk };
        return Assert(Index(i, Remembered)) ? i : 0;
    }

    private static WorldChunk? Chunk(BlockAccessorReadLockfree accessor, int x, int y, int z)
    {
        var map = WorldMapOf(accessor);
        if (!NotNull(map) || (x | y | z) < 0 || x >= map.MapSizeX || y >= map.MapSizeY || z >= map.MapSizeZ)
            return null;
        const int size = GlobalConstants.ChunkSize;
        return map.GetChunkNonLocking(x / size, y / size, z / size) as WorldChunk;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "worldmap")]
    private static extern ref WorldMap WorldMapOf(BlockAccessorReadLockfree accessor);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packUnpackLock")]
    private static extern ref object PackUnpackLock(WorldChunk chunk);

    private struct Slot
    {
        public WorldChunk? Owner;
        public long BusyUntil;
        public int Light;
        public bool Known;
    }
}
