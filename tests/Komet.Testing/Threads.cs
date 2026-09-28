namespace Komet.Testing;

// Races for code that must hold up under concurrent callers: every thread's failure comes back to the test thread
public static class Threads
{
    // Runs body(t) on count threads at once; returns each thread's failure: what body answered, or what it threw (null: none)
    public static string?[] Run(int count, System.Func<int, string?> body)
    {
        var failures = new string?[count];
        var threads = Enumerable.Range(0, count).Select(t => new Thread(() =>
        {
            try
            {
                failures[t] = body(t);
            }
            catch (Exception e)
            {
                failures[t] = e.ToString();
            }
        })).ToList();
        threads.ForEach(thread => thread.Start());
        threads.ForEach(thread => thread.Join());
        return failures;
    }
}

// Time passing and a collection happening, the way a frame sees them: busy, not asleep, and the collector triggered by allocation
public static class Busy
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
