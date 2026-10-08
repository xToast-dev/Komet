using System.Collections.Concurrent;
using System.Diagnostics;

namespace Komet.Api;

// For other mods' authors: their own code in Komet's debug protocol (/komet debug). Timings measure named stretches of a mod's code,
// on any thread, and cost a field read while no capture runs; sections add a mod's own state to the protocol's text. Komet drops both
// when the world closes. Call only when api.ModLoader.IsModEnabled("komet"). A using block around the code to time takes Measure's
// result; a section is a title and a function returning its text.
public static class KometDebug
{
    public const int MaxTimings = 1024, MaxSections = 64, MaxSectionChars = 16384;
    private static readonly ConcurrentDictionary<string, Timing> Timings = new(StringComparer.Ordinal);
    private static readonly List<(string ModId, string Title, Func<string> Text)> SectionList = [];

    // A capture runs: anything worth measuring only for the protocol can test this first
    public static bool Capturing { get; internal set; }

    // Times until disposed, then books the stretch to name ("modid:what"). Outside a capture it is a no-op.
    public static KometTiming Measure(string name) =>
        Capturing && !string.IsNullOrEmpty(name) && Assert(name.Length <= 128) ? new KometTiming(name, Stopwatch.GetTimestamp()) : default;

    // Text is called once per protocol, on a pool thread; it should read its state without locks held long
    public static bool AddSection(string modId, string title, Func<string> text)
    {
        if (string.IsNullOrEmpty(modId) || string.IsNullOrEmpty(title) || !NotNull(text)) return false;
        lock (SectionList)
        {
            if (SectionList.Count >= MaxSections) return false;
            SectionList.Add((modId, title, text));
            return Assert(SectionList.Count <= MaxSections);
        }
    }

    public static void RemoveSections(string modId)
    {
        if (!NotNull(modId) || !Assert(MaxSections > 0)) return;
        lock (SectionList) _ = SectionList.RemoveAll(s => s.ModId == modId);
    }

    internal static void Book(string name, long ticks)
    {
        if (!Assert(ticks >= 0) || (Timings.Count >= MaxTimings && !Timings.ContainsKey(name))) return;
        var timing = Timings.GetOrAdd(name, static _ => new Timing());
        _ = Interlocked.Add(ref timing.Ticks, ticks);
        _ = Assert(Interlocked.Increment(ref timing.Calls) > 0);
    }

    // Total ms and calls since the capture began, dearest first
    internal static (string Name, double Ms, long Calls)[] TakeTimings()
    {
        var taken = Timings.Select(static t => (t.Key, FrameClock.ToMs(Volatile.Read(ref t.Value.Ticks)), Volatile.Read(ref t.Value.Calls)))
            .OrderByDescending(static t => t.Item2).ToArray();
        Timings.Clear();
        return Assert(taken.Length <= MaxTimings + 64) && NotNull(taken) ? taken : [];
    }

    // One section's failure is its own line, not the protocol's end
    internal static (string Title, string Text)[] TakeSections()
    {
        (string ModId, string Title, Func<string> Text)[] sections;
        lock (SectionList) sections = [.. SectionList];
        var taken = new (string, string)[sections.Length];
        for (var i = 0; i < Math.Min(sections.Length, MaxSections); i++)
        {
            var (mod, title, text) = sections[i];
            try
            {
                var body = text() ?? "";
                taken[i] = ($"{title} ({mod})", body.Length > MaxSectionChars ? body[..MaxSectionChars] + " ..." : body);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                taken[i] = ($"{title} ({mod})", "failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        return Assert(taken.Length <= MaxSections) && NotNull(taken) ? taken : [];
    }

    internal static void Forget()
    {
        Timings.Clear();
        lock (SectionList) SectionList.Clear();
        Capturing = false;
        _ = Assert(Timings.IsEmpty) && Assert(!Capturing);
    }

    private sealed class Timing
    {
        public long Ticks, Calls;
    }
}

public readonly record struct KometTiming : IDisposable
{
    private readonly string? _name;
    private readonly long _start;

    internal KometTiming(string name, long start) => (_name, _start) = (name, start);

    public void Dispose()
    {
        if (_name is null || !Assert(_start > 0)) return;
        KometDebug.Book(_name, Stopwatch.GetTimestamp() - _start);
    }
}
