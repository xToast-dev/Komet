using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Core;

// Komet's worker threads, one pool for everything that runs beside the game's threads: frame jobs (FrustumSweep's cull of one
// MeshDataPoolManager.Render call) and background jobs (tessellation passes, TessWorkers). Frame jobs win: a worker helps a published
// batch first and runs one background job only when no batch waits - and only while fewer than BackgroundLimit workers are in one, so
// the background work cannot crowd out the main thread.
//
// The shared cursor holds the batch's count in its high half and the next index in its low half: one Interlocked increment claims an
// index and says of which batch, so a worker that saw the previous batch can never claim an item of the next one under the old count.
// The caller never waits for a worker that is inside a background job: a worker only claims between jobs and finishes what it claimed
// before it takes anything else, so the caller waits at most for the items workers claimed, one each. Nothing is allocated per batch.
// A resting worker marks itself Idle through a full fence before it looks at the cursor a last time, and the caller publishes through
// a full fence before it looks for Idle workers: either the caller wakes it or it sees the batch.
//
// Threads belong to a world: they join its list of client threads (DestroyGameSession waits for them like for its own) and end when it
// exits, when the pool moves to another world, or at Stop. Fewer wanted than running parks the rest; they are reused, so a thread keeps
// whatever it pinned to itself (its ChunkTesselator). Threads run at normal priority: an item a worker claimed holds up the frame.
internal static class WorkerPool
{
    public enum FrameResult
    {
        Declined,
        Done,
        Failed
    }

    public const int MaxThreads = 8, MaxItems = 1 << 16, MaxFrameFailures = 2;

    private const int IdleMs = 50, Spins = 32, SignalSpins = 8, JoinMs = 2000, FrameCounter = 0, BackgroundCounter = 1,
        MaxRetired = 64, MaxWaits = 1 << 24;

    private const long MaxRounds = long.MaxValue; // a worker runs until its world or generation ends
    private const long Low = 0xFFFFFFFFL;
    private static readonly int DefaultThreads = Math.Clamp(Environment.ProcessorCount - 2, 1, MaxThreads);

    private static readonly bool Accessible = Seams();
    private static readonly Lock Gate = new();
    private static readonly ManualResetEventSlim Finished = new(false);
    private static int _relay; // resting workers still to be woken by the ones woken already

    // Worker ticks on frame and background jobs, while Counting.On
    private static readonly Tally Busy = new(BackgroundCounter + 1);

    private static readonly List<Worker> Retired = []; // workers of an earlier world or generation, until they ended
    private static Worker[] _workers = [];
    private static object? _world; // the ClientMain the threads belong to; null for a pool without a world (tests)
    private static int _generation, _done, _frameFailures, _backgroundLimit, _inBackground;
    private static long _cursor; // count << 32 | next index; count 0 while no batch is open
    private static Action<int>? _job;
    private static Exception? _error;
    [ThreadStatic] private static int _number; // this thread's worker number + 1; 0 on every other thread

    // 0: no pool, culling and tessellation stay on the game's threads
    public static int Wanted { get; set; } = DefaultThreads;

    public static Func<bool>? Background { get; set; } // one background job, true when it ran one; must not throw

    public static bool FrameOff => Volatile.Read(ref _frameFailures) >= MaxFrameFailures;

    public static bool BackgroundOff { get; private set; } // a background job threw past its own handler
    public static Exception? LastError { get; private set; }
    public static int Current => _number - 1;
    public static long FrameTicks => Busy.Total(FrameCounter);
    public static long BackgroundTicks => Busy.Total(BackgroundCounter);
    public static int InBackground => Volatile.Read(ref _inBackground);

    // How many workers may be inside a background job at once; set by the job's owner (TessWorkers)
    public static int BackgroundLimit
    {
        get => Volatile.Read(ref _backgroundLimit);
        set => Volatile.Write(ref _backgroundLimit, Assert(value is >= 0 and <= MaxThreads) ? value : 0);
    }

    // Whether a frame batch could run now: workers running, frame batches not switched off, none open (FrustumSweep's stage batch
    // stays open across a render stage's calls)
    public static bool Free => _job is null && !FrameOff && _number == 0 && Running > 0;

    public static int Running
    {
        get
        {
            var n = 0;
            foreach (var worker in Volatile.Read(ref _workers).Bounded(MaxThreads))
                if (!worker.Parked)
                    n++;
            return Assert(n <= MaxThreads) ? n : 0;
        }
    }

    // On the main thread, every few hundred milliseconds: the wanted number of threads for the world being played
    public static void Steer(ClientMain? game)
    {
        if (game is null || game.threadsShouldExit ||
            !Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)) return;
        Resize(game, Math.Clamp(Wanted, 0, MaxThreads));
    }

    // Threads for `world` (a ClientMain, or null without one): another world retires every thread first
    internal static void Resize(object? world, int wanted)
    {
        if (!Assert(wanted is >= 0 and <= MaxThreads) || (world is not null && !Assert(Accessible))) return;
        lock (Gate)
        {
            if (!ReferenceEquals(world, _world)) Retire();
            _world = world;
            var workers = _workers;
            for (var i = 0; i < Math.Min(workers.Length, MaxThreads); i++) workers[i].Parked = i >= wanted;
            if (wanted <= workers.Length)
            {
                _ = Assert(Running == wanted);
                return;
            }

            var grown = new Worker[wanted];
            workers.CopyTo(grown, 0);
            for (var i = workers.Length; i < Math.Min(wanted, MaxThreads); i++)
            {
                var worker = grown[i] = new Worker(world as ClientMain, i, Volatile.Read(ref _generation));
                if (world is ClientMain game)
                    Threads(game).Add(worker.Thread); // on the main thread, which DestroyGameSession iterates on
                _ = Assert(!worker.Thread.IsAlive);
                worker.Thread.Start();
            }

            Volatile.Write(ref _workers, grown);
            _ = Assert(grown.Length <= MaxThreads);
        }
    }

    // Leaving the world or the mod: every thread ends; true when all did within JoinMs
    public static bool Stop()
    {
        Worker[] ending;
        lock (Gate)
        {
            Retire();
            _world = null;
            ending = [.. Retired];
            Retired.Clear();
            _ = Assert(_workers.Length == 0);
            (_frameFailures, BackgroundOff, LastError) = (0, false, null);
        }

        var (joined, deadline) = (true, Environment.TickCount64 + JoinMs);
        foreach (var worker in ending.Bounded(MaxRetired))
        {
            var ended = worker.Thread.Join((int)Math.Max(0, deadline - Environment.TickCount64));
            if (ended) worker.Dispose();
            joined &= ended;
        }

        return joined;
    }

    // Under Gate: the running threads end after their current job, the resting ones at once
    private static void Retire()
    {
        _ = Interlocked.Increment(ref _generation);
        var alive = 0;
        for (var i = 0; i < Math.Min(Retired.Count, MaxRetired); i++)
            if (Retired[i].Thread.IsAlive) Retired[alive++] = Retired[i];
            else Retired[i].Dispose();
        Retired.RemoveRange(alive, Retired.Count - alive);
        foreach (var worker in _workers.Bounded(MaxThreads))
        {
            worker.Parked = true;
            worker.Signal.Set();
            Retired.Add(worker);
        }

        Volatile.Write(ref _workers, []);
        _ = Assert(Retired.Count <= MaxRetired);
    }

    // Runs job(0..count-1) on the calling thread and every free worker, waking up to `helpers` resting ones; Declined when there is no
    // pool (the caller does the work its own way), Failed when an item threw (every item has ended; LastError holds the first exception)
    public static FrameResult RunFrame(Action<int> job, int count, int helpers)
    {
        if (!NotNull(job) || count < 2 || !OpenFrame(job, count, Math.Min(helpers, count - 1)))
            return FrameResult.Declined;
        var mine = Claim();
        _ = Assert(mine <= count);
        var spin = new SpinWait();
        for (var i = 0; i < Spins && Volatile.Read(ref _done) < count; i++) spin.SpinOnce(-1);
        if (Volatile.Read(ref _done) < count) Finished.Wait();
        _ = Assert(Volatile.Read(ref _done) == count); // every item ran exactly once
        _ = Interlocked.Exchange(ref _cursor, 0);
        _ = Assert(ReferenceEquals(_job, job)); // no second batch ran meanwhile
        return Ended();
    }

    // A batch the caller does not wait for (RunFrame opens its batch here, then waits): the call returns at once and the workers claim
    // its items while the caller goes on. The caller takes items itself with ClaimBelow, waits for whatever it needs through the job's own
    // bookkeeping, and ends the batch with CloseFrame before anything else opens one. False when there is no pool to run it.
    public static bool OpenFrame(Action<int> job, int count, int helpers)
    {
        if (!NotNull(job) || !Assert(count <= MaxItems) || count < 1 || Running == 0 || FrameOff ||
            !Assert(_number == 0) || !Assert(_job is null))
            return false;
        (_job, _done, _error) = (job, 0, null);
        Finished.Reset();
        _ = Interlocked.Exchange(ref _cursor, (long)count << 32); // publishes the batch
        _ = Assert(Wake(Math.Min(helpers, count)) <= MaxThreads);
        return true;
    }

    // The open batch's items the caller runs itself: the next one, as long as its index lies below `below`; how many it ran. A compare
    // and swap takes it, so the caller never claims one past the bound, while the workers' increments go on claiming beside it.
    public static int ClaimBelow(int below)
    {
        var ran = 0;
        for (var i = 0; i < MaxItems && _job is not null; i++)
        {
            var seen = Volatile.Read(ref _cursor);
            var (count, at) = ((int)(seen >>> 32), (int)(seen & Low));
            if (at >= Math.Min(count, below)) break;
            if (Interlocked.CompareExchange(ref _cursor, seen + 1, seen) != seen) continue;
            Item(at, count);
            ran++;
        }

        return Assert(ran <= MaxItems) ? ran : 0;
    }

    // Ends the batch OpenFrame opened: no item is handed out any more, and once every item a thread claimed has ended it returns.
    // Unclaimed items never run. Declined when none was open.
    public static FrameResult CloseFrame()
    {
        if (_job is null || !Assert(_number == 0)) return FrameResult.Declined;
        var seen = Interlocked.Exchange(ref _cursor, 0);
        var claimed = (int)Math.Min(seen >>> 32, seen & Low); // an increment past the count claimed nothing
        var spin = new SpinWait();
        for (var i = 0; i < MaxWaits && Volatile.Read(ref _done) < claimed; i++) spin.SpinOnce(-1);
        _ = Assert(Volatile.Read(ref _done) == claimed); // every claimed item ended, no other ran
        return Ended();
    }

    private static FrameResult Ended()
    {
        _job = null;
        if (Volatile.Read(ref _error) is not { } error) return FrameResult.Done;
        LastError = error;
        _ = Assert(Interlocked.Increment(ref _frameFailures) > 0);
        return FrameResult.Failed;
    }

    // Up to n resting workers woken: one by the caller, the rest by the relay - each woken worker wakes the next before it
    // claims, so the caller (the main thread, for a frame batch) pays for one wake-up instead of n. Waking only hurries the
    // workers: a batch never waits for a worker it did not claim an item for, and a resting worker looks again after IdleMs.
    // How many the caller woke itself.
    private static int Wake(int n)
    {
        _ = Assert(n <= MaxItems);
        if (n <= 0) return 0;
        Volatile.Write(ref _relay, Math.Min(n, MaxThreads) - 1);
        var woken = WakeOne() ? 1 : 0;
        _ = Assert(woken <= n) && Assert(Volatile.Read(ref _relay) < MaxThreads);
        return woken;
    }

    // One resting worker, taken from its rest (Idle 1 -> 0) so no other waker picks it too; false when none rests
    private static bool WakeOne()
    {
        _ = Assert(_workers.Length <= MaxThreads);
        foreach (var worker in Volatile.Read(ref _workers).Bounded(MaxThreads))
        {
            if (worker.Parked || Interlocked.CompareExchange(ref worker.Idle, 0, 1) != 1) continue;
            worker.Signal.Set();
            return true;
        }

        return false;
    }

    // Resting workers woken for background work, as many as BackgroundLimit leaves room for; for the job's owner when work arrived
    public static void WakeBackground()
    {
        var room = BackgroundLimit - InBackground;
        if (room > 0 && Background is not null && !BackgroundOff) _ = Assert(Wake(room) <= room);
    }

    private static int Claim()
    {
        var ran = 0;
        for (var i = 0; i < MaxItems; i++)
        {
            var claim = Interlocked.Increment(ref _cursor) - 1;
            var (count, at) = ((int)(claim >>> 32), (int)(claim & Low));
            if (at >= count) break;
            Item(at, count);
            ran++;
        }

        return Assert(ran <= MaxItems) ? ran : 0;
    }

    private static void Item(int at, int count)
    {
        try
        {
            _job!(at);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = Interlocked.CompareExchange(ref _error, e, null);
        }

        if (Interlocked.Increment(ref _done) == count) Finished.Set();
        _ = Assert(at < count);
    }

    // The engine fields the accessors reach, with their types: a missing one would throw on a game thread
    private static bool Seams()
    {
        var main = typeof(ClientMain);
        return Assert(AccessTools.DeclaredField(main, "clientThreads")?.FieldType == typeof(List<Thread>)) &&
               Assert(AccessTools.DeclaredField(main, "_clientThreadsCts")?.FieldType ==
                      typeof(CancellationTokenSource));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "clientThreads")]
    private static extern ref List<Thread> Threads(ClientMain game);

    private sealed class Worker : IDisposable
    {
        public readonly ManualResetEventSlim Signal = new(false, SignalSpins);
        private readonly ClientMain? _game;
        private readonly int _slot, _era;
        public int Idle; // 1 while resting on Signal
        public volatile bool Parked;

        public Worker(ClientMain? game, int number, int generation)
        {
            (_game, _slot, _era) = (game, number, generation);
            Thread = new Thread(Loop) { IsBackground = true, Name = "komet-worker-" + (number + 1) };
            _ = Assert(number is >= 0 and < MaxThreads);
        }

        public Thread Thread { get; }

        // Once the thread ended and no one wakes it any more
        public void Dispose()
        {
            _ = Assert(!Thread.IsAlive);
            Signal.Dispose();
        }

        private static bool Open()
        {
            var seen = Volatile.Read(ref _cursor);
            return Assert(seen >= 0) && (seen & Low) < seen >>> 32; // count <= MaxItems: the sign bit stays clear
        }

        private void Loop()
        {
            Number(_slot + 1);
            var token = _game is null ? CancellationToken.None : Cancel(_game)?.Token ?? CancellationToken.None;
            for (long round = 0; round < MaxRounds; round++)
            {
                if (Volatile.Read(ref _generation) != _era || token.IsCancellationRequested ||
                    _game?.threadsShouldExit == true) break;
                if (Parked || (!Help() && !Admit())) Rest();
            }

            Number(0);
            _ = Assert(Thread == Thread.CurrentThread) && Assert(Volatile.Read(ref Idle) == 0);
        }

        private void Rest()
        {
            _ = Interlocked.Exchange(ref Idle, 1);
            var woken = (Parked || !Open()) && Signal.Wait(IdleMs);
            _ = Interlocked.Exchange(ref Idle, 0);
            Signal.Reset(); // a wake that came late is not lost: the loop looks at the cursor before it rests again
            if (woken && !Parked) Relay();
            _ = Assert(Volatile.Read(ref Idle) == 0);
        }

        private static void Relay()
        {
            _ = Assert(_number > 0) && Assert(Volatile.Read(ref _relay) <= MaxThreads);
            for (var i = 0; i < MaxThreads; i++)
            {
                var left = Volatile.Read(ref _relay);
                if (left <= 0) return;
                if (Interlocked.CompareExchange(ref _relay, left - 1, left) != left) continue;
                if (WakeOne()) return; // the one woken relays on
                Volatile.Write(ref _relay, 0); // nobody rests any more
                return;
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clientThreadsCts")]
        private static extern ref CancellationTokenSource? Cancel(ClientMain game);

        private static void Number(int number)
        {
            _ = Assert(number is >= 0 and <= MaxThreads);
            _number = number;
        }

        // A worker's turn at the open batch, if one is open: the stale read only decides whether to claim
        private static bool Help()
        {
            if (!Assert(_number > 0) || !Open()) return false;
            var start = Counting.On ? Stopwatch.GetTimestamp() : 0;
            var ran = Claim();
            if (start != 0) Busy.Add(FrameCounter, Stopwatch.GetTimestamp() - start);
            return ran > 0;
        }

        private static bool Admit()
        {
            if (BackgroundOff || Background is not { } job) return false;
            if (Interlocked.Increment(ref _inBackground) > BackgroundLimit)
            {
                _ = Interlocked.Decrement(ref _inBackground);
                return false;
            }

            _ = Assert(InBackground <= MaxThreads);
            _ = Assert(_number > 0); // a pool thread's own
            var (start, ran) = (Counting.On ? Stopwatch.GetTimestamp() : 0, false);
            try
            {
                ran = job();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                (LastError, BackgroundOff) = (e, true);
            }
            finally
            {
                if (start != 0 && ran) Busy.Add(BackgroundCounter, Stopwatch.GetTimestamp() - start);
                _ = Assert(Interlocked.Decrement(ref _inBackground) >= 0);
            }

            return ran;
        }
    }
}
