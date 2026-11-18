using System.Diagnostics;
using System.Runtime;

namespace Komet.Rendering;

// Only consider collecting during a measured FPS-cap wait. A VSync wait is spent in a different part of the
// frame and can disappear when a frame misses refresh. The runtime decides whether an early Gen0 is worthwhile.
internal static class GcIdle
{
    private const double MarginMs = 3, MinPauseMs = 8, MinFill = 0.9;
    private const int MinBudget = 1 << 20;
    private const long CooldownMs = 1000, QuietAfterGen2Ms = 5000;

    private static readonly Action<int, GCCollectionMode, bool, bool> Collect = GC.Collect;

    private static long _base, _filled, _nextAttempt, _gen2At;
    private static TimeSpan _totalPauses;
    private static double _pauseMs, _budget;
    private static int _gen0, _gen1, _gen2;
    private static bool _tracking;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Successful idle collection requests, counted by the HUD.
    public static long Moved { get; private set; }

    public static void Install() => (Installed, _tracking) = (true, false);

    public static void Stop() => (Installed, _tracking) = (false, false);

    internal static void FrameStart(double cappedLeftMs)
    {
        if (!Installed || !Enabled || !double.IsFinite(cappedLeftMs))
        {
            _tracking = false;
            return;
        }

        var now = Environment.TickCount64;
        var start = Stopwatch.GetTimestamp();
        Track(now, false);
        if (now < _nextAttempt || now - _gen2At < QuietAfterGen2Ms ||
            !Fits(cappedLeftMs - Stopwatch.GetElapsedTime(start).TotalMilliseconds, _pauseMs, _filled, _budget))
        {
            return;
        }

        if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) return;

        _nextAttempt = now + CooldownMs;
        var gen0 = _gen0;
        Collect(0, GCCollectionMode.Optimized, true, false);
        Track(Environment.TickCount64, true);
        if (_gen0 != gen0) Moved++;
    }

    internal static bool Fits(double leftMs, double pauseMs, long filled, double budget) =>
        pauseMs > 0 && leftMs >= Math.Max(MinPauseMs, pauseMs) + MarginMs &&
        budget >= MinBudget && filled >= MinFill * budget;

    private static void Track(long now, bool requested)
    {
        var (gen0, gen1, gen2, total) =
            (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalAllocatedBytes());
        if (!_tracking)
        {
            (_tracking, _base, _filled, _nextAttempt, _gen2At, _pauseMs, _budget) = (true, -1, 0, now, now, 0, 0);
            _totalPauses = GC.GetTotalPauseDuration();
        }
        else if (gen0 != _gen0)
        {
            var pauses = GC.GetTotalPauseDuration();
            var duration = (pauses - _totalPauses).TotalMilliseconds;
            if (gen2 != _gen2) _gen2At = now;

            _pauseMs = Math.Max(duration, _pauseMs * 0.9);
            // The first observed interval may have started partway through a generation. Start learning after it.
            if (!requested && gen1 == _gen1 && gen0 == _gen0 + 1 && _filled >= MinBudget)
            {
                _budget = _budget <= 0 ? _filled : 0.8 * _budget + 0.2 * _filled; // 0: nothing learned yet
            }

            _totalPauses = pauses;
            _base = total;
        }

        (_gen0, _gen1, _gen2, _filled) = (gen0, gen1, gen2, _base >= 0 ? Math.Max(0, total - _base) : 0);
    }
}
