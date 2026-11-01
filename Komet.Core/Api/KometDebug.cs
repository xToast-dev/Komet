using System.Collections.Concurrent;
using System.Diagnostics;

namespace Komet.Api;

/// <summary>
/// Lets another mod's code show up in Komet's <c>/komet debug</c> protocol. Call only when
/// <c>api.ModLoader.IsModEnabled("komet")</c>. Timings go when the world closes.
/// </summary>
/// <example>
/// <code language="csharp">
/// using (KometDebug.Measure("mymod:grade"))
/// {
///     RenderGrade();
/// }
/// </code>
/// </example>
public static class KometDebug
{
    /// <summary>How many timing names a capture keeps.</summary>
    public const int MaxTimings = 1024;
    private static readonly ConcurrentDictionary<string, Timing> Timings = new(StringComparer.Ordinal);

    /// <summary>A capture runs. Work done only for the protocol can test this first.</summary>
    public static bool Capturing { get; internal set; }

    /// <summary>Times a stretch of code on any thread, booked when the result is disposed. Outside a capture it costs a field read.</summary>
    /// <param name="name"><c>modid:what</c>, at most 128 characters.</param>
    /// <returns>The timing; dispose it at the stretch's end (<c>using</c>).</returns>
    public static KometTiming Measure(string name) =>
        Capturing && !string.IsNullOrEmpty(name) && Assert(name.Length <= 128) ? new KometTiming(name, Stopwatch.GetTimestamp()) : default;

    internal static void Book(string name, long ticks)
    {
        if (!Assert(ticks >= 0)) return;
        if (!Timings.TryGetValue(name, out var timing))
        {
            if (Timings.Count >= MaxTimings) return; // Count takes every lock: asked only for a name not seen yet
            timing = Timings.GetOrAdd(name, static _ => new Timing());
        }

        _ = Interlocked.Add(ref timing.Ticks, ticks);
        _ = Interlocked.Increment(ref timing.Calls);
    }

    // Total ms and calls since the capture began, dearest first
    internal static (string Name, double Ms, long Calls)[] TakeTimings()
    {
        var taken = Timings.Select(static t => (t.Key, Ms(Volatile.Read(ref t.Value.Ticks)), Volatile.Read(ref t.Value.Calls)))
            .OrderByDescending(static t => t.Item2).ToArray();
        Timings.Clear();
        return taken;
    }

    private static double Ms(long ticks) => ticks > 0 ? ticks * 1000.0 / Stopwatch.Frequency : 0;

    internal static void Forget()
    {
        Timings.Clear();
        Capturing = false;
    }

    private sealed class Timing
    {
        public long Ticks, Calls;
    }
}

/// <summary>A running timing from <see cref="KometDebug.Measure"/>.</summary>
public readonly record struct KometTiming : IDisposable
{
    private readonly string? _name;
    private readonly long _start;

    internal KometTiming(string name, long start) => (_name, _start) = (name, start);

    /// <summary>Books the time since <see cref="KometDebug.Measure"/> under its name.</summary>
    public void Dispose()
    {
        if (_name is null) return;
        KometDebug.Book(_name, Stopwatch.GetTimestamp() - _start);
    }
}
