using System.Diagnostics;

namespace Komet.Test.Rigs;

// Time passing and a collection happening, the way a frame sees them: busy, not asleep, and the collector triggered by allocation
internal static class Busy
{
    public static void Spin(double millis)
    {
        var spin = Stopwatch.StartNew();
        while (spin.Elapsed.TotalMilliseconds < millis) Thread.SpinWait(64);
    }

    // Allocates until gen0 fills up and collects; returns the pause that collection added
    public static TimeSpan Collect()
    {
        var (before, count) = (GC.GetTotalPauseDuration(), GC.CollectionCount(0));
        var keep = new object[64];
        for (var i = 0; i < 100_000_000 && GC.CollectionCount(0) == count; i++) keep[i & 63] = new byte[1024];
        GC.KeepAlive(keep);
        return GC.GetTotalPauseDuration() - before;
    }
}
