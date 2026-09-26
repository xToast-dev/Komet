using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet.Diagnostics;

// Maps the engine profiler's marks to render passes. Queries address a place in the list sorted by cost.
internal sealed class RenderPassStats
{
    // MaxMarks: marks read per frame and range, and names kept per pass and interval
    public const int DetailCount = 3, MaxMarks = 256;

    // The time between frames, ranked in with the marks; '~' never starts an engine mark
    public const string Outside = "~outside";

    private const int MaxRanges = 64;
    private const string StagePrefix = "beginrenderstage-";

    private static readonly string[] Keys =
    [
        "gametick", "before", "shadows", "opaque", "transparent", "postprocess", "gui", "done", "mainthread", "swap",
        "sleep", "outside", "other"
    ];

    private readonly Comparison<int> _byCost;

    private readonly (string? Name, double Ms)[] _frameTop = new (string?, double)[DetailCount],
        _worstTop = new (string?, double)[DetailCount];

    private readonly Dictionary<string, double>[]
        _marks = Array.ConvertAll(Keys, _ => new Dictionary<string, double>());

    private readonly int[] _order = new int[Keys.Length];

    private readonly double[] _sumMs = new double[Keys.Length],
        _worstMs = new double[Keys.Length],
        _frameMs = new double[Keys.Length];

    private readonly (string? Name, double Ms)[][] _topMarks =
        Array.ConvertAll(Keys, _ => new (string?, double)[DetailCount]);

    private int _frames;
    private bool _hasFrame;
    private double _sumTotalMs;

    public RenderPassStats()
    {
        _byCost = (a, b) => _sumMs[b].CompareTo(_sumMs[a]);
        if (!Assert(Keys.Length == Enum.GetValues<Pass>().Length)) return;
        for (var i = 0; i < Keys.Length; i++) _order[i] = i;
    }

    public static int Count => Keys.Length;

    public double AverageTotalMs => _frames == 0 ? 0 : _sumTotalMs / _frames;

    // The worst steady frame with its dearest marks (_worstTop); _worstMs is each pass's own worst, which can come from any frame
    public double WorstFrameMs { get; private set; }

    // The dearest marks and ranges of the frame added last, the time between frames ranked in as Outside; empty when it had no profile
    public ReadOnlySpan<(string? Name, double Ms)> FrameTop => _hasFrame ? _frameTop : [];

    public string Key(int place)
    {
        return Index(place, Count) ? Keys[_order[place]] : "";
    }

    public double AverageMs(int place)
    {
        return Index(place, Count) && _frames > 0 ? _sumMs[_order[place]] / _frames : 0;
    }

    public double Percent(int place)
    {
        return Index(place, Count) && AverageTotalMs > 0 ? AverageMs(place) / AverageTotalMs * 100 : 0;
    }

    public double WorstPercent(int place)
    {
        return Index(place, Count) && AverageTotalMs > 0 ? _worstMs[_order[place]] / AverageTotalMs * 100 : 0;
    }

    public string DetailName(int place, int rank)
    {
        return Index(place, Count) && Index(rank, DetailCount) ? _topMarks[_order[place]][rank].Name ?? "–" : "";
    }

    public double DetailMs(int place, int rank)
    {
        return Index(place, Count) && Index(rank, DetailCount) && _frames > 0 &&
               _topMarks[_order[place]][rank].Name is { } mark
            ? _marks[_order[place]].GetValueOrDefault(mark) / _frames
            : double.NaN;
    }

    public string WorstMarkName(int rank)
    {
        return Index(rank, DetailCount) ? _worstTop[rank].Name ?? "" : "";
    }

    public double WorstMarkMs(int rank)
    {
        return Index(rank, DetailCount) && _worstTop[rank].Name != null ? _worstTop[rank].Ms : double.NaN;
    }

    // The frame's own root, and its dt from start to start: what the profiler did not see between this frame's End() and the next
    // Begin() is its own pass, and what is left of dt goes to Other
    public void AddFrame(in FrameRecord frame, bool steady)
    {
        if (!Assert(frame.Index >= 0) || !Assert(double.IsNaN(frame.DtMs) || frame.DtMs >= 0)) return;
        AddFrame(frame.Root, steady, frame.DtMs, frame.OutsideMs);
    }

    // steady keeps load stalls and the pause gap out: one of those would own the worst frame for the rest of the session.
    // Without FrameClock dt and outside are NaN and the profile's Begin..End is the whole frame.
    // Past MaxMarks the marks are not read one by one; their time is still in the total and so lands in Other.
    public void AddFrame(ProfileEntryRange? frame, bool steady, double dtMs = double.NaN, double outsideMs = double.NaN)
    {
        _hasFrame = false;
        if (frame?.Marks == null) return;
        var profiled = ToMs(frame.ElapsedTicks);
        var outside = outsideMs > 0 ? outsideMs : 0; // NaN: not measured
        var total = dtMs > 0 ? Math.Max(dtMs, profiled + outside) : profiled + outside;
        if (!Assert(profiled >= 0) || !Finite(total)) return;
        _frames++;
        _sumTotalMs += total;
        Array.Clear(_frameMs);
        Array.Clear(_frameTop);
        _frameMs[(int)Pass.Outside] = outside;
        if (outside > 0) TopN.Rank(_frameTop, Outside, outside);
        var assigned = outside + AddMarks(frame.Marks) + AddRanges(frame.ChildRanges);
        _frameMs[(int)Pass.Other] += Math.Max(0, total - assigned);
        for (var i = 0; i < Keys.Length; i++)
        {
            _sumMs[i] += _frameMs[i];
            _worstMs[i] = Math.Max(_worstMs[i], _frameMs[i]);
        }

        _hasFrame = true;
        if (!steady || total <= WorstFrameMs) return;
        WorstFrameMs = total;
        _frameTop.CopyTo(_worstTop, 0);
    }

    // The root's own marks, in the order the frame first set them (a struct enumerator: no allocation per frame); returns their sum
    private double AddMarks(Dictionary<string, ProfileEntry> marks)
    {
        var (current, sum) = (Pass.Other, 0.0);
        using var entries = marks.GetEnumerator();
        for (var i = 0; i < MaxMarks && entries.MoveNext(); i++)
        {
            var (code, entry) = entries.Current;
            if (!NotNull(entry)) continue;
            var ms = ToMs(entry.ElapsedTicks);
            var pass = Classify(code, ref current);
            _frameMs[(int)pass] += ms;
            AddMark(pass, code, ms);
            TopN.Rank(_frameTop, code, ms);
            sum += ms;
        }

        return sum;
    }

    // FrameProfilerUtil.Leave() moves the root's LastMark past an Enter/Leave range, so its time is in no root mark. rendTransparent
    // brackets the OIT stage (ClientMain.MainRenderLoop), behaviors every client entity's behaviour ticks (Entity.OnGameTick)
    // inside TriggerGameTick. A range ranks in whole, under its own name; returns the ranges' sum.
    private double AddRanges(Dictionary<string, ProfileEntryRange>? ranges)
    {
        if (ranges == null) return 0;
        var sum = 0.0;
        using var entries = ranges.GetEnumerator();
        for (var i = 0; i < MaxRanges && entries.MoveNext(); i++)
        {
            var (code, range) = entries.Current;
            if (!NotNull(range)) continue;
            var pass = code switch
            {
                "rendTransparent" => Pass.Transparent,
                "behaviors" => Pass.GameTick,
                _ => Pass.Other
            };
            var ms = ToMs(range.ElapsedTicks);
            _frameMs[(int)pass] += ms;
            TopN.Rank(_frameTop, code, ms);
            sum += ms;
            if (range.Marks == null) continue;
            using var marks = range.Marks.GetEnumerator();
            for (var j = 0; j < MaxMarks && marks.MoveNext(); j++)
                AddMark(pass, marks.Current.Key, ToMs(marks.Current.Value.ElapsedTicks));
        }

        return sum;
    }

    // A name past MaxMarks in one pass and interval is not kept; its time still counts for the pass
    private void AddMark(Pass pass, string code, double ms)
    {
        if (!Index((int)pass, Count) || !Assert(code.Length > 0) || !Assert(ms >= 0)) return;
        var marks = _marks[(int)pass];
        ref var sum = ref CollectionsMarshal.GetValueRefOrNullRef(marks, code);
        if (!Unsafe.IsNullRef(ref sum)) sum += ms;
        else if (marks.Count < MaxMarks) marks[code] = ms;
    }

    // A mark carries the time since the previous one, so a stage boundary still belongs to the old pass and switches afterwards.
    private static Pass Classify(string code, ref Pass current)
    {
        if (!Assert(code.Length > 0)) return Pass.Other;
        switch (code)
        {
            case "sleep": return Pass.Sleep;
            case "mrl":
                current = Pass.GameTick;
                return Pass.Other;
            case "beginMTT":
            case "doneMTT":
                current = Pass.MainThread;
                return Pass.MainThread;
            case "end": return Pass.Swap;
        }

        if (!code.StartsWith(StagePrefix, StringComparison.Ordinal)) return current;

        var previous = current;
        current = code.AsSpan(StagePrefix.Length) switch
        {
            "Before" => Pass.Before,
            "ShadowFar" or "ShadowFarDone" or "ShadowNear" or "ShadowNearDone" => Pass.Shadows,
            "Opaque" => Pass.Opaque,
            "AfterOIT" or "AfterPostProcessing" or "AfterBlit" or "AfterFinalComposition" => Pass.PostProcess,
            "Ortho" => Pass.Gui,
            "Done" => Pass.Done,
            _ => Pass.Other
        };
        return previous;
    }

    // The panel promises "most expensive first", so the order has to come out of the same window the rows show:
    // sorting by a smoothed cost, or only every few intervals, leaves a pass in a place its own number no longer earns.
    // The sums rank like the averages, _frames being the same divisor for all of them.
    public void UpdateOrder()
    {
        if (_frames == 0) return;
        Array.Sort(_order, _byCost);
        for (var pass = 0; pass < Keys.Length; pass++)
        {
            Array.Clear(_topMarks[pass]);
            if (!Assert(_marks[pass].Count <= MaxMarks)) continue;
            using var marks = _marks[pass].GetEnumerator();
            for (var i = 0; i < MaxMarks && marks.MoveNext(); i++)
                TopN.Rank(_topMarks[pass], marks.Current.Key, marks.Current.Value);
        }
    }

    public void Reset()
    {
        (_frames, _sumTotalMs) = (0, 0);
        Array.Clear(_sumMs);
        Array.Clear(_worstMs);
        for (var i = 0; i < Keys.Length; i++) _marks[i].Clear();
    }

    public void ResetWorst()
    {
        if (!Assert(_worstTop.Length == DetailCount)) return;
        WorstFrameMs = 0;
        Array.Clear(_worstTop);
    }

    private static double ToMs(long ticks)
    {
        return Assert(ticks >= 0) ? ticks * 1000.0 / Stopwatch.Frequency : 0;
    }

    private enum Pass
    {
        GameTick,
        Before,
        Shadows,
        Opaque,
        Transparent,
        PostProcess,
        Gui,
        Done,
        MainThread,
        Swap,
        Sleep,
        Outside,
        Other
    }
}
