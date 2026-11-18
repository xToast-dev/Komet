using System.Collections.Concurrent;
using System.Diagnostics;
using Komet.Api;

namespace Komet.Core;

// Timings of other mods' code (KapiDebug.Measure) and of their render steps in the .komet debug protocol; they go when
// the world closes
internal static class DebugTimings
{
    // How many timing names a capture keeps
    private const int MaxTimings = 1024;

    private static readonly ConcurrentDictionary<string, Timing> Timings = new(StringComparer.Ordinal);

    // A capture and its timings belong to the world they were taken in
    static DebugTimings() => WorldEnd.On(nameof(DebugTimings), Forget);

    // A capture runs: work done only for the protocol can test this first
    public static bool Capturing { get; internal set; }

    // Times a stretch of code on any thread, booked when the result is disposed; outside a capture a field read
    public static KapiTiming Measure(string name) =>
        Capturing && !string.IsNullOrEmpty(name) && Assert(name.Length <= KapiDebug.MaxName)
            ? new KapiTiming(name, Stopwatch.GetTimestamp())
            : default;

    internal static void Book(string name, long ticks)
    {
        if (!Assert(ticks >= 0)) return;

        if (!Timings.TryGetValue(name, out var timing))
        {
            if (Timings.Count >= MaxTimings)
            {
                return; // Count takes every lock: asked only for a name not seen yet
            }

            timing = Timings.GetOrAdd(name, static _ => new Timing());
        }

        _ = Interlocked.Add(ref timing.Ticks, ticks);
        _ = Interlocked.Increment(ref timing.Calls);
    }

    // Total ms and calls since the capture began, dearest first
    internal static (string Name, double Ms, long Calls)[] TakeTimings()
    {
        var taken = Timings.Select(static t =>
                (t.Key, Ms(Volatile.Read(ref t.Value.Ticks)), Volatile.Read(ref t.Value.Calls)))
            .OrderByDescending(static t => t.Item2).ToArray();
        Timings.Clear();
        return taken;
    }

    private static double Ms(long ticks) => ticks > 0 ? ticks * 1000.0 / Stopwatch.Frequency : 0;

    private static void Forget()
    {
        Timings.Clear();
        Capturing = false;
    }

    private sealed class Timing
    {
        public long Ticks, Calls;
    }
}
