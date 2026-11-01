using System.Diagnostics;
using HarmonyLib;
using Komet.Diagnostics;

namespace Komet.Rendering;

// A gen0 collection pauses the game ~7 ms (card marking over a multi-GB heap, not the garbage) wherever it falls, so it
// is a hitch whenever it lands mid-frame. Where a frame has time to spare it is moved there: at the frame's start, if
// the time left this interval exceeds the recent pauses plus a margin and gen0 is at least half full, the collection
// runs now. Time left: the fps cap's remaining wait (paced or not), else, with VSync, the display interval (the shortest
// recent frame) less the recent work (frame time less the time in SwapBuffers). No time to spare (uncapped, or frames
// slower than the display): nothing changes. Not within 5 s of a gen2, which a forced gen0 would wait for.
internal static class GcIdle
{
    private const double MarginMs = 1.0, FirstPauseMs = 8, MinFill = 0.5, Smoothing = 0.15, FirstBudget = 8 << 20;
    private const int Recent = 64;
    private const long QuietAfterGen2Ms = 5000;

    // GC.Collect through a delegate: the analyzers forbid the call itself
    private static readonly Action<int, GCCollectionMode, bool, bool> Collect = GC.Collect;
    private static readonly float[] Frames = new float[Recent];

    private static double _pauseMs = FirstPauseMs, _budget = FirstBudget, _workMs = double.NaN, _swapMs, _movedMs;
    private static long _frameStart, _swapStart, _base, _lastFilled, _gen2At = long.MinValue / 2;
    private static int _gen0 = -1, _gen2 = -1, _frame;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    // Collections moved into spare time
    public static long Moved { get; private set; }

    public static void Install(Harmony harmony)
    {
        Patched = false;
        (_pauseMs, _budget, _workMs, _swapMs, _movedMs, _frameStart, _swapStart, _gen0, _gen2, _frame) =
            (FirstPauseMs, FirstBudget, double.NaN, 0, 0, 0, 0, -1, -1, 0);
        Array.Clear(Frames);
        var swap = AccessTools.Method(typeof(OpenTK.Windowing.Desktop.GameWindow), "SwapBuffers");
        if (!NotNull(harmony) || !NotNull(swap)) return;
        Patched = harmony.Patch(swap, new HarmonyMethod(AccessTools.Method(typeof(GcIdle), nameof(Swapping))) { priority = Priority.First },
            new HarmonyMethod(AccessTools.Method(typeof(GcIdle), nameof(Swapped))) { priority = Priority.Last }) is not null;
    }

    private static void Swapping() => _swapStart = Stopwatch.GetTimestamp();

    private static void Swapped()
    {
        if (_swapStart == 0) return;
        var ms = Ms(Stopwatch.GetTimestamp() - _swapStart);
        _swapMs = Finite(ms) && ms >= 0 ? ms : 0;
    }

    // FramePacer, at the frame's start, paced or not: cappedLeftMs is the fps cap's remaining wait, NaN without a cap
    internal static void FrameStart(double cappedLeftMs)
    {
        var now = Stopwatch.GetTimestamp();
        if (_frameStart != 0) Measured(Ms(now - _frameStart), _movedMs);
        (_frameStart, _movedMs) = (now, 0);
        Track(now, false);
        if (!Enabled || !Patched) return;
        var left = double.IsFinite(cappedLeftMs) ? cappedLeftMs : VsyncLeft();
        if (double.IsNaN(left) || left < _pauseMs + MarginMs || _lastFilled < MinFill * _budget || now - _gen2At < Ticks(QuietAfterGen2Ms)) return;
        Collect(0, GCCollectionMode.Forced, true, false);
        var pause = Ms(Stopwatch.GetTimestamp() - now);
        if (Finite(pause)) (_pauseMs, _movedMs) = (_pauseMs + (pause - _pauseMs) * Smoothing, pause);
        Moved++;
        Track(Stopwatch.GetTimestamp(), true);
    }

    // The frame that ended: its whole time (with VSync still the display's interval, so the shortest-frame estimate
    // holds), and its work: the time not spent waiting in SwapBuffers, less a moved collection's pause, which came out
    // of that wait
    private static void Measured(double dt, double movedMs)
    {
        if (!Finite(dt)) return;
        Frames[_frame++ % Recent] = (float)dt;
        var work = Math.Max(0, dt - movedMs - _swapMs);
        _workMs = double.IsNaN(_workMs) ? work : _workMs + (work - _workMs) * Smoothing;
    }

    // With VSync the frames that waited run at the display's interval: the shortest recent frame, less the recent work
    private static double VsyncLeft()
    {
        if (FrameSettings.VsyncMode != 1 || _frame < Recent || double.IsNaN(_workMs)) return double.NaN;
        var shortest = float.MaxValue;
        foreach (var dt in Frames.Bounded(Recent)) shortest = Math.Min(shortest, dt);
        return Assert(shortest > 0) ? shortest - _workMs : double.NaN;
    }

    // Gen0's fill since its last collection; a natural one (not moved) teaches the budget: the fill seen the frame before
    private static void Track(long now, bool moved)
    {
        var (gen0, gen2, total) = (GC.CollectionCount(0), GC.CollectionCount(2), GC.GetTotalAllocatedBytes());
        if (gen2 != _gen2 && _gen2 >= 0) _gen2At = now;
        if (gen0 != _gen0)
        {
            if (!moved && _gen0 >= 0 && _lastFilled > 0) _budget += (Math.Max(_lastFilled, 1 << 20) - _budget) * Smoothing;
            _base = total;
        }

        (_gen0, _gen2, _lastFilled) = (gen0, gen2, Math.Max(0, total - _base));
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static long Ticks(long ms) => ms * Stopwatch.Frequency / 1000;
}
