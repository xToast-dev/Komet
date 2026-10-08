using System.Diagnostics;

namespace Komet.Vulkan;

// Shader ports and pipelines are made off the engine's thread: shaderc, the driver's compiler and the disk cache take up to
// tens of ms each, a stutter wherever a draw first needs one. The engine's thread starts a build and asks on later draws
// whether it is done; those draws stay OpenGL's until then. At most Workers builds run at once, on the thread pool, beside
// the engine's own threads. Thread-safety: Running and the totals are guarded by Gate, each build's result by its own lock
// (Build).
internal static class Builds
{
    public enum Kind
    {
        Port,
        Pipeline
    }

    private const int Workers = 2, Kinds = 2, MaxRunning = 1 << 16;
    private static readonly TaskScheduler Scheduler =
        new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, Workers).ConcurrentScheduler;
    private static readonly Lock Gate = new();
    private static readonly HashSet<Task> Running = [];
    private static readonly Tally Tallied = new(["ports", "pipelines"]);

    // Tests: the engine's thread waits for each build it starts, so a frame draws as if it were made in place
    public static bool Waited { get; set; }

    private static long _started;

    // Builds started so far
    public static long Started
    {
        get
        {
            lock (Gate) return _started;
        }
    }

    // Listed before it starts, so its end never comes before it is listed
    public static Task Run(Action build)
    {
        var task = new Task(build);
        lock (Gate)
        {
            _ = NotNull(build) && Assert(Running.Count < MaxRunning);
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

    // Every build started so far is done (or the time ran out): the device goes only after its builds, the tests draw after
    // them
    public static bool Wait(TimeSpan timeout)
    {
        Task[] running;
        lock (Gate) running = [.. Running];
        _ = Assert(running.Length < MaxRunning) && Assert(timeout >= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan);
        return Task.WaitAll(running, timeout);
    }

    public static int Pending
    {
        get
        {
            lock (Gate) return Running.Count;
        }
    }

    public static void Count(Kind kind, long ticks)
    {
        var i = (int)kind;
        if (!Index(i, Kinds) || !Assert(ticks >= 0)) return;
        lock (Gate) Tallied.Add(i, ticks);
    }

    public static string Report()
    {
        lock (Gate) return Tallied.Report("made off the engine's thread");
    }
}

// One build's result, handed over once. Thread-safety: the worker stores what it made under _gate and the engine's thread
// takes it under _gate; a result whose owner dropped it before taking it is freed by whichever comes second - the worker as
// it finishes, or Drop - so never twice and never while the other still holds it. A build dropped before its worker began
// makes nothing (cancelled). What make reads must not change while it runs: it captures copies, or objects nobody writes
// after they were made.
internal sealed class Build<T> where T : class
{
    private readonly Lock _gate = new();
    private readonly Action<T>? _free;
    private readonly Task _task;
    private T? _made;
    private string _why = "";
    private bool _dropped, _taken;

    public Build(Builds.Kind kind, Func<(T? Made, string Why)> make, Action<T>? free = null)
    {
        _ = NotNull(make) && Assert(kind <= Builds.Kind.Pipeline);
        _free = free;
        _task = Builds.Run(() => Made(kind, make));
        if (Builds.Waited) _task.Wait();
    }

    public bool Done => _task.IsCompleted;

    // How long it took on its worker
    public long Ticks { get; private set; }

    private void Made(Builds.Kind kind, Func<(T? Made, string Why)> make)
    {
        lock (_gate)
            if (_dropped)
                return;
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
        Builds.Count(kind, ticks);
        lock (_gate)
            if (!_dropped)
            {
                (_made, _why, Ticks) = (made, why, ticks);
                return;
            }

        if (made is not null) _free?.Invoke(made);
        _ = Assert(ticks >= 0);
    }

    // The engine's thread, once Done: what was made, now the caller's; null with why it was not
    public T? Take(out string why)
    {
        lock (_gate)
        {
            _ = Assert(_task.IsCompleted) && Assert(!_taken && !_dropped);
            var made = _made;
            (why, _made, _taken) = (_made is null && _why.Length == 0 ? "nothing made" : _why, null, true);
            return made;
        }
    }

    // The owner no longer wants it: not begun, it never is; made and not taken, it is freed
    public void Drop()
    {
        T? made;
        lock (_gate)
        {
            (made, _made, _dropped) = (_made, null, true);
        }

        if (made is not null) _free?.Invoke(made);
        _ = Assert(_made is null);
    }

    // The engine's thread waits for it: where leaving its draws to OpenGL is no option
    public bool Wait(TimeSpan timeout) => Assert(timeout > TimeSpan.Zero) && NotNull(_task) && _task.Wait(timeout);
}
