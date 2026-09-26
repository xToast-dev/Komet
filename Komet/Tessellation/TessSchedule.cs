using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// The order in which chunks are tessellated. The engine's tick (ChunkTesselatorManager.OnSeperateThreadGameTick, on the
// "tesselateterrain" thread) works dirtyChunksPriority (block edits near the player) completely, then game.dirtyChunks in arrival order
// up to a vertex budget, then five marks of dirtyChunksLast, where SystemRenderTerrain.OnPlayerLeaveChunk moves the whole backlog after
// a jump of more than five chunks. Distance counts only at upload, so a chunk next to the player waited behind all that came before it.
// Here the tick is Komet's: the priority loop as the engine runs it, then every normal and last mark moves into a TessQueue that hands
// out the nearest chunk (weighted by the angle to the camera) within the engine's vertex budget. The passes, the budget, the early
// return while priority chunks wait, the recycling and the requeues (back in the next tick) stay the engine's. Switched off, what
// waits goes back into game.dirtyChunks.
// The tick and its state (Batch, Busy, the aim) belong to the tessellation thread, the tick's only caller. Komet's worker threads
// (TessWorkers) take normal passes from the same queue (Work), whose lock hands each chunk to one thread at a time; the tessellation
// thread keeps the priority marks and the passes the workers hand back. The main thread reads only the published count and the bound
// world, and Clear only drops the world (the next Bind empties the queue).
internal static class TessSchedule
{
    private const int MaxPerTick = 1 << 16, MaxCollect = 1 << 14, MaxPriority = 1 << 14, NearRadius = 6, AimMs = 200;
    private const double TurnCos = 0.866; // turning further than 30° scores again

    private static readonly TessQueue Waiting = new();
    private static readonly List<long> Batch = [];

    // Priority marks of chunks a worker is on: back into the engine's queue after the loop
    private static readonly List<long> Busy = [];

    private static volatile ClientMain? _game;
    private static TessView _aim;
    private static long _aimedAt, _nearAt;
    private static int _near;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static int NearWaiting => Volatile.Read(ref _near); // marks within NearRadius columns of the player
    public static int Backlog => Waiting.Count; // marks waiting for a normal pass
    private static bool Active => Installed && Enabled;

    public static void Install(Harmony harmony)
    {
        Installed = false;
        Clear();
        var manager = typeof(ChunkTesselatorManager);
        var tick = AccessTools.DeclaredMethod(manager, nameof(ChunkTesselatorManager.OnSeperateThreadGameTick),
            [typeof(float)]);
        var frame = AccessTools.DeclaredMethod(manager, nameof(ChunkTesselatorManager.OnBeforeFrame), [typeof(float)]);
        if (!NotNull(harmony) || !NotNull(tick) || !NotNull(frame) || !Seams()) return;
        _ = NotNull(harmony.Patch(tick, new HarmonyMethod(Tick)));
        _ = NotNull(harmony.Patch(frame, postfix: new HarmonyMethod(Counted)));
        Installed = true;
    }

    // Every private engine field the accessors reach (TessSeams.Started's too), with its type, and the pass the tick calls: a missing
    // one would throw inside the tick, on the tessellation thread, and ClientThread exits the game on any exception there
    private static bool Seams()
    {
        var (main, queue, gate) = (typeof(ClientMain), typeof(UniqueQueue<long>), typeof(object));
        Type[] pass = [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()];
        return Assert(Is(typeof(ClientSystem), "game", main)) &&
               Assert(Is(typeof(ChunkTesselator), "started", typeof(bool))) &&
               Assert(Is(main, "dirtyChunks", queue)) && Assert(Is(main, "dirtyChunksLock", gate)) &&
               Assert(Is(main, "dirtyChunksLast", queue)) && Assert(Is(main, "dirtyChunksLastLock", gate)) &&
               Assert(Is(main, "dirtyChunksPriority", queue)) && Assert(Is(main, "dirtyChunksPriorityLock", gate)) &&
               Assert(AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager), "TesselateChunk", pass)?.ReturnType ==
                      typeof(int));
    }

    // Drops the world, from Install and from Dispose (on whichever thread disposes); the queue itself is emptied by the next Bind
    public static void Clear()
    {
        _game = null;
        Volatile.Write(ref _near, 0);
    }

    // Harmony injects the instance by name
    private static bool Tick(ChunkTesselatorManager __instance)
    {
        var game = NotNull(__instance) ? Game(__instance) : null;
        if (!NotNull(game) || !NotNull(game.WorldMap)) return true;
        if (!Active)
        {
            TessWorkers.Steer(game, __instance, false); // here and below: no pass of this thread runs
            if (TessWorkers.Passing) return false; // the engine's tick could take the chunk a worker still tessellates
            if (ReferenceEquals(game, _game) && Waiting.Count > 0) GiveBack(game);
            Measure(game, true);
            return true;
        }

        if (game.TerrainChunkTesselator is not { } tesselator || !TessSeams.Started(tesselator) ||
            !NotNull(game.frustumCuller)) return false;
        TessWorkers.Steer(game, __instance, true);
        Bind(game); // after started: ChunkTesselator.Start reads the map size, so the index multipliers are set
        if (Priority(__instance, game) && Home(__instance, game))
        {
            Collect(game);
            Aim(game, false);
            Measure(game, false);
            // The engine leaves the rest of the tick to new priority chunks, recycling too
            if (!Normal(__instance, game)) return false;
        }

        MeshData.Recycler?.DoRecycling();
        return false;
    }

    private static void Counted(ChunkTesselatorManager __instance)
    {
        if (!Active || !NotNull(__instance) || !ReferenceEquals(Game(__instance), _game)) return;
        // The engine counted its three queues, these moved out of them
        RuntimeStats.chunksAwaitingTesselation += Waiting.Count;
        _ = Assert(RuntimeStats.chunksAwaitingTesselation >= 0);
    }

    // The engine's priority loop, except that an empty queue ends it instead of throwing, a switch-off ends it before a mark is taken
    // instead of dropping that mark, and a chunk a worker is on waits for the next tick. False when tessellation is switched off.
    private static bool Priority(ChunkTesselatorManager manager, ClientMain game)
    {
        var gate = PriorityLock(game);
        if (!NotNull(gate) || !NotNull(PriorityQueue(game)) || !Assert(Busy.Count == 0)) return false;
        var go = PriorityMarks(manager, game, gate);
        if (Busy.Count == 0) return go;
        lock (gate)
        {
            foreach (var mark in Busy.Bounded(MaxPriority)) PriorityQueue(game).Enqueue(mark);
        }

        Busy.Clear();
        return go;
    }

    private static bool PriorityMarks(ChunkTesselatorManager manager, ClientMain game, object gate)
    {
        var left = PriorityQueue(game).Count; // the engine's count, which a merge counts down too
        for (var i = 0; i < MaxPriority && left-- > 0; i++)
        {
            if (!game.ShouldTesselateTerrain) return false;
            long mark;
            lock (gate)
            {
                var queue = PriorityQueue(game);
                if (queue.Count == 0) break;
                mark = queue.Dequeue();
                var full = mark & long.MaxValue;
                if (mark < 0 && queue.Contains(full))
                {
                    queue.Remove(full);
                    (mark, left) = (full, left - 1); // the full pass stands in for the edge-only mark
                }
            }

            if (!Waiting.TryBegin(mark & long.MaxValue))
            {
                Busy.Add(mark);
                continue;
            }

            if (Owned(manager, game, mark, true, out _))
                lock (gate)
                {
                    PriorityQueue(game).Enqueue(mark);
                }
        }

        return Assert(Busy.Count <= MaxPriority) && game.ShouldTesselateTerrain;
    }

    // The passes the workers handed back, run here. False when tessellation is switched off.
    private static bool Home(ChunkTesselatorManager manager, ClientMain game)
    {
        for (var i = 0; i < MaxPriority && game.ShouldTesselateTerrain && Waiting.TryTakeHome(out var mark); i++)
            if (Owned(manager, game, mark, false, out _)) Waiting.Defer(mark);
        return game.ShouldTesselateTerrain;
    }

    // Nearest first up to the engine's vertex budget; a requeue comes back with the next tick's Collect. False when priority chunks
    // arrived meanwhile.
    private static bool Normal(ChunkTesselatorManager manager, ClientMain game)
    {
        var budget = (game.frustumCuller.ViewDistanceSq + 16800) * 3 / 2;
        var vertices = 0;
        for (var i = 0; i < MaxPerTick && vertices < budget; i++)
        {
            if (PriorityQueue(game).Count > 0) return false;
            if (!game.ShouldTesselateTerrain || !Waiting.TryTake(out var mark)) break;
            if (Owned(manager, game, mark, false, out var added)) Waiting.Defer(mark);
            vertices += added;
        }

        return Assert(vertices >= 0);
    }

    // One normal pass on a worker: false when nothing is takeable. A pass the engine wants again, or one that threw, goes to the
    // tessellation thread (Home).
    internal static bool Work(ChunkTesselatorManager manager, ClientMain game)
    {
        if (!Active || !ReferenceEquals(game, _game) || !game.ShouldTesselateTerrain ||
            !Waiting.TryTake(out var mark)) return false;
        var (requeue, finished) = (true, false);
        try
        {
            _ = Pass(manager, game, mark, false, out requeue);
            finished = true;
        }
        finally
        {
            if (requeue || !finished) Waiting.Home(mark);
            else Waiting.Done(mark & long.MaxValue);
        }

        return Assert(finished);
    }

    // A pass of a chunk this thread holds in flight, released afterwards: its vertices, and true when the engine wants it again
    private static bool Owned(ChunkTesselatorManager manager, ClientMain game, long mark, bool priority,
        out int vertices)
    {
        bool requeue;
        try
        {
            vertices = Pass(manager, game, mark, priority, out requeue);
        }
        finally
        {
            Waiting.Done(mark & long.MaxValue);
        }

        return requeue;
    }

    // MapUtil.PosInt3d's arithmetic, into locals: several threads pass at once
    private static int Pass(ChunkTesselatorManager manager, ClientMain game, long mark, bool priority, out bool requeue)
    {
        var (index, mulX, mulZ) = (mark & long.MaxValue, (long)game.WorldMap.index3dMulX,
            (long)game.WorldMap.index3dMulZ);
        requeue = false;
        if (!Assert(mulX > 0 && mulZ > 0)) return 0;
        int x = (int)(index % mulX), y = (int)(index / (mulX * mulZ)), z = (int)(index / mulX % mulZ);
        var vertices = manager.TesselateChunk(x, y, z, priority, mark < 0, out requeue);
        return Assert(vertices >= 0) ? vertices : 0;
    }

    // Everything the engine queued since the last tick moves over, and last tick's requeues come back
    private static void Collect(ClientMain game)
    {
        if (!Assert(Batch.Count == 0)) Batch.Clear(); // emptied at the end of every Collect
        Take(DirtyLock(game), ref Dirty(game));
        Take(LastLock(game), ref Last(game));
        Waiting.AddRange(CollectionsMarshal.AsSpan(Batch));
        Batch.Clear();
        Waiting.Undefer();
    }

    // SystemRenderTerrain.RedrawAllBlocks assigns game.dirtyChunks a new queue (without the lock): the field is read anew through the ref
    private static void Take(object gate, ref UniqueQueue<long> field)
    {
        if (!NotNull(gate)) return;
        lock (gate)
        {
            var queue = field;
            var count = NotNull(queue) ? queue.Count : 0; // shrinks as the loop takes
            for (var i = 0; i < Math.Min(count, MaxCollect); i++) Batch.Add(queue.Dequeue());
        }

        _ = Assert(Batch.Count <= 2 * MaxCollect);
    }

    // Another world, or the first tick after Clear: what waits belongs to no world any more
    private static void Bind(ClientMain game)
    {
        if (ReferenceEquals(game, _game)) return;
        Waiting.Clear();
        Waiting.Map(game.WorldMap.index3dMulX, game.WorldMap.index3dMulZ);
        _aimedAt = 0;
        _game = game;
        Aim(game, true);
        _ = Assert(Waiting.Count == 0); // a world starts with nothing waiting
    }

    // Scores again when the player is in another chunk or looks elsewhere, at most every AimMs
    private static void Aim(ClientMain game, bool force)
    {
        if (View(game) is not { } v) return;
        var now = Environment.TickCount64;
        var moved = v.X != _aim.X || v.Y != _aim.Y || v.Z != _aim.Z || v.Fx * _aim.Fx + v.Fz * _aim.Fz < TurnCos;
        if (!force && (!moved || now - _aimedAt < AimMs)) return;
        (_aim, _aimedAt) = (v, now);
        Waiting.Aim(v);
    }

    private static TessView? View(ClientMain game)
    {
        var pos = game.EntityPlayer?.Pos;
        if (pos is null || !Finite(pos.X) || !Finite(pos.Z) || !Finite(game.mouseYaw)) return null;
        var (fx, fz) = TessQueue.Facing(game.mouseYaw);
        return new TessView((int)Math.Floor(pos.X / 32), (int)Math.Floor(pos.Y / 32) + pos.Dimension * 1024,
            (int)Math.Floor(pos.Z / 32), fx, fz);
    }

    // Switched off: what still waits goes back into game.dirtyChunks
    private static void GiveBack(ClientMain game)
    {
        var gate = DirtyLock(game);
        if (!NotNull(gate)) return;
        lock (gate)
        {
            Waiting.Drain(Dirty(game));
        }

        _ = Assert(Waiting.Count == 0);
    }

    // Marks within NearRadius columns of the player that still wait, every AimMs while someone looks: in the queue while the schedule
    // runs (the engine's queues were just emptied into it), else in the engine's two queues, under their locks
    private static void Measure(ClientMain game, bool engine)
    {
        var now = Environment.TickCount64;
        if (!Counting.On || now - _nearAt < AimMs || View(game) is not { } v) return;
        _nearAt = now;
        long mulX = game.WorldMap.index3dMulX, mulZ = game.WorldMap.index3dMulZ;
        var near = engine
            ? Near(DirtyLock(game), ref Dirty(game), v, mulX, mulZ) +
              Near(LastLock(game), ref Last(game), v, mulX, mulZ)
            : Waiting.Near(v, NearRadius);
        Volatile.Write(ref _near, near);
    }

    private static int Near(object gate, ref UniqueQueue<long> field, TessView v, long mulX, long mulZ)
    {
        if (!NotNull(gate)) return 0;
        var n = 0;
        lock (gate)
        {
            if (!NotNull(field)) return 0;
            using var marks = field.GetEnumerator(); // a boxed enumerator per AimMs, only while someone looks
            for (var i = 0; i < TessQueue.MaxPending && marks.MoveNext(); i++)
                n += TessQueue.Within(marks.Current & long.MaxValue, v, NearRadius, mulX, mulZ) ? 1 : 0;
        }

        return n;
    }

    private static bool Is(Type owner, string name, Type type)
    {
        return AccessTools.DeclaredField(owner, name) is { IsStatic: false } field && field.FieldType == type;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref ClientMain? Game(ClientSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dirtyChunks")]
    private static extern ref UniqueQueue<long> Dirty(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dirtyChunksLock")]
    private static extern ref object DirtyLock(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dirtyChunksLast")]
    private static extern ref UniqueQueue<long> Last(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dirtyChunksLastLock")]
    private static extern ref object LastLock(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dirtyChunksPriority")]
    private static extern ref UniqueQueue<long> PriorityQueue(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dirtyChunksPriorityLock")]
    private static extern ref object PriorityLock(ClientMain game);
}
