using System.Globalization;
using System.Runtime;
using System.Text;
using Komet.Api;
using Komet.Diagnostics;

namespace Komet.Hud;

// The protocol's sections in their order, one writer each
internal static partial class DebugProtocol
{
    private const int ShownSpikes = 10,
        ShownMods = 40,
        ShownMethods = 40,
        ShownEntities = 12,
        HistogramWidth = 40,
        ShownAllocFrames = 5;

    private const int ShownThreads = 8, ShownTypes = 15, ShownWaits = 8;
    internal const int ShownLog = 60; // log lines in the protocol; DebugFiles keeps no more
    private static readonly double[] Buckets = [8, 12, 16.7, 20, 25, 33.3, 50, 100, double.PositiveInfinity];
    private static readonly string[] GenNames = ["gen0", "gen1", "gen2", "loh", "poh"];

    private static void CaptureSection(DebugText text, DebugInput input, DebugSummary summary)
    {
        Section(text, "hud-dbg-s-capture");
        _ = text.Row("hud-dbg-mode", input.Mode).Row("hud-dbg-trigger", input.Trigger)
            .Row("hud-dbg-duration", summary.Seconds, "F1", "s").Row("hud-dbg-frames", DebugText.Int(summary.Frames))
            .Row("hud-dbg-steady", $"{summary.Steady} ({DebugText.Num(Share(summary.Steady, summary.Frames), "F0")} %)")
            .Row("hud-dbg-spikes-kept", DebugText.Int(input.Spikes.Length))
            .Row("hud-dbg-language", input.Code);
    }

    private static void EnvironmentSection(DebugText text, DebugInput input)
    {
        Rows(text, "hud-dbg-s-system", input.System);
        Section(text, "hud-dbg-s-game");
        foreach (var (name, value) in input.Client.Bounded(MaxRows))
        {
            _ = text.Pair(name, value);
        }
    }

    private static void KometSection(DebugText text, DebugInput input)
    {
        Section(text, "hud-dbg-s-komet");
        _ = text.Row("hud-dbg-knobs", $"{input.Knobs.Length}, {input.Knobs.Count(static k => k.Value != k.Engine)} " +
                                      text.T("hud-dbg-changed"));
        foreach (var (key, value, engine) in input.Knobs.Bounded(MaxRows))
        {
            if (value != engine) _ = text.Pair(key, $"{value}  ({text.T("hud-dbg-engine")} {engine})", 4);
        }

        var active = input.Features.Count(static f => f.State == KapiFeatureState.Active);
        _ = text.Row("hud-dbg-features", $"{input.Features.Length}, {active} " + text.T("hud-dbg-active"));
        foreach (var feature in input.Features.Bounded(MaxRows))
        {
            if (feature.State != KapiFeatureState.Active)
            {
                _ = text.Pair(feature.Id,
                    feature.State + (feature.HeldBy is { } by ? $"  ({by}: {feature.Reason})" : ""), 4);
            }
        }
    }

    private static void FramesSection(DebugText text, DebugInput input, DebugSummary s)
    {
        Section(text, "hud-dbg-s-frames");
        _ = text.Row("hud-dbg-fps", $"{N(s.Fps, "F1")}   1 %: {N(s.Low1Fps, "F1")}   0.1 %: {N(s.Low01Fps, "F1")}")
            .Row("hud-dbg-frametime", $"{N(s.AvgMs, "F2")} ms  ± {N(s.StdevMs, "F2")}")
            .Row("hud-dbg-percentiles",
                $"50 %: {N(s.MedianMs, "F1")}   90 %: {N(s.P90Ms, "F1")}   99 %: {N(s.P99Ms, "F1")}" +
                $"   99.9 %: {N(s.P999Ms, "F1")}   max: {N(s.WorstMs, "F1")} ms")
            .Row("hud-dbg-over", $"16.7 ms: {s.Over17}   33 ms: {s.Over33}   50 ms: {s.Over50}   100 ms: {s.Over100}")
            .Row("hud-dbg-split",
                $"GC {N(s.GcMs / Math.Max(1, s.Frames), "F2")}  JIT {N(s.JitMs / Math.Max(1, s.Frames), "F2")}" +
                $"  {text.T("hud-dbg-runqueue")} {N(s.RunQueueMs, "F2")}  {text.T("hud-dbg-outside")} {N(s.OutsideMs, "F2")}" +
                $"  Lock {N(LockMs(input.Events) / Math.Max(1, s.Frames), "F2")} ms")
            .Row("hud-dbg-stability", text.T("hud-dbg-stability-value", N(s.Jump95Ms, "F1"), N(s.Jump99Ms, "F1"),
                s.Hitches,
                N(PerMinute(s.Hitches, s.Seconds), "F1")))
            .Blank();
        Histogram(text, input.Frames);
    }

    private static void Histogram(DebugText text, DebugFrame[] frames)
    {
        Span<int> counts = stackalloc int[Buckets.Length];
        var steady = 0;
        foreach (var frame in frames.Bounded(DebugFrames.Capacity))
        {
            if (frame.Steady)
            {
                var bucket = 0;
                for (var i = 0; i < Buckets.Length && frame.DtMs > Buckets[i]; i++)
                {
                    bucket = i + 1;
                }

                counts[Math.Min(bucket, Buckets.Length - 1)]++;
                steady++;
            }
        }

        if (steady == 0) return;

        for (var i = 0; i < Buckets.Length; i++)
        {
            var bound = double.IsFinite(Buckets[i]) ? Buckets[i] : Buckets[i - 1];
            var label = (double.IsFinite(Buckets[i]) ? "<= " : " > ") +
                        bound.ToString("0.#", CultureInfo.InvariantCulture).PadLeft(5) + " ms";
            var bar = new string('#', (int)Math.Ceiling((double)counts[i] * HistogramWidth / steady));
            _ = text.Line($"{label}  {bar,-HistogramWidth} {counts[i],7} {N(Share(counts[i], steady), "F1"),6} %", 4);
        }
    }

    private static void SpikesSection(DebugText text, DebugInput input)
    {
        Section(text, "hud-dbg-s-spikes");
        foreach (var (spike, marks) in First(input.Spikes, ShownSpikes).Bounded(ShownSpikes))
        {
            var f = spike.Frame;
            _ = text.Line(
                $"{N(f.At, "F1"),7} s  {N(f.DtMs, "F1"),7} ms   GC {N(f.GcMs, "F1")}  JIT {N(f.JitMs, "F1")}  " +
                $"{text.T("hud-dbg-runqueue")} {N(f.RunQueueMs, "F1")}  Lock {N(LockIn(input.Events, f), "F1")}  " +
                $"{text.T("hud-dbg-outside")} {N(f.OutsideMs, "F1")}  {f.AllocKb} KiB{Gen(f.Gens)}");
            foreach (var (name, ms) in marks.Bounded(DebugFrames.Marks))
            {
                if (name is not null) _ = text.Line($"{Mark(text, name),-40} {N(ms, "F1"),7} ms", 14);
            }
        }

        if (Array.Exists(input.Causes, static c => c.Count > 0)) _ = text.Blank().Line(text.T("hud-dbg-causes"));

        foreach (var (name, count, max, average) in input.Causes.Bounded(MaxRows))
        {
            if (count > 0)
            {
                _ = text.Pair(Mark(text, name), $"{count,5} x   avg {N(average, "F1")} ms   max {N(max, "F1")} ms", 4);
            }
        }
    }

    private static string Gen(byte gens) => gens != 0 ? "  gen" + (31 - int.LeadingZeroCount(gens)) : "";

    // A pseudo-cause ("~gc") by the overlay's name for it
    private static string Mark(DebugText text, string name)
    {
        if (!Assert(name.Length > 0) || name[0] != '~') return name;

        var key = "hud-cause-" + name[1..];
        return text.T(key) is var shown && shown != key ? shown : name;
    }

    // The heap as it was when the capture ended (live only for an input without that snapshot)
    private static void MemorySection(DebugText text, DebugInput input, DebugSummary s)
    {
        Section(text, "hud-dbg-s-memory");
        var info = input.Gc ?? GC.GetGCMemoryInfo();
        _ = text.Row("hud-dbg-alloc",
                $"{N(s.AllocMbPerSec, "F1")} MB/s   {text.T("hud-dbg-main-thread")} {N(s.MainAllocMbPerSec, "F1")} MB/s")
            .Row("hud-dbg-collections", $"gen0 {s.Gen0}   gen1 {s.Gen1}   gen2 {s.Gen2}")
            .Row("hud-dbg-gc-pause",
                $"{N(s.GcMs, "F1")} ms  ({N(Share(s.GcMs, s.Seconds * 1000), "F2")} %)   max {N(s.GcMaxMs, "F1")} ms")
            .Row("hud-dbg-heap",
                $"{Mb(info.HeapSizeBytes)} MB   {text.T("hud-dbg-committed")} {Mb(info.TotalCommittedBytes)} MB" +
                $"   {text.T("hud-dbg-fragmented")} {Mb(info.FragmentedBytes)} MB")
            .Row("hud-dbg-generations", Generations(info))
            .Row("hud-dbg-gc-mode",
                $"{(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}, " +
                $"concurrent {AppContext.GetData("System.GC.Concurrent")?.ToString() ?? "default"}")
            .Row("hud-dbg-gc-last", $"#{info.Index} gen{info.Generation} {(info.Compacted ? "compacting" : "")}" +
                                    $"{(info.Concurrent ? " background" : "")}  promoted {Mb(info.PromotedBytes)} MB  " +
                                    $"pause {N(info.PauseDurations[0].TotalMilliseconds, "F1")} ms");
        var dearest = input.Frames.OrderByDescending(static f => f.AllocKb).Take(ShownAllocFrames).ToArray();
        _ = text.Line(text.T("hud-dbg-alloc-frames"));
        foreach (var frame in dearest.Bounded(ShownAllocFrames))
        {
            _ = text.Line(
                $"{N(frame.At, "F1"),7} s  {frame.AllocKb,8} KiB  ({text.T("hud-dbg-main-thread")} {frame.MainAllocKb} KiB)" +
                $"  {N(frame.DtMs, "F1")} ms", 4);
        }
    }

    private static string Mb(long bytes) => N(bytes / 1048576.0, "F0");

    private static string Generations(GCMemoryInfo info)
    {
        var sizes = info.GenerationInfo;
        if (!Assert(sizes.Length >= 3) || !Assert(sizes.Length <= 8)) return "";

        var text = new StringBuilder();
        for (var i = 0; i < Math.Min(sizes.Length, GenNames.Length); i++)
        {
            _ = text.Append(GenNames[i]).Append(' ').Append(Mb(sizes[i].SizeAfterBytes)).Append("  ");
        }

        return text.ToString().TrimEnd() + " MB";
    }

    // Who allocates and who holds the main thread up, from the runtime's own events (RuntimeEvents): sampled allocation by
    // thread group and type, the main thread's lock waits by the thread holding the lock, every group's waits
    private static void AllocatorsSection(DebugText text, DebugInput input, DebugSummary s)
    {
        Section(text, "hud-dbg-s-alloc");
        var e = input.Events;
        if (e.Seconds <= 0)
        {
            _ = text.Line(text.T("hud-dbg-none"));
            return;
        }

        var perSecond = 1 / (1048576.0 * e.Seconds);
        _ = text.Row("hud-dbg-alloc-sampled", $"{N(e.Sampled * perSecond, "F1")} MB/s")
            .Line(text.T("hud-dbg-alloc-threads"));
        foreach (var (thread, bytes) in First(e.Threads, ShownThreads).Bounded(ShownThreads))
        {
            _ = text.Line($"{thread,-28} {N(bytes * perSecond, "F1"),7} MB/s  {N(Share(bytes, e.Sampled), "F0"),4} %",
                4);
        }

        _ = text.Line(text.T("hud-dbg-alloc-types"));
        foreach (var (thread, type, bytes) in First(e.Types, ShownTypes).Bounded(ShownTypes))
        {
            _ = text.Line($"{Clip(type),-56} {N(bytes * perSecond, "F1"),7} MB/s  {thread}", 4);
        }

        var main = LockMs(e);
        _ = text.Blank().Row("hud-dbg-locks-main",
            $"{N(main, "F1")} ms  ({N(main / Math.Max(1, s.Frames), "F2")} ms/frame)");
        foreach (var (owner, ms) in First(e.MainWaitsBy, ShownWaits).Bounded(ShownWaits))
        {
            _ = text.Line($"{text.T("hud-dbg-locks-held-by")} {owner,-24} {N(ms, "F1"),8} ms", 4);
        }

        _ = text.Line(text.T("hud-dbg-locks-threads"));
        foreach (var (thread, ms, count) in First(e.Waits, ShownWaits).Bounded(ShownWaits))
        {
            _ = text.Line($"{thread,-28} {N(ms, "F1"),8} ms  {count,7} x", 4);
        }
    }

    private static string Clip(string type) => type.Length > 56 ? "…" + type[^55..] : type;

    // The main thread's lock waits over the capture
    private static double LockMs(RuntimeEvents.Snapshot events)
    {
        if (!NotNull(events.MainWaits)) return 0;

        var sum = 0.0;
        foreach (var (_, ms) in events.MainWaits.Bounded(1 << 16))
        {
            sum += ms;
        }

        return sum;
    }

    // Those that ended within the frame: (At - Dt, At]
    private static double LockIn(RuntimeEvents.Snapshot events, DebugFrame frame)
    {
        if (!NotNull(events.MainWaits)) return 0;

        var (from, sum) = (frame.At - frame.DtMs / 1000f, 0.0);
        foreach (var (at, ms) in events.MainWaits.Bounded(1 << 16))
        {
            if (at > from && at <= frame.At) sum += ms;
        }

        return sum;
    }

    private static void WorldSection(DebugText text, DebugInput input)
    {
        Rows(text, "hud-dbg-s-world", input.World);
        _ = text.Row("hud-dbg-entities", DebugText.Int(input.EntityTotal));
        foreach (var (code, count) in First(input.Entities, ShownEntities).Bounded(ShownEntities))
        {
            _ = text.Pair(code, DebugText.Int(count), 4);
        }
    }

    private static void ModsSection(DebugText text, DebugInput input)
    {
        Section(text, "hud-dbg-s-mods");
        _ = text.Line(text.T("hud-dbg-mod-times"));
        foreach (var (mod, ms, top) in input.ModTimes.Bounded(MaxRows))
        {
            _ = text.Pair(mod, $"{N(ms, "F3")} ms/frame", 4);
            foreach (var (name, detail) in top.Bounded(ModTimes.DetailCount))
            {
                _ = text.Pair(name, $"{N(detail, "F3")} ms", 8);
            }
        }

        if (input.Scopes.Length > 0) _ = text.Blank().Line(text.T("hud-dbg-scopes"));

        foreach (var (name, ms, calls) in input.Scopes.Bounded(MaxRows))
        {
            _ = text.Pair(name,
                $"{N(ms, "F2")} ms   {calls} x   {N(calls > 0 ? ms / calls * 1000 : double.NaN, "F1")} us/call", 4);
        }

        var code = input.Mods.Count(static m => m.Kind == "Code");
        _ = text.Blank().Row("hud-dbg-mods-loaded",
            $"{input.Mods.Length}  ({code} code, {input.Mods.Length - code} content)");
        foreach (var (id, name, version, kind, source) in input.Mods.Bounded(HarmonyAudit.MaxLoadedMods))
        {
            if (kind == "Code") _ = text.Pair(id, $"{version,-12} {name}  [{source}]", 4);
        }

        var content = string.Join(", ",
            input.Mods.Where(static m => m.Kind != "Code").Select(static m => m.Id + " " + m.Version));
        if (content.Length > 0) _ = text.Line(text.T("hud-dbg-content-mods") + " " + content, 4);
    }

    private static void HarmonySection(DebugText text, HarmonyAudit audit)
    {
        Section(text, "hud-dbg-s-harmony");
        _ = text.Row("hud-dbg-patched", $"{audit.Methods.Length}   {text.T("hud-dbg-owners")} {audit.Owners.Length}" +
                                        $"   {text.T("hud-dbg-shared")} {audit.Shared}");
        foreach (var (owner, mod, methods) in First(audit.Owners, ShownMods).Bounded(ShownMods))
        {
            _ = text.Pair(owner, $"{methods,5}  {mod}", 4);
        }

        if (Array.Exists(audit.Methods, static m => m.Owners >= 2))
        {
            _ = text.Blank().Line(text.T("hud-dbg-shared-methods"));
        }

        var shown = 0;
        foreach (var method in audit.Methods.Bounded(HarmonyAudit.MaxMethods))
        {
            if (method.Owners < 2 || ++shown > ShownMethods) continue;

            _ = text.Line($"{text.T("hud-dbg-risk-" + method.Risk.ToString().ToLowerInvariant()),-7} {method.Name}", 4);
            foreach (var link in method.Chain.Bounded(HarmonyAudit.MaxPatches))
            {
                _ = text.Line(
                    $"{link.Kind,-10} {link.Owner,-24} prio {link.Priority,4}{(link.Skips ? "  " + text.T("hud-dbg-can-skip") : "")}" +
                    $"  {link.Method}", 12);
            }

            if (method.Risk == PatchRisk.High) _ = text.Line(text.T(Effect(method)), 12);
        }

        if (shown > ShownMethods) _ = text.Line(text.T("hud-dbg-more-in-file", shown - ShownMethods, "harmony.txt"), 4);
    }

    private static string Effect(PatchedMethod method) =>
        method.Chain.Where(static l => l.Kind == PatchKind.Transpiler).Select(static l => l.Owner).Distinct()
            .Count() > 1
            ? "hud-dbg-effect-transpilers"
            : "hud-dbg-effect-skip";

    private static void LogSection(DebugText text, DebugExtra extra)
    {
        Section(text, "hud-dbg-s-log");
        _ = text.Row("hud-dbg-log-counts", $"{extra.LogErrors} / {extra.LogWarnings}");
        var from = Math.Max(0, extra.Log.Length - ShownLog);
        foreach (var line in extra.Log.AsSpan(from).Bounded(ShownLog))
        {
            _ = text.Line(line.Length > DebugText.MaxWidth * 2
                ? line[..(DebugText.MaxWidth * 2)] + " ..."
                : line, 4);
        }
    }
}
