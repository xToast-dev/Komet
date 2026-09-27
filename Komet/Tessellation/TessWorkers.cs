using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// Chunk tessellation as WorkerPool's background job. The engine tessellates on one thread with one ChunkTesselator, 96-99 % busy while
// terrain loads. A pool thread that takes a tessellation job gets a ChunkTesselator of its own, asked for on first use and made on the
// main thread as the engine makes its one (the constructor adds a settings watcher; LightlevelsReceived and BlockTexturesLoaded run
// Start, which reads the atlases and subscribes the instance to texture and shape reloads), then pinned to that thread for its life:
// BeginProcessChunk reloads the shape tesselator of the thread it runs on when its instance asks for it. A job is one normal pass from
// TessSchedule's queue - nearest first, one pass per chunk at a time - through the engine's own ChunkTesselatorManager.TesselateChunk,
// every patch on it included; a transpiler makes its one read of game.TerrainChunkTesselator this thread's instance. The engine's
// thread keeps the priority marks and takes from the same queue.
//
// What else the engine shares between passes TessSafety makes safe; without it installed completely there are no jobs. A pass the
// engine wants again (RetryTesselationException, a chunk not loaded yet) or one that threw goes to the engine's thread: the atlas and
// meal mesh code queue their main-thread fix only when the retry is thrown on game.TesselationThreadId. An atlas created at runtime is
// forwarded to every pool thread's instance, as the engine tells its one.
//
// Parallel (TessSafety's locks) is switched by the engine's thread at the start of its tick, where no pass of its own runs, and only
// off when no pool thread is inside a pass: a pool thread counts itself in (_passing) before it reads Parallel, the engine's thread
// switches off before it reads _passing, both through full fences, so at least one of them sees the other and no pass runs unguarded.
// An exception in a pass stops the jobs for this world and logs once, rather than end the game as an exception on a client thread
// would: the engine's thread goes on alone.
internal static class TessWorkers
{
    private const int TesselatorSites = 1, DefaultJobs = 2;

    // A backlog past BoostOn (a world join or a teleport: 3000-4700 marks waited in game, flight with the engine's thread alone peaked
    // at 2600) lets every pool thread tessellate until it drops under BoostOff; otherwise at most Jobs at once, since eight passes at
    // once took 3.9 instead of 1.5 ms each and halved the 1 % low in flight
    private const int BoostOn = 3000, BoostOff = 500;

    // By pool thread, main thread writes
    private static readonly ChunkTesselator?[] Made = new ChunkTesselator?[WorkerPool.MaxThreads];

    // 1 while a pool thread's instance is being made
    private static readonly int[] Asked = new int[WorkerPool.MaxThreads];

    private static readonly Func<bool> Job = Pass;
    private static volatile ClientMain? _game; // the world the jobs belong to
    private static volatile ChunkTesselatorManager? _manager;

    // A pool thread's instance on its thread; null on every other thread
    [ThreadStatic] private static ChunkTesselator? _own;

    private static int _passing, _sites;

    // Pool threads that may tessellate at once without a backlog; 0 = none
    public static int Jobs { get; set; } = DefaultJobs;

    public static bool Boosted { get; private set; }

    public static bool Installed { get; private set; }
    public static bool Failed { get; private set; } // an exception stopped the jobs for this world
    public static bool Passing => Volatile.Read(ref _passing) > 0;

    public static void Install(Harmony harmony)
    {
        Stop();
        var (i, b) = (typeof(int), typeof(bool));
        var pass = TessSeams.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.TesselateChunk), i, i,
            i, b, b, b.MakeByRefType());
        var atlas = TessSeams.Method(typeof(ChunkTesselator), nameof(ChunkTesselator.RuntimeCreateNewBlockTextureAtlas),
            i);
        // The private engine fields the accessors reach, with their types (a missing one would throw on a game thread): game
        // here, started with TessSchedule's install
        var game = AccessTools.DeclaredField(typeof(ChunkTesselator), "game");
        if (!NotNull(harmony) || !NotNull(pass) || !NotNull(atlas) || !TessSafety.Installed ||
            !TessSchedule.Installed || !Assert(game?.FieldType == typeof(ClientMain))) return;
        _sites = 0;
        _ = NotNull(harmony.Patch(pass, transpiler: new HarmonyMethod(OwnTesselator)));
        if (!Assert(Volatile.Read(ref _sites) == TesselatorSites))
        {
            harmony.Unpatch(pass, AccessTools.Method(typeof(TessWorkers), nameof(OwnTesselator)));
            return;
        }

        _ = NotNull(harmony.Patch(atlas, postfix: new HarmonyMethod(Forward)));
        Installed = true;
        WorkerPool.Background = Job;
    }

    // Leaving the world or the mod (its patches go next): no more jobs
    public static void Stop()
    {
        if (ReferenceEquals(WorkerPool.Background, Job)) WorkerPool.Background = null;
        (Installed, _game, _manager, Failed, Boosted) = (false, null, null, false, false);
        WorkerPool.BackgroundLimit = 0;
        Array.Clear(Made);
        Array.Clear(Asked);
        _ = Assert(!Installed);
    }

    // The one read of game.TerrainChunkTesselator in TesselateChunk becomes this thread's instance; any other count of reads leaves
    // the IL alone and the install declines
    private static List<CodeInstruction> OwnTesselator(IEnumerable<CodeInstruction> instructions)
    {
        var field = AccessTools.DeclaredField(typeof(ClientMain), nameof(ClientMain.TerrainChunkTesselator));
        var mine = AccessTools.Method(typeof(TessWorkers), nameof(Tesselator));
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code)) || !NotNull(field) || !NotNull(mine))
            return code;
        var at = Il.Single(code, c => c.opcode == OpCodes.Ldfld && Equals(c.operand, field));
        Volatile.Write(ref _sites, at >= 0 && Il.Substitute(code, at, mine) ? TesselatorSites : 0);
        return code;
    }

    private static ChunkTesselator Tesselator(ClientMain game)
    {
        var own = _own;
        _ = Assert(own is null || WorkerPool.Current >= 0); // only pool threads have their own
        return own is not null && ReferenceEquals(TessSeams.Game(own), game) ? own : game.TerrainChunkTesselator;
    }

    // An atlas the engine's instance was told about reaches every pool thread's instance too (on the main thread, which the engine's
    // own call runs on; each instance takes its own ReloadLock)
    private static void Forward(ChunkTesselator __instance, int textureId)
    {
        if (!NotNull(__instance) || !Assert(textureId >= 0)) return;
        var game = TessSeams.Game(__instance);
        // A pool thread's __instance: this loop's call
        if (game is null || !ReferenceEquals(game.TerrainChunkTesselator, __instance)) return;
        // BlockTextureAtlasManager.RuntimeCreateNewAtlas
        _ = Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId);
        foreach (var made in Made.Bounded(WorkerPool.MaxThreads))
            if (made is not null && ReferenceEquals(TessSeams.Game(made), game) && TessSeams.Started(made))
                _ = made.RuntimeCreateNewBlockTextureAtlas(textureId);
    }

    // On the engine's thread at the start of each tick: switches Parallel for the pool. This thread's own passes take the locks exactly
    // while Parallel is on.
    internal static void Steer(ClientMain game, ChunkTesselatorManager manager, bool active)
    {
        if (!Installed || !NotNull(game) || !NotNull(manager)) return;
        if (!ReferenceEquals(game, _game)) // a new world: the old one's instances are no one's any more
        {
            Array.Clear(Made);
            Array.Clear(Asked);
            (_game, _manager, Failed) = (game, manager, false);
        }

        var backlog = TessSchedule.Backlog;
        Boosted = backlog > BoostOn || (Boosted && backlog > BoostOff);
        var jobs = Math.Clamp(Jobs, 0, WorkerPool.MaxThreads);
        var on = active && jobs > 0 && !Failed && !TessSafety.Broken && !WorkerPool.BackgroundOff &&
                 WorkerPool.Running > 0;
        var limit = Boosted ? WorkerPool.MaxThreads : jobs;
        WorkerPool.BackgroundLimit = on ? limit : 0;
        Switch(on);
        TessSafety.Guard(TessSafety.Parallel);
        if (on && backlog > 0) WorkerPool.WakeBackground();
    }

    private static void Switch(bool on)
    {
        _ = Assert(WorkerPool.Current < 0); // the engine's thread, never a pool thread
        if (on)
        {
            TessSafety.Open(true);
            return;
        }

        if (!TessSafety.Parallel || Passing) return;
        TessSafety.Open(false);
        // A pool thread counted itself in first: off at a later tick
        if (Volatile.Read(ref _passing) > 0) TessSafety.Open(true);
    }

    // The background job: one pass on this pool thread, true when it ran one
    private static bool Pass()
    {
        var (game, manager) = (_game, _manager);
        if (!Installed || Failed || game is null || manager is null || !game.ShouldTesselateTerrain ||
            Own(game) is null) return false;
        _ = Assert(_own is not null && WorkerPool.Current >= 0);
        _ = Interlocked.Increment(ref _passing); // counted in before Parallel is read (the class comment)
        try
        {
            return TessSafety.Parallel && TessSchedule.Work(manager, game);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or ThreadInterruptedException))
        {
            Fail(game, e);
            return false;
        }
        finally
        {
            _ = Assert(Interlocked.Decrement(ref _passing) >= 0);
        }
    }

    // This pool thread's instance for the world, or null while the main thread makes it
    private static ChunkTesselator? Own(ClientMain game)
    {
        var own = _own;
        if (own is not null && ReferenceEquals(TessSeams.Game(own), game)) return own;
        var slot = WorkerPool.Current;
        if (!Index(slot, Made.Length)) return null;
        if (Volatile.Read(ref Made[slot]) is { } made && ReferenceEquals(TessSeams.Game(made), game) &&
            TessSeams.Started(made))
        {
            TessSafety.Guard(true); // a pool thread passes only while Parallel is on
            return _own = made;
        }

        if (Interlocked.Exchange(ref Asked[slot], 1) == 0)
            game.EnqueueMainThreadTask(() => Make(game, slot), "komet-tesselator");
        return null;
    }

    // On the main thread: the instance is made where the engine makes its own
    private static void Make(ClientMain game, int slot)
    {
        if (!Index(slot, Made.Length) || !ReferenceEquals(game, _game) || game.threadsShouldExit) return;
        _ = Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId);
        try
        {
            var tesselator = new ChunkTesselator(game);
            tesselator.LightlevelsReceived();
            tesselator.BlockTexturesLoaded(); // both in: Start()
            if (Assert(TessSeams.Started(tesselator))) Volatile.Write(ref Made[slot], tesselator);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(game, e);
        }
        finally
        {
            Volatile.Write(ref Asked[slot], 0);
        }
    }

    // A test's instance for a pool thread, as Make would leave it
    internal static void Provide(int slot, ChunkTesselator tesselator)
    {
        if (Index(slot, Made.Length) && NotNull(tesselator)) Volatile.Write(ref Made[slot], tesselator);
    }

    private static void Fail(ClientMain game, Exception e)
    {
        // An unclean exit, as the engine's own threads report it
        if (!NotNull(e) || game.threadsShouldExit || Failed) return;
        Failed = true;
        game.Logger.Error("Komet: a tessellation pass on a worker thread failed, the engine's thread goes on alone " +
                          "for this world: {0}", e);
    }
}
