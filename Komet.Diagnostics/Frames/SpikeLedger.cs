namespace Komet.Diagnostics;

// Its dearest marks sit in the ledger's parallel mark array
internal readonly record struct Spike(
    double AtSeconds, double DtMs, string Cause, double GcMs, double OutsideMs, double JitMs, double RunQueueMs);

// What the lows are made of: steady frames over max(MinMs, MeanFactor × running mean), booked to their dominant cause.
internal sealed class SpikeLedger
{
    public const double MinMs = 25, MeanFactor = 2.5;
    public const int Shown = 5, Recent = 8, TopMarks = RenderPassStats.DetailCount, MaxCauses = 256;

    public const string Outside = RenderPassStats.Outside, Gc = "~gc", Jit = "~jit", RunQueue = "~runqueue",
        Unprofiled = "~unprofiled", Other = "~other";

    private const double MeanFrames = 128;
    private readonly Dictionary<string, Cause> _causes = [];
    private readonly (Cause? Item, double Ms)[] _ranked = new (Cause?, double)[Shown];
    private readonly Spike[] _ring = new Spike[Recent];
    private readonly (string? Name, double Ms)[] _ringMarks = new (string?, double)[Recent * TopMarks];
    private readonly int[] _ringCalls = new int[Recent * TopMarks]; // how often the engine marked each in its frame
    private double _meanMs;
    private int _next;

    public double ThresholdMs => Math.Max(MinMs, MeanFactor * _meanMs);
    public int Count { get; private set; } // spikes since the last reset
    public int Stored { get; private set; }

    // Threshold from the mean before this frame; a spike moves the mean at most by the threshold, so a hitch hardly
    // shifts it and a steady 20 fps is not a spike forever.
    public bool Add(in FrameRecord frame, ReadOnlySpan<(string? Name, double Ms)> top, bool steady, double atSeconds)
    {
        var dt = frame.DtMs;
        if (!steady || !Finite(dt) || !Assert(dt >= 0)) return false;
        var (threshold, mean) = (ThresholdMs, _meanMs);
        _meanMs = mean <= 0 ? dt : mean + (Math.Min(dt, threshold) - mean) / MeanFrames; // 0: no mean yet
        if (mean <= 0 || dt <= threshold) return false;
        var cause = CauseOf(frame, top, dt - mean);
        Book(cause, dt);
        Remember(new Spike(atSeconds, dt, cause, frame.GcMs, frame.OutsideMs, frame.JitMs, frame.RunQueueMs), top,
            frame.Root);
        Count++;
        return true;
    }

    // GC, JIT or scheduler wins when it alone covers half the excess over the mean; else the dearest mark or range
    // (Outside competing); without a profile Outside or Unprofiled.
    private static string CauseOf(in FrameRecord frame, ReadOnlySpan<(string? Name, double Ms)> top, double excessMs)
    {
        var half = excessMs / 2;
        if (frame.GcMs > 0 && frame.GcMs >= half) return Gc; // NaN compares false everywhere below
        if (frame.JitMs > 0 && frame.JitMs >= half) return Jit;
        if (frame.RunQueueMs > 0 && frame.RunQueueMs >= half) return RunQueue;
        var outside = frame.OutsideMs > 0 ? frame.OutsideMs : 0;
        if (!top.IsEmpty && top[0].Name is { } mark && top[0].Ms >= outside) return mark;
        return outside > 0 && outside >= half ? Outside : Unprofiled;
    }

    private void Book(string cause, double dt)
    {
        if (!_causes.TryGetValue(cause, out var stats))
        {
            if (_causes.Count >= MaxCauses - 1 && !_causes.TryGetValue(Other, out stats)) cause = Other;
            if (stats is null) _causes[cause] = stats = new Cause { Name = cause };
        }

        stats.Count++;
        stats.SumMs += dt;
        stats.MaxMs = Math.Max(stats.MaxMs, dt);
    }

    private void Remember(Spike spike, ReadOnlySpan<(string? Name, double Ms)> top, ProfileEntryRange? root)
    {
        _ring[_next] = spike;
        var marks = _ringMarks.AsSpan(_next * TopMarks, TopMarks);
        var calls = _ringCalls.AsSpan(_next * TopMarks, TopMarks);
        marks.Clear();
        calls.Clear();
        top[..Math.Min(top.Length, TopMarks)].CopyTo(marks);
        for (var i = 0; i < Math.Min(top.Length, TopMarks); i++) calls[i] = Calls(root, top[i].Name);
        _next = (_next + 1) % Recent;
        Stored = Math.Min(Stored + 1, Recent);
    }

    // FrameProfilerUtil merges one code's marks per frame and counts calls (23.7 ms esr-tesseleateshape may be nine
    // entities)
    internal static int Calls(ProfileEntryRange? root, string? name)
    {
        if (string.IsNullOrEmpty(name) || root?.Marks is null) return 0;
        if (root.Marks.TryGetValue(name, out var entry))
            return NotNull(entry) && Assert(entry.CallCount >= 0) ? entry.CallCount : 0;
        return root.ChildRanges?.GetValueOrDefault(name) is { } range && Assert(range.CallCount >= 0)
            ? range.CallCount
            : 0;
    }

    public void Rank()
    {
        Array.Clear(_ranked);
        using var causes = _causes.GetEnumerator();
        for (var i = 0; i < MaxCauses && causes.MoveNext(); i++)
            TopN.Rank(_ranked, causes.Current.Value, causes.Current.Value.SumMs);
    }

    public (string Name, int Count, double MaxMs, double AverageMs) Ranked(int place)
    {
        if (!Index(place, Shown) || _ranked[place].Item is not { } c)
            return ("", 0, double.NaN, double.NaN);
        return (c.Name, c.Count, c.MaxMs, c.SumMs / c.Count);
    }

    public Spike Latest(int age) =>
        Index(age, Stored) ? _ring[(_next - 1 - age + 2 * Recent) % Recent] : default;

    public (string? Name, double Ms, int Calls) LatestMark(int age, int rank)
    {
        if (!Index(age, Stored) || !Index(rank, TopMarks)) return default;
        var at = (_next - 1 - age + 2 * Recent) % Recent * TopMarks + rank;
        return (_ringMarks[at].Name, _ringMarks[at].Ms, _ringCalls[at]);
    }

    public void Reset()
    {
        _causes.Clear();
        Array.Clear(_ranked);
        Array.Clear(_ring);
        Array.Clear(_ringMarks);
        Array.Clear(_ringCalls);
        (_meanMs, _next, Count, Stored) = (0, 0, 0, 0);
    }

    private sealed class Cause
    {
        public int Count;
        public double SumMs, MaxMs;
        public required string Name { get; init; }
    }
}

// The few largest by milliseconds, in a span sorted by falling ms; the smallest drops out when a larger one comes
internal static class TopN
{
    public const int MaxRank = 8;

    public static void Rank<T>(Span<(T? Item, double Ms)> top, T item, double ms) where T : class
    {
        if (!Assert(ms >= 0) || !NotNull(item)) return;
        // A full top whose last holds as much takes nothing: most of a frame's marks (376 in a trace) leave here, not
        // after Insert's delegate walk
        if (!top.IsEmpty && top[^1].Item is not null && top[^1].Ms >= ms) return;
        Insert<(T? Item, double Ms)>(top, (item, ms), ms, static entry => entry.Item is null ? -1 : entry.Ms);
    }

    // At the first place that is empty (msOf gives a negative) or smaller; nothing when every place holds more
    public static void Insert<T>(Span<T> top, T entry, double ms, System.Func<T, double> msOf)
    {
        if (!Assert(top.Length is > 0 and <= MaxRank) || !Finite(ms)) return;
        var slot = top.Length;
        for (var i = 0; i < Math.Min(top.Length, MaxRank); i++)
            if (msOf(top[i]) < ms)
            {
                slot = i;
                break;
            }

        if (slot == top.Length) return;
        top[slot..^1].CopyTo(top[(slot + 1)..]);
        top[slot] = entry;
    }
}
