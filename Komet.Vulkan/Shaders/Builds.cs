using System.Diagnostics;

namespace Komet.Vulkan;

// Ports and pipelines build off the engine's thread (each up to tens of ms); a draw waits for its own only, or makes it
// itself when no worker began it. At most Workers at once. Gate guards Running and totals, each Build its result.
internal static class Builds
{
    public enum Kind
    {
        Port,
        Pipeline
    }

    public const int LongestSeconds = 30; // a driver taking longer is hung
    private const int Kinds = 2;

    // A quarter of the cores, 2 to 6: shaderc and the driver's compiler keep a worker busy, the engine keeps its threads
    internal static readonly int Workers = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);

    private static readonly TaskScheduler Scheduler =
        new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Workers).ConcurrentScheduler;
    private static readonly Lock Gate = new();
    private static readonly HashSet<Task> Running = [];
    private static readonly KindTimes Tallied =
        new(["ports", "pipelines", "ports by the waiting draw", "pipelines by the waiting draw"]);
    private static long _started;

    // Tests: each build is waited for, so a frame draws as if made in place
    public static bool Waited { get; set; }

    public static long Started
    {
        get
        {
            lock (Gate) return _started;
        }
    }

    // Listed before it starts, so its end never precedes the listing
    public static Task Run(Action build)
    {
        var task = new Task(build);
        lock (Gate)
        {
            _ = Running.Add(task);
            _started++;
        }

        _ = task.ContinueWith(static done =>
        {
            lock (Gate) _ = Running.Remove(done);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        task.Start(Scheduler);
        return task;
    }

    // All builds done (or timeout): the device goes only after them, tests draw after them
    public static bool Wait(TimeSpan timeout)
    {
        Task[] running;
        lock (Gate) running = [.. Running];
        return Task.WaitAll(running, timeout);
    }

    public static int Pending
    {
        get
        {
            lock (Gate) return Running.Count;
        }
    }

    // waiting: made on the engine's thread by the draw awaiting it, tallied apart from the workers' builds
    public static void Count(Kind kind, long ticks, bool waiting)
    {
        var i = (int)kind;
        lock (Gate) Tallied.Add(waiting ? i + Kinds : i, ticks);
    }

    public static string Report()
    {
        lock (Gate) return Tallied.Report("shaders made");
    }
}

// One result, handed over once under _gate. Dropped before taken: freed by whichever comes second (maker or Drop),
// never twice. Dropped before start: nothing made. make reads only captured copies or immutable objects. Made by
// whoever claims it first: a worker, or the engine's thread awaiting it, so an awaited build never waits behind the
// workers' queue.
internal sealed class Build<T> where T : class
{
    private readonly Lock _gate = new();
    private readonly Action<T>? _free;
    private readonly Func<(T? Made, string Why)> _make;
    private readonly Builds.Kind _kind;
    private readonly Task _task;
    private T? _made;
    private string _why = "";
    private bool _dropped, _taken;
    private int _claimed; // 1 once a worker or the awaiting thread began make
    private volatile bool _finished;

    public Build(Builds.Kind kind, Func<(T? Made, string Why)> make, Action<T>? free = null)
    {
        (_free, _make, _kind) = (free, make, kind);
        _task = Builds.Run(Claimed);
        if (Builds.Waited) _task.Wait();
    }

    // Made, failed or dropped: what Take hands over is there
    public bool Done => _finished;

    public long Ticks { get; private set; }

    // Where make ran, for the log: a worker, or the draw that awaited it on the engine's thread
    public string Where { get; private set; } = "off the engine's thread";

    // The worker's run: nothing when the awaiting thread claimed it first
    private void Claimed()
    {
        if (Interlocked.Exchange(ref _claimed, 1) == 0) Made(_kind, _make, false);
    }

    private void Made(Builds.Kind kind, Func<(T? Made, string Why)> make, bool waiting)
    {
        lock (_gate)
            if (_dropped)
            {
                _finished = true;
                return;
            }

        var start = Stopwatch.GetTimestamp();
        T? made = null;
        string why;
        try
        {
            (made, why) = make();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            why = $"{e.GetType().Name}: {e.Message}";
        }

        var ticks = Stopwatch.GetTimestamp() - start;
        Builds.Count(kind, ticks, waiting);
        lock (_gate)
        {
            if (!_dropped)
            {
                (_made, _why, Ticks) = (made, why, ticks);
                if (waiting) Where = "by the waiting draw";
                _finished = true; // last: Done (read without _gate) publishes the result, Ticks and Where with it
                return;
            }

            _finished = true;
        }

        if (made is null) return;
        try
        {
            _free?.Invoke(made);
        }
        catch (Exception e) when (e is not OutOfMemoryException) // a faulted worker would make Builds.Wait throw at device end
        {
            _ = Assert(false); // reported once; what was dropped stays unfreed
        }
    }

    // Once Done: the result, now the caller's; null with why
    public T? Take(out string why)
    {
        lock (_gate)
        {
            _ = Assert(_finished) && Assert(!_taken && !_dropped);
            var made = _made;
            (why, _made, _taken) = (_made is null && _why.Length == 0 ? "nothing made" : _why, null, true);
            return made;
        }
    }

    // Not begun: never begins; made and not taken: freed
    public void Drop()
    {
        T? made;
        lock (_gate)
        {
            (made, _made, _dropped) = (_made, null, true);
        }

        if (made is not null) _free?.Invoke(made);
    }

    // For where a draw cannot be left out
    public bool Wait(TimeSpan timeout) => _task.Wait(timeout) && Done;

    // Made here when no worker began it yet, else waited for as long as a build may take; booked as a stutter of its kind
    public bool Awaited(Hitches.Kind kind)
    {
        if (Done) return true;
        var at = Hitches.Now;
        if (Interlocked.Exchange(ref _claimed, 1) == 0) Made(_kind, _make, true);
        else _ = Wait(TimeSpan.FromSeconds(Builds.LongestSeconds));
        Hitches.Since(kind, at);
        return Done;
    }
}
