using System.Diagnostics;
using System.Runtime;
using HarmonyLib;
using Microsoft.Win32.SafeHandles;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Diagnostics;

// One frame, start to start, every part booked to that same frame. Dt, GC pause, main-thread JIT time and run-queue wait are deltas
// between two frame starts. Root is the profile this frame's own FrameProfiler.End() produced. Outside is the gap from this frame's
// end to the next one's start: OpenTK's input and window events, where the engine sets no mark. NaN = not measured.
internal readonly record struct FrameRecord(
    long Index, double DtMs, double GcMs, double OutsideMs, double JitMs, double RunQueueMs, ProfileEntryRange? Root);

// Collections per generation (a gen2 collection counts in all three, as GC.CollectionCount does) and bytes allocated by every
// thread and by the main thread alone, between the same two frame starts as the record's dt
internal readonly record struct FrameCounters(int Gen0, int Gen1, int Gen2, long Allocated, long MainAllocated)
{
    public FrameCounters Since(in FrameCounters before)
    {
        return Assert(Gen0 >= before.Gen0 && Gen1 >= before.Gen1 && Gen2 >= before.Gen2) &&
               Assert(Allocated >= before.Allocated && MainAllocated >= before.MainAllocated)
            ? new FrameCounters(Gen0 - before.Gen0, Gen1 - before.Gen1, Gen2 - before.Gen2,
                Allocated - before.Allocated, MainAllocated - before.MainAllocated)
            : default;
    }
}

// Takes every frame FrameClock closes, on the main thread, inside window_RenderFrame's prefix: nothing it does may throw
internal interface IFrameSink
{
    void Frame(in FrameRecord frame, in FrameCounters counters);
}

// Prefix and postfix on ClientPlatformWindows.window_RenderFrame, the RenderFrame handler that brackets a frame: FrameProfiler.Begin,
// the FPS limiter sleep, dt from frameStopWatch, ScreenManager.OnNewFrame, SwapBuffers, FrameProfiler.End. Read in the Ortho stage
// instead, a collection in the ~85 % of a frame before Ortho lands on the previous frame's dt, and PrevRootEntry is what
// GuiScreenRunningGame.OnMouseDown/OnMouseUp leave between frames with their own Begin("mousedown")/End(): a click's profile, all of it
// under "end". The postfix takes the root before any input runs.
internal static class FrameClock
{
    private const int MaxSchedBytes = 128;

    // = /proc/self/task/<tid>/schedstat of the thread that opens it
    private const string SchedStat = "/proc/thread-self/schedstat";

    private static readonly byte[] Sched = new byte[MaxSchedBytes];
    internal static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
    private static long _start, _end;
    private static TimeSpan _pause, _jit;
    private static double _runDelayNs;
    private static ProfileEntryRange? _root, _seen;
    private static SafeFileHandle? _schedstat;
    private static FrameCounters _counters;
    private static bool _schedTried, _counted;

    // The HUD is looking; with no sink either, both patches return at once
    public static bool Stats { get; set; }

    // The benchmark's recorder; only while one listens are the counters read
    public static IFrameSink? Sink { get; set; }

    public static FrameRecord Last { get; private set; }

    // Records so far; a reader compares it to see whether Last is new
    public static long Completed { get; private set; }

    public static void Install(Harmony harmony)
    {
        (_start, _end, _root, _seen, Last, Completed, Sink, _counted) = (0, 0, null, null, default, 0, null, false);
        var frame = AccessTools.Method(typeof(ClientPlatformWindows), "window_RenderFrame");
        if (!NotNull(harmony) || !NotNull(frame) || !Assert(frame.GetParameters().Length == 1)) return;
        _ = NotNull(harmony.Patch(frame, new HarmonyMethod(Begin), new HarmonyMethod(End)));
    }

    // Prefix: the previous frame ends where this one starts, so its record is complete now. An exception that skipped the previous
    // postfix leaves _end behind _start; the frame still has its dt, only the split is unknown. A sink gets a frame only when the
    // previous start read the counters too, so the frame it joined in is skipped.
    internal static void Begin()
    {
        var sink = Sink;
        if (!Stats && sink is null)
        {
            (_start, _counted) = (0, false);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var (pause, jit, delay) = (GC.GetTotalPauseDuration(), JitInfo.GetCompilationTime(true), RunDelayNs());
        var counters = sink is null
            ? default
            : new FrameCounters(GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
                GC.GetTotalAllocatedBytes(), GC.GetAllocatedBytesForCurrentThread());
        // All four are cumulative: a step back is a broken reading, and the frame is dropped rather than booked a negative share.
        // End() has checked the root's length; delay may be NaN (no schedstat), which passes and stays NaN.
        if (_start != 0 && Assert(now >= _start) && Assert(pause >= _pause) && Assert(jit >= _jit) &&
            Assert(!(delay < _runDelayNs)))
        {
            Last = new FrameRecord(Completed, (now - _start) * TickMs, (pause - _pause).TotalMilliseconds,
                _end >= _start ? (now - _end) * TickMs : double.NaN, (jit - _jit).TotalMilliseconds,
                (delay - _runDelayNs) / 1e6, _root);
            Completed++;
            if (sink is not null && _counted) sink.Frame(Last, counters.Since(_counters));
        }

        (_start, _end, _root, _pause, _jit, _runDelayNs) = (now, 0, null, pause, jit, delay);
        (_counters, _counted) = (counters, sink is not null);
    }

    // Postfix: End() has just set PrevRootEntry to this frame's root. A root seen before is stale: the profiler was off this frame.
    internal static void End()
    {
        if (_start == 0) return;
        _end = Stopwatch.GetTimestamp();
        if (!Assert(_end >= _start)) return;
        var profiler = ScreenManager.FrameProfiler;
        var root = profiler?.PrevRootEntry;
        _root = profiler is { Enabled: true } && root != null && !ReferenceEquals(root, _seen) ? root : null;
        _seen = root;
        // Leave() has closed it, so it has a length
        if (_root != null && !Assert(_root.ElapsedTicks >= 0)) _root = null;
    }

    // Stopwatch ticks in milliseconds, for the profiler's lengths and tick totals
    internal static double ToMs(long ticks)
    {
        return Assert(ticks >= 0) && Assert(Stopwatch.Frequency > 0) ? ticks * 1000.0 / Stopwatch.Frequency : 0;
    }

    // Time the main thread was runnable but waited for a core (schedstat field 2, ns). One pread through a handle opened once, on
    // the render thread, ~1.2 µs; Linux only.
    private static double RunDelayNs()
    {
        if (!_schedTried) OpenSchedStat();
        if (_schedstat is null) return double.NaN;
        int read;
        try
        {
            read = RandomAccess.Read(_schedstat, Sched, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // a throw here would leave window_RenderFrame's prefix and end the game; the column goes blank instead
            _schedstat.Dispose();
            _schedstat = null;
            return double.NaN;
        }

        long value = 0, delay = -1;
        var field = 0;
        for (var i = 0; i < Math.Min(read, MaxSchedBytes); i++)
        {
            var c = Sched[i];
            if (c is >= (byte)'0' and <= (byte)'9')
            {
                value = value * 10 + (c - '0');
            }
            else if (field++ == 1)
            {
                delay = value;
                break;
            }
            else
            {
                value = 0;
            }
        }

        return Assert(read > 0) && Assert(delay >= 0) ? delay : double.NaN;
    }

    private static void OpenSchedStat()
    {
        _schedTried = true;
        if (!OperatingSystem.IsLinux() || !Assert(_schedstat is null)) return;
        try
        {
            _schedstat = File.OpenHandle(SchedStat);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _schedstat = null; // no run-queue column, everything else still works
        }
    }
}
