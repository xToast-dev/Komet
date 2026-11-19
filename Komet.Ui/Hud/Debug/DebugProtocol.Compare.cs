using System.Globalization;
using Komet.Diagnostics;

namespace Komet.Hud;

// The numbers kept for the next protocol (summary.txt), whether two captures are comparable, and the compare section
internal static partial class DebugProtocol
{
    private const double ShortSeconds = 9.5, TrianglesRatio = 1.3, LoadingBacklog = 100;

    internal static (string Key, double Value)[] Numbers(DebugSummary s) =>
    [
        ("fps", s.Fps), ("low1", s.Low1Fps), ("low01", s.Low01Fps), ("avg", s.AvgMs), ("p99", s.P99Ms),
        ("worst", s.WorstMs), ("over50", Share(s.Over50, s.Steady)), ("gc", Share(s.GcMs, s.Seconds * 1000)),
        ("alloc", s.AllocMbPerSec), ("jit", s.JitMs), ("runqueue", s.RunQueueMs), ("jump99", s.Jump99Ms),
        ("hitches", PerMinute(s.Hitches, s.Seconds))
    ];

    internal static (string Key, double Value)[] Scene(DebugInput input, DebugSummary s) =>
    [
        ("s-mode", ModeCode(input.Mode)), ("s-seconds", s.Seconds), ("s-backlog", input.TessBacklog), .. input.Scene
    ];

    private static double ModeCode(string mode)
    {
        if (mode.StartsWith("spike", StringComparison.Ordinal)) return 2;

        return mode == "now" ? 1 : 0;
    }

    private static bool Differ(double before, double after) => Math.Abs(before - after) > 0.5;

    // Why the captures are not comparable; empty when they are. Older summaries are checked for what they hold.
    internal static string Unlike(System.Func<string, object[], string> t, Dictionary<string, double> previous,
        DebugInput input, DebugSummary s)
    {
        var now = Scene(input, s).ToDictionary(static n => n.Key, static n => n.Value, StringComparer.Ordinal);
        var reasons = new List<string>();

        bool Both(string key, out double before, out double after)
        {
            after = now.GetValueOrDefault(key, double.NaN);
            return previous.TryGetValue(key, out before) && double.IsFinite(before) && double.IsFinite(after);
        }

        string[] modes = ["record", "now", "spike"];
        if (Both("s-mode", out var m0, out var m1) && Differ(m0, m1) && (uint)m0 < modes.Length &&
            (uint)m1 < modes.Length)
        {
            reasons.Add(t("hud-dbg-unlike-mode", [modes[(int)m0], modes[(int)m1]]));
        }

        if (Both("s-seconds", out var t0, out var t1) && Math.Min(t0, t1) < ShortSeconds)
        {
            reasons.Add(t("hud-dbg-unlike-short", [N(t0, "F1"), N(t1, "F1")]));
        }

        if (Both("s-backlog", out var b0, out var b1) && Math.Max(b0, b1) > LoadingBacklog)
        {
            reasons.Add(t("hud-dbg-unlike-loading", [N(b0, "F0"), N(b1, "F0")]));
        }

        if (Both("s-vd", out var v0, out var v1) && Differ(v0, v1))
        {
            reasons.Add(t("hud-dbg-unlike-vd", [N(v0, "F0"), N(v1, "F0")]));
        }

        if (Both("s-pixels", out var p0, out var p1) && Math.Abs(p0 - p1) > 0.01 * Math.Max(p0, p1))
        {
            reasons.Add(t("hud-dbg-unlike-window", [N(p0 / 1e6, "F1"), N(p1 / 1e6, "F1")]));
        }

        if (Both("s-renderer", out var r0, out var r1) && Differ(r0, r1)) reasons.Add(t("hud-dbg-unlike-renderer", []));

        if (Both("s-tris", out var g0, out var g1) && Math.Min(g0, g1) > 0 &&
            Math.Max(g0, g1) / Math.Min(g0, g1) >= TrianglesRatio)
        {
            reasons.Add(t("hud-dbg-unlike-tris", [N(g0 / 1e6, "F0"), N(g1 / 1e6, "F0")]));
        }

        return reasons.Count > 0 ? string.Join("; ", reasons) : "";
    }

    private static void CompareSection(DebugText text, DebugExtra extra, DebugInput input, DebugSummary s)
    {
        Section(text, "hud-dbg-s-compare");
        if (extra.Previous is not { } previous)
        {
            _ = text.Line(text.T("hud-dbg-compare-none"));
            return;
        }

        _ = text.Line(text.T("hud-dbg-compare-with", extra.PreviousStamp));
        if (Unlike(text.T, previous, input, s) is { Length: > 0 } unlike)
        {
            _ = text.Line(text.T("hud-dbg-compare-unlike", unlike));
        }

        foreach (var (key, now) in Numbers(s).Bounded(MaxRows))
        {
            if (!previous.TryGetValue(key, out var before) || !double.IsFinite(before) ||
                !double.IsFinite(now))
            {
                continue;
            }

            var change = Math.Abs(before) > 0 ? 100 * (now - before) / Math.Abs(before) : double.NaN;
            var shown = double.IsFinite(change) ? change.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " %" : "";
            _ = text.Row("hud-dbg-n-" + key, $"{N(before, "F2"),10} -> {N(now, "F2"),10}   {shown}");
        }
    }
}
