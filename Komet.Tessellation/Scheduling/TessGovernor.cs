using System.Diagnostics;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Tessellation;

// Pool threads tessellating at once, set every Window from the main thread's wait for a core (schedstat): any wait is
// render time lost to workers. More workers than cores make frames wait during a load; a fixed few leave minutes of
// backlog. GcAware: the share of the window the GC paused the game as a second signal. A join at a long view distance
// allocates fast, nearly all on the tessellation workers, and pauses with no core wait at all; fewer workers allocate
// less a second, so the world fills more slowly and its frames stay smoother.
internal static class TessGovernor
{
    private const long WindowMs = 250;
    private const int GrowAt = 64;

    // Main thread core wait, ms/frame: below Calm workers may grow, from Pressed shrink, from Severe halve; times Scale
    internal const double Calm = 0.3, Pressed = 1.0, Severe = 4.0;

    // Share of the window in GC pauses: below GcCalm workers may grow, from GcPressed shrink; times Scale
    internal const double GcCalm = 0.03, GcPressed = 0.06;

    private static TimeSpan _lastPause = TimeSpan.MinValue;
    private static long _windowStart, _frames, _lastDelayNs = -1;
    private static int _limit = 1, _ceiling = 2;
    private static double _scale = 1;


    // The ceiling TessWorkers allows at the moment, set on the engine's tessellation thread
    public static int Ceiling
    {
        get => Volatile.Read(ref _ceiling);
        set => Volatile.Write(ref _ceiling, Math.Clamp(value, 0, WorkerPool.MaxThreads));
    }

    // TessWorkers.Priority as a factor on the thresholds (divisor on GrowAt): 0.5 at 0, 1 at default 25, 2.5 at 100
    public static double Scale
    {
        get => Volatile.Read(ref _scale);
        set => Volatile.Write(ref _scale, Finite(value) && Assert(value > 0) ? value : 1);
    }

    private static bool Measured { get; set; }

    // Pool threads that may tessellate now; the ceiling while nothing was measured (no schedstat, or no frame yet)
    public static int Limit(int ceiling) =>
        Measured ? Math.Min(Volatile.Read(ref _limit), ceiling) : ceiling;

    // Main thread, each frame start (FrameClock's prefix): count frames; once per Window read schedstat
    public static void Frame()
    {
        _frames++;
        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0) _windowStart = now;
        if ((now - _windowStart) * 1000 < WindowMs * Stopwatch.Frequency) return;
        var delay = SchedStat.MainWaitNs();
        var (frames, elapsed) = (_frames, now - _windowStart);
        (_windowStart, _frames) = (now, 0);
        var gcShare = GcShare(elapsed);
        double waitMs = 0; // no schedstat (Windows, macOS): the GC's pauses alone steer
        if (delay >= 0)
        {
            var previous = _lastDelayNs;
            _lastDelayNs = delay;
            if (previous < 0 || !Assert(delay >= previous)) return;
            waitMs = (delay - previous) / 1e6 / frames;
        }

        var ceiling = Ceiling;
        Volatile.Write(ref _limit, Decide(Volatile.Read(ref _limit), ceiling, waitMs,
            (TessSchedule.Backlog, WorkerPool.InBackground), Scale, gcShare));
        Measured = true;
    }

    // The GC's pauses since the last window as a share of its length; 0 on the first window
    private static double GcShare(long elapsedTicks)
    {
        var pause = GC.GetTotalPauseDuration();
        var last = _lastPause;
        _lastPause = pause;
        if (last == TimeSpan.MinValue || elapsedTicks <= 0 || !Assert(pause >= last)) return 0;
        var share = (pause - last).TotalSeconds * Stopwatch.Frequency / elapsedTicks;
        return Math.Clamp(share, 0, 1);
    }

    internal static int Decide(int limit, int ceiling, double waitMs, (int Backlog, int Busy) work, double scale = 1,
        double gcShare = 0)
    {
        if (!Assert(ceiling >= 0) || !Assert(work.Busy >= 0) || !Assert(scale > 0) || ceiling == 0) return 0;
        limit = Math.Clamp(limit, 1, ceiling);
        if (!Finite(waitMs)) return limit;
        if (waitMs >= Severe * scale) return Math.Max(1, limit / 2);
        if (waitMs >= Pressed * scale || gcShare >= GcPressed * scale) return Math.Max(1, limit - 1);
        if (work.Backlog == 0) return Math.Max(1, limit - 1);
        var grow = waitMs < Calm * scale && gcShare < GcCalm * scale && work.Backlog > GrowAt / scale && work.Busy >= limit;
        return grow ? Math.Min(ceiling, limit + 1) : limit;
    }

    public static void Reset()
    {
        (_windowStart, _frames, _lastDelayNs, Measured, _lastPause) = (0, 0, -1, false, TimeSpan.MinValue);
        Volatile.Write(ref _limit, 1);
    }
}
