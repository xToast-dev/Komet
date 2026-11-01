using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Common;

namespace Komet.World;

// TickNow reads particle light under the chunk's packUnpackLock, which the packer and tesselator hold 1 ms+: ~150 bees
// cost 8-13 ms a frame. On the render thread a busy or packed chunk serves its last light, else the last anywhere,
// BackoffMs as TryEnter spins. A block's light read under the lock answers its block for HoldMs: every particle reads
// its block every third tick and a snowfall's flakes share their blocks, while each TryEnter of a lock a tesselator
// holds spins until it is free (trace 2026-10-31, user world in snow: 4.6 % of the render thread in TryEnter). A
// block's light changes only when its chunk is relit, which a particle shows HoldMs later at most.
internal static class ParticleLight
{
    public const int BackoffMs = 20, HoldMs = 500;
    private const int Remembered = 32, Cells = 4096;

    private static readonly Slot[] Slots = new Slot[Remembered];
    private static readonly Cell[] Lights = new Cell[Cells];
    private static int _next, _last;
    private static bool _hasLast;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static long Reads { get; private set; }
    public static long Busy { get; private set; }
    public static long Packed { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "worldmap")]
    private static extern ref WorldMap WorldMapOf(BlockAccessorReadLockfree accessor);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        var target = AccessTools.Method(typeof(BlockAccessorReadLockfree),
            nameof(BlockAccessorReadLockfree.GetLightRGBsAsInt), [typeof(int), typeof(int), typeof(int)]);
        if (!NotNull(harmony) || !NotNull(target)) return;
        _ = harmony.Patch(target, new HarmonyMethod(Prefix), finalizer: new HarmonyMethod(Finalizer));
        Installed = true;
    }

    private static bool Prefix(BlockAccessorReadLockfree __instance, int posX, int posY, int posZ, ref int __result,
        out WorldChunk? __state)
    {
        __state = null;
        if (!Enabled || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return true;
        if (Held(posX, posY, posZ, out var held))
        {
            if (Counting.Hud) Reads++;
            __result = held;
            return false;
        }

        var chunk = Chunk(__instance, posX, posY, posZ);
        // no chunk: the engine's own fallback, no lock involved
        return chunk is null || Enter(chunk, ref __result, out __state);
    }

    // True: the engine reads under the lock taken into state. False: result is an earlier light.
    internal static bool Enter(WorldChunk chunk, ref int result, out WorldChunk? state)
    {
        state = null;
        var gate = ChunkInternals.PackUnpackLock(chunk);
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

        // held across the original, which reenters it; the finalizer releases it
        state = chunk;
        return true;
    }

    // Releases the prefix's lock exactly once, also when the original threw
    internal static Exception? Finalizer(Exception? __exception, WorldChunk? __state, int __result, int posX, int posY, int posZ)
    {
        if (__state is null) return __exception;
        if (__exception is null)
        {
            (_last, _hasLast) = (__result, true);
            var slot = Claim(__state, Find(__state));
            (Slots[slot].Light, Slots[slot].Known, Slots[slot].BusyUntil) = (__result, true, 0);
            Lights[Index(posX, posY, posZ)] = new Cell
                { X = posX, Y = posY, Z = posZ, Light = __result, Until = Environment.TickCount64 + HoldMs };
        }

        Monitor.Exit(ChunkInternals.PackUnpackLock(__state));
        return __exception;
    }

    // Leaving the world: no chunk of it stays reachable from here
    internal static void Forget()
    {
        Array.Clear(Slots);
        Array.Clear(Lights);
        (_next, _last, _hasLast) = (0, 0, false);
    }

    // The light read at the block within HoldMs
    internal static bool Held(int x, int y, int z, out int light)
    {
        ref var cell = ref Lights[Index(x, y, z)];
        light = cell.Light;
        return cell.Until > Environment.TickCount64 && cell.X == x && cell.Y == y && cell.Z == z;
    }

    private static int Index(int x, int y, int z) => (int)((uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791) & (Cells - 1));

    // The chunk's last light, else the last anywhere; the very first read waits like the engine's
    private static bool Served(int slot, ref int result)
    {
        if (slot >= 0 && Slots[slot].Known) result = Slots[slot].Light;
        else if (_hasLast) result = _last;
        else return true;

        return false;
    }

    private static int Find(WorldChunk chunk)
    {
        for (var i = 0; i < Remembered; i++)
            if (ReferenceEquals(Slots[i].Owner, chunk)) return i;
        return -1;
    }

    // The chunk's slot, else the next round robin, emptied: a swarm stays within a few chunks
    private static int Claim(WorldChunk chunk, int slot)
    {
        if (slot >= 0) return slot;
        var i = _next;
        _next = (_next + 1) % Remembered;
        Slots[i] = new Slot { Owner = chunk };
        return i;
    }

    private static WorldChunk? Chunk(BlockAccessorReadLockfree accessor, int x, int y, int z)
    {
        var map = WorldMapOf(accessor);
        if (!NotNull(map) || (x | y | z) < 0 || x >= map.MapSizeX || y >= map.MapSizeY || z >= map.MapSizeZ)
            return null;
        const int size = GlobalConstants.ChunkSize;
        return map.GetChunkNonLocking(x / size, y / size, z / size) as WorldChunk;
    }

    private struct Cell
    {
        public int X, Y, Z, Light;
        public long Until;
    }

    private struct Slot
    {
        public WorldChunk? Owner;
        public long BusyUntil;
        public int Light;
        public bool Known;
    }
}
