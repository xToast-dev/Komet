using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Komet.Runtime.Diagnostics;

// Maps the engine profiler's marks to render passes. Queries address a place in the list sorted by cost.
internal sealed class RenderPassStats
{
    private enum Pass { GameTick, Before, Shadows, Opaque, Transparent, PostProcess, Gui, Done, MainThread, Swap, Sleep, Other }

    private static readonly string[] Keys = ["gametick", "before", "shadows", "opaque", "transparent", "postprocess", "gui", "done", "mainthread", "swap", "sleep", "other"];
    private const string StagePrefix = "beginrenderstage-";
    private const double Smoothing = 0.25;
    private const int ReorderEvery = 8;
    public const int DetailCount = 3, MaxMarks = 256;   // profiler marks per frame

    private readonly double[] _sumMs = new double[Keys.Length], _worstMs = new double[Keys.Length], _frameMs = new double[Keys.Length];
    private readonly Dictionary<string, double>[] _marks = Array.ConvertAll(Keys, _ => new Dictionary<string, double>());
    private double _sumTotalMs;
    private int _frames;

    private readonly double[] _smoothMs = new double[Keys.Length];
    private readonly int[] _order = new int[Keys.Length];
    private readonly (string? Name, double Ms)[][] _topMarks = Array.ConvertAll(Keys, _ => new (string?, double)[DetailCount]);
    private int _updates;

    public static int Count => Keys.Length;
    public string Key(int place) => Index(place, Count) ? Keys[_order[place]] : "";
    public double AverageTotalMs => _frames == 0 ? 0 : _sumTotalMs / _frames;
    public double AverageMs(int place) => Index(place, Count) && _frames > 0 ? _sumMs[_order[place]] / _frames : 0;
    public double Percent(int place) => Index(place, Count) && AverageTotalMs > 0 ? AverageMs(place) / AverageTotalMs * 100 : 0;
    public double WorstPercent(int place) => Index(place, Count) && AverageTotalMs > 0 ? _worstMs[_order[place]] / AverageTotalMs * 100 : 0;
    public string DetailName(int place, int rank) => Index(place, Count) && Index(rank, DetailCount) ? _topMarks[_order[place]][rank].Name ?? "–" : "";
    public double DetailMs(int place, int rank)
        => Index(place, Count) && Index(rank, DetailCount) && _frames > 0 && _topMarks[_order[place]][rank].Name is { } mark ? _marks[_order[place]].GetValueOrDefault(mark) / _frames : double.NaN;

    public RenderPassStats()
    {
        if (!Assert(Keys.Length == Enum.GetValues<Pass>().Length)) return;
        for (var i = 0; i < Keys.Length; i++) _order[i] = i;
    }

    public void AddFrame(ProfileEntryRange? frame)
    {
        if (frame?.Marks == null) return;
        var total = ToMs(frame.ElapsedTicks);
        if (!Assert(total >= 0) || !Assert(frame.Marks.Count <= MaxMarks)) return;
        _frames++;
        _sumTotalMs += total;
        Array.Clear(_frameMs);

        var assigned = 0.0;
        var current = Pass.Other;
        using var marks = frame.Marks.GetEnumerator();   // struct enumerator: no allocation per frame, order = order within the frame
        for (var i = 0; i < MaxMarks && marks.MoveNext(); i++)
        {
            var (code, entry) = marks.Current;
            var ms = ToMs(entry.ElapsedTicks);
            var pass = Classify(code, ref current);
            _frameMs[(int)pass] += ms;
            AddMark(pass, code, ms);
            assigned += ms;
        }

        if (frame.ChildRanges != null && frame.ChildRanges.TryGetValue("rendTransparent", out var transparent))
        {
            var ms = ToMs(transparent.ElapsedTicks);
            _frameMs[(int)Pass.Transparent] += ms;
            assigned += ms;
            if (transparent.Marks != null)
            {
                using var inner = transparent.Marks.GetEnumerator();
                for (var i = 0; i < MaxMarks && inner.MoveNext(); i++) AddMark(Pass.Transparent, inner.Current.Key, ToMs(inner.Current.Value.ElapsedTicks));
            }
        }

        _frameMs[(int)Pass.Other] += Math.Max(0, total - assigned);
        for (var i = 0; i < Keys.Length; i++)
        {
            _sumMs[i] += _frameMs[i];
            _worstMs[i] = Math.Max(_worstMs[i], _frameMs[i]);
        }
    }

    private void AddMark(Pass pass, string code, double ms)
    {
        if (!Index((int)pass, Count) || !Assert(code.Length > 0) || !Assert(ms >= 0)) return;
        CollectionsMarshal.GetValueRefOrAddDefault(_marks[(int)pass], code, out _) += ms;
    }

    // A mark carries the time since the previous one, so a stage boundary still belongs to the old pass and switches afterwards.
    private static Pass Classify(string code, ref Pass current)
    {
        if (!Assert(code.Length > 0)) return Pass.Other;
        switch (code)
        {
            case "sleep":     return Pass.Sleep;
            case "mrl":       current = Pass.GameTick; return Pass.Other;
            case "beginMTT":
            case "doneMTT":   current = Pass.MainThread; return Pass.MainThread;
            case "end":       return Pass.Swap;
            default:          break;
        }
        if (!code.StartsWith(StagePrefix, StringComparison.Ordinal)) return current;

        var previous = current;
        current = code.AsSpan(StagePrefix.Length) switch
        {
            "Before" => Pass.Before,
            "ShadowFar" or "ShadowFarDone" or "ShadowNear" or "ShadowNearDone" => Pass.Shadows,
            "Opaque" => Pass.Opaque,
            "OIT" => Pass.Transparent,
            "AfterOIT" or "AfterPostProcessing" or "AfterBlit" or "AfterFinalComposition" => Pass.PostProcess,
            "Ortho" => Pass.Gui,
            "Done" => Pass.Done,
            _ => Pass.Other,
        };
        return previous;
    }

    public void UpdateOrder()
    {
        if (_frames == 0) return;
        for (var pass = 0; pass < Keys.Length; pass++)
            _smoothMs[pass] += (_sumMs[pass] / _frames - _smoothMs[pass]) * Smoothing;
        var reorder = _updates % ReorderEvery == 0;
        _updates++;
        if (!reorder) return;

        Array.Sort(_order, (a, b) => _smoothMs[b].CompareTo(_smoothMs[a]));
        if (!Index(_order[0], Count)) return;
        for (var pass = 0; pass < Keys.Length; pass++)
        {
            Array.Clear(_topMarks[pass]);
            foreach (var (name, ms) in _marks[pass].Bounded(MaxMarks)) Rank(_topMarks[pass], name, ms);
        }
    }

    // Insertion into the top-N slots, sorted descending: a new item enters at its rank and the last one falls off
    public static void Rank<T>(Span<(T? Item, double Ms)> top, T item, double ms) where T : class
    {
        if (!Assert(top.Length is > 0 and <= DetailCount) || !Assert(ms >= 0)) return;
        var slot = 0;
        for (; slot < DetailCount && slot < top.Length; slot++) if (top[slot].Item is null || top[slot].Ms < ms) break;
        if (slot == top.Length) return;
        top[slot..^1].CopyTo(top[(slot + 1)..]);
        top[slot] = (item, ms);
    }

    public void Reset()
    {
        (_frames, _sumTotalMs) = (0, 0);
        Array.Clear(_sumMs);
        Array.Clear(_worstMs);
        for (var i = 0; i < Keys.Length; i++) _marks[i].Clear();
    }

    private static double ToMs(long ticks) => Assert(ticks >= 0) ? ticks * 1000.0 / Stopwatch.Frequency : 0;
}
