using Komet.Api;
using Komet.Diagnostics;
using System.Globalization;
using System.Runtime;

namespace Komet.Hud;

// Plain-text report of everything Komet measures, in sections findings point into; built on a pool thread. Findings:
// what stands out, worst first; coarse thresholds say where to look, the section how bad.
internal static partial class DebugProtocol
{
    public const int MaxFindings = 16, MaxRows = 512;
    private const double ModMsMedium = 2, ModMsHigh = 5, AllocMedium = 200, AllocHigh = 500;

    // A finding refers to its section by index + 1
    internal static readonly string[] Sections =
    [
        "hud-dbg-s-findings", "hud-dbg-s-capture", "hud-dbg-s-system", "hud-dbg-s-game", "hud-dbg-s-komet", "hud-dbg-s-frames",
        "hud-dbg-s-spikes", "hud-dbg-s-memory", "hud-dbg-s-alloc", "hud-dbg-s-runtime", "hud-dbg-s-world", "hud-dbg-s-render", "hud-dbg-s-mods",
        "hud-dbg-s-harmony", "hud-dbg-s-log", "hud-dbg-s-hud", "hud-dbg-s-compare", "hud-dbg-s-files"
    ];

    internal enum Severity { High, Medium, Low, Info }

    internal readonly record struct Finding(Severity Level, string Where, string Key, object[] Args);

    public static string Build(DebugInput input, DebugExtra extra, DebugSummary summary, string[] files)
    {
        if (!NotNull(input) || !NotNull(extra) || !NotNull(files)) return "";
        var text = new DebugText(input.Code);
        Head(text, input, summary);
        var findings = Findings(input, extra, summary);
        Section(text, "hud-dbg-s-findings");
        foreach (var finding in findings.Bounded(MaxFindings))
            _ = text.Line($"{text.T("hud-dbg-level-" + finding.Level.ToString().ToLowerInvariant()),-7} " +
                          $"{text.T(finding.Key, finding.Args)}  > {Number(finding.Where)}");
        CaptureSection(text, input, summary);
        EnvironmentSection(text, input);
        KometSection(text, input);
        FramesSection(text, input, summary);
        SpikesSection(text, input);
        MemorySection(text, input, summary);
        AllocatorsSection(text, input, summary);
        Rows(text, "hud-dbg-s-runtime", input.Runtime);
        WorldSection(text, input);
        Rows(text, "hud-dbg-s-render", input.Render);
        ModsSection(text, input);
        HarmonySection(text, extra.Audit);
        LogSection(text, extra);
        Section(text, "hud-dbg-s-hud");
        _ = text.Line(input.HudMeans.Length > 0 ? input.HudMeans.Replace("\n", "\n  ", StringComparison.Ordinal) : text.T("hud-dbg-none"));
        CompareSection(text, extra, input, summary);
        Section(text, "hud-dbg-s-files");
        foreach (var file in files.Bounded(MaxRows)) _ = text.Line(file);
        ExtraSections(text, input);
        return Anonymize(text.ToString());
    }

    internal static int Number(string section) =>
        Array.IndexOf(Sections, section) is var at && Index(at, Sections.Length) ? at + 1 : 0;

    private static void Section(DebugText text, string key)
    {
        if (!Assert(Number(key) > 0)) return;
        _ = text.Section(Number(key), key);
    }

    private static void Rows(DebugText text, string section, (string Key, string Value)[] rows)
    {
        Section(text, section);
        foreach (var (key, value) in rows.Bounded(MaxRows)) _ = text.Row(key, value);
    }

    private static void Head(DebugText text, DebugInput input, DebugSummary summary)
    {
        _ = text.Head(text.T("hud-dbg-title"))
            .Head($"Komet {input.Komet} | Vintage Story {GameVersion.OverallVersion} | {input.Started:yyyy-MM-dd HH:mm:ss zzz}")
            .Head(text.T("hud-dbg-head", input.Mode, DebugText.Num(summary.Seconds, "F1"), summary.Frames,
                DebugText.Num(summary.Fps, "F1"), DebugText.Num(summary.Low1Fps, "F1"), DebugText.Num(summary.WorstMs, "F1")));
        if (input.Trigger.Length > 0) _ = text.Head(input.Trigger);
    }

    internal static double Share(double part, double whole) =>
        Assert(part >= 0 || double.IsNaN(part)) && whole > 0 ? 100 * part / whole : double.NaN;

    private static string N(double value, string format) => DebugText.Num(value, format);

    private static double PerMinute(int count, double seconds) =>
        Finite(seconds) && seconds > 0 ? count * 60.0 / seconds : double.NaN;

    internal static ReadOnlySpan<T> First<T>(T[] items, int count) =>
        items.AsSpan(0, Math.Min(items.Length, count));

    internal static string Anonymize(string text)
    {
        if (!NotNull(text) || text.Length == 0) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) text = text.Replace(home, "~", StringComparison.Ordinal);
        var user = Environment.UserName;
        if (user.Length < 3) return text;
        return text.Replace("/" + user + "/", "/<user>/", StringComparison.Ordinal)
            .Replace("\\" + user + "\\", "\\<user>\\", StringComparison.Ordinal);
    }

    private static void ExtraSections(DebugText text, DebugInput input)
    {
        if (!NotNull(input.Extra) || !Assert(input.Extra.Length <= MaxRows)) return;
        var number = Sections.Length;
        foreach (var (title, body) in input.Extra.Bounded(MaxRows))
        {
            _ = text.Blank().Head($"[{++number}] {title.ToUpperInvariant()} " + new string('=', Math.Max(4, DebugText.MaxWidth - title.Length - 6)));
            _ = text.Line(body.Replace("\n", "\n  ", StringComparison.Ordinal));
        }
    }

    private static Finding[] Findings(DebugInput input, DebugExtra extra, DebugSummary s)
    {
        var list = new List<Finding>();
        Performance(list, s);
        Context(list, input, extra);
        if (extra.Previous is { } previous && Unlike(static (key, _) => key, previous, input, s).Length > 0)
            list.Add(new Finding(Severity.Info, "hud-dbg-s-compare", "hud-dbg-f-unlike", []));
        if (list.Count == 0) list.Add(new Finding(Severity.Info, "hud-dbg-s-capture", "hud-dbg-f-none", []));
        list.Sort(static (a, b) => a.Level.CompareTo(b.Level));
        return [.. list.Take(MaxFindings)];
    }

    private static void Performance(List<Finding> list, DebugSummary s)
    {
        if (s.Frames == 0) return;
        var gc = Share(s.GcMs, s.Seconds * 1000);
        if (gc >= 2)
            list.Add(new Finding(gc >= 5 ? Severity.High : Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-gc",
                [N(gc, "F1"), s.Gen0, N(s.GcMaxMs, "F1")]));
        if (s.Gen2 > 0) list.Add(new Finding(Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-gen2", [s.Gen2]));
        if (s.AllocMbPerSec >= AllocMedium)
            list.Add(new Finding(s.AllocMbPerSec >= AllocHigh ? Severity.High : Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-alloc",
                [N(s.AllocMbPerSec, "F0"), N(Share(s.MainAllocMbPerSec, s.AllocMbPerSec), "F0")]));
        if (s.JitMs >= 50)
            list.Add(new Finding(s.JitMs >= 500 ? Severity.Medium : Severity.Low, "hud-dbg-s-runtime", "hud-dbg-f-jit", [N(s.JitMs, "F0")]));
        if (s.WorstMs >= 100) list.Add(new Finding(Severity.High, "hud-dbg-s-spikes", "hud-dbg-f-worst", [N(s.WorstMs, "F0")]));
        if (Share(s.Over50, s.Steady) >= 1)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-frames", "hud-dbg-f-over50", [s.Over50, N(Share(s.Over50, s.Steady), "F1")]));
        if (s.Low1Fps < 30) list.Add(new Finding(Severity.Medium, "hud-dbg-s-frames", "hud-dbg-f-low", [N(s.Low1Fps, "F0")]));
        if (s.RunQueueMs >= 1)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-runtime", "hud-dbg-f-runqueue",
                [N(s.RunQueueMs, "F1"), N(s.RunQueueMaxMs, "F0")]));
    }

    private static void Context(List<Finding> list, DebugInput input, DebugExtra extra)
    {
        foreach (var (mod, ms, _) in input.ModTimes.Bounded(MaxRows))
            if (ms >= ModMsMedium)
                list.Add(new Finding(ms >= ModMsHigh ? Severity.High : Severity.Medium, "hud-dbg-s-mods", "hud-dbg-f-mod", [mod, N(ms, "F2")]));
        var risky = extra.Audit.Methods.Count(static m => m.Risk == PatchRisk.High);
        if (risky > 0) list.Add(new Finding(Severity.Medium, "hud-dbg-s-harmony", "hud-dbg-f-harmony", [risky]));
        if (extra.LogErrors > 0)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-log", "hud-dbg-f-log", [extra.LogErrors, extra.LogWarnings]));
        if (input.VramPercent >= 90) list.Add(new Finding(Severity.High, "hud-dbg-s-render", "hud-dbg-f-vram", [N(input.VramPercent, "F0")]));
        if (input.RamFreePercent < 10)
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-system", "hud-dbg-f-ram", [N(input.RamFreePercent, "F0")]));
        if (input.TessBacklog > 500) list.Add(new Finding(Severity.Low, "hud-dbg-s-world", "hud-dbg-f-backlog", [input.TessBacklog]));
        foreach (var feature in input.Features.Bounded(MaxRows))
            if (feature.State is FeatureState.Failed or FeatureState.StoodDown or FeatureState.EngineChanged)
                list.Add(new Finding(Severity.Info, "hud-dbg-s-komet", "hud-dbg-f-feature", [feature.Id, feature.State]));
    }
}

// What only the main thread may read, copied when the capture ends; the pool thread adds Harmony, log, previous
// protocol.
internal sealed class DebugInput
{
    public required string Code { get; set; }
    public required string Mode { get; init; }
    public string Trigger { get; init; } = "";
    public required DateTimeOffset Started { get; init; }
    public required string Komet { get; init; }
    public required DebugFrame[] Frames { get; init; }
    public required (DebugSpike Spike, (string? Name, float Ms)[] Marks)[] Spikes { get; init; }
    public required (string Name, int Count, double MaxMs, double AverageMs)[] Causes { get; init; }
    public required (string Mod, double Ms, (string Name, double Ms)[] Top)[] ModTimes { get; init; }
    public required (string Name, double Ms, long Calls)[] Scopes { get; init; }
    public (string Title, string Text)[] Extra { get; set; } = [];
    public required string HudMeans { get; init; }
    public required (string Key, string Value)[] System { get; init; }
    public required (string Key, string Value)[] Runtime { get; init; }
    public required (string Key, string Value)[] World { get; init; }
    public required (string Key, string Value)[] Render { get; init; }
    public required (string Name, string Value)[] Client { get; init; }
    public required (string Key, int Value, int Engine)[] Knobs { get; init; }
    public required FeatureInfo[] Features { get; init; }
    public required (string Id, string Name, string Version, string Kind, string Source)[] Mods { get; init; }
    public required (string Code, int Count)[] Entities { get; init; }
    public required int EntityTotal { get; init; }
    public double VramPercent { get; init; } = double.NaN;
    public double RamFreePercent { get; init; } = double.NaN;
    public int TessBacklog { get; init; }

    // The heap when the capture ended: a relabel rebuilds the protocol later and must show the same numbers
    public GCMemoryInfo? Gc { get; init; }

    public (string Key, double Value)[] Scene { get; init; } = [];

    // The runtime's allocation samples and lock waits over the capture (RuntimeEvents)
    public RuntimeEvents.Snapshot Events { get; set; } = RuntimeEvents.Snapshot.Empty;
}

// Pool-thread half: full registry, log warnings and errors since the capture began, last protocol's numbers
internal sealed record DebugExtra(HarmonyAudit Audit, string[] Log, int LogErrors, int LogWarnings, Dictionary<string, double>? Previous,
    string PreviousStamp);

// The protocol's sections in their order, one writer each
internal static partial class DebugProtocol
{
    private const int ShownSpikes = 10, ShownMods = 40, ShownMethods = 40, ShownEntities = 12, HistogramWidth = 40, ShownAllocFrames = 5;
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
        foreach (var (name, value) in input.Client.Bounded(MaxRows)) _ = text.Pair(name, value);
    }

    private static void KometSection(DebugText text, DebugInput input)
    {
        Section(text, "hud-dbg-s-komet");
        _ = text.Row("hud-dbg-knobs", $"{input.Knobs.Length}, {input.Knobs.Count(static k => k.Value != k.Engine)} " +
                                      text.T("hud-dbg-changed"));
        foreach (var (key, value, engine) in input.Knobs.Bounded(MaxRows))
            if (value != engine) _ = text.Pair(key, $"{value}  ({text.T("hud-dbg-engine")} {engine})", 4);
        var active = input.Features.Count(static f => f.State == FeatureState.Active);
        _ = text.Row("hud-dbg-features", $"{input.Features.Length}, {active} " + text.T("hud-dbg-active"));
        foreach (var feature in input.Features.Bounded(MaxRows))
            if (feature.State != FeatureState.Active)
                _ = text.Pair(feature.Id, feature.State + (feature.HeldBy is { } by ? $"  ({by}: {feature.Reason})" : ""), 4);
    }

    private static void FramesSection(DebugText text, DebugInput input, DebugSummary s)
    {
        Section(text, "hud-dbg-s-frames");
        _ = text.Row("hud-dbg-fps", $"{N(s.Fps, "F1")}   1 %: {N(s.Low1Fps, "F1")}   0.1 %: {N(s.Low01Fps, "F1")}")
            .Row("hud-dbg-frametime", $"{N(s.AvgMs, "F2")} ms  ± {N(s.StdevMs, "F2")}")
            .Row("hud-dbg-percentiles", $"50 %: {N(s.MedianMs, "F1")}   90 %: {N(s.P90Ms, "F1")}   99 %: {N(s.P99Ms, "F1")}" +
                                        $"   99.9 %: {N(s.P999Ms, "F1")}   max: {N(s.WorstMs, "F1")} ms")
            .Row("hud-dbg-over", $"16.7 ms: {s.Over17}   33 ms: {s.Over33}   50 ms: {s.Over50}   100 ms: {s.Over100}")
            .Row("hud-dbg-split", $"GC {N(s.GcMs / Math.Max(1, s.Frames), "F2")}  JIT {N(s.JitMs / Math.Max(1, s.Frames), "F2")}" +
                                  $"  {text.T("hud-dbg-runqueue")} {N(s.RunQueueMs, "F2")}  {text.T("hud-dbg-outside")} {N(s.OutsideMs, "F2")}" +
                                  $"  Lock {N(LockMs(input.Events) / Math.Max(1, s.Frames), "F2")} ms")
            .Row("hud-dbg-stability", text.T("hud-dbg-stability-value", N(s.Jump95Ms, "F1"), N(s.Jump99Ms, "F1"), s.Hitches,
                N(PerMinute(s.Hitches, s.Seconds), "F1")))
            .Blank();
        Histogram(text, input.Frames);
    }

    private static void Histogram(DebugText text, DebugFrame[] frames)
    {
        Span<int> counts = stackalloc int[Buckets.Length];
        var steady = 0;
        foreach (var frame in frames.Bounded(DebugFrames.Capacity))
            if (frame.Steady)
            {
                var bucket = 0;
                for (var i = 0; i < Buckets.Length && frame.DtMs > Buckets[i]; i++) bucket = i + 1;
                counts[Math.Min(bucket, Buckets.Length - 1)]++;
                steady++;
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
            _ = text.Line($"{N(f.At, "F1"),7} s  {N(f.DtMs, "F1"),7} ms   GC {N(f.GcMs, "F1")}  JIT {N(f.JitMs, "F1")}  " +
                          $"{text.T("hud-dbg-runqueue")} {N(f.RunQueueMs, "F1")}  Lock {N(LockIn(input.Events, f), "F1")}  " +
                          $"{text.T("hud-dbg-outside")} {N(f.OutsideMs, "F1")}  {f.AllocKb} KiB{Gen(f.Gens)}");
            foreach (var (name, ms) in marks.Bounded(DebugFrames.Marks))
                if (name is not null) _ = text.Line($"{Mark(text, name),-40} {N(ms, "F1"),7} ms", 14);
        }

        if (Array.Exists(input.Causes, static c => c.Count > 0)) _ = text.Blank().Line(text.T("hud-dbg-causes"));
        foreach (var (name, count, max, average) in input.Causes.Bounded(MaxRows))
            if (count > 0) _ = text.Pair(Mark(text, name), $"{count,5} x   avg {N(average, "F1")} ms   max {N(max, "F1")} ms", 4);
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
        _ = text.Row("hud-dbg-alloc", $"{N(s.AllocMbPerSec, "F1")} MB/s   {text.T("hud-dbg-main-thread")} {N(s.MainAllocMbPerSec, "F1")} MB/s")
            .Row("hud-dbg-collections", $"gen0 {s.Gen0}   gen1 {s.Gen1}   gen2 {s.Gen2}")
            .Row("hud-dbg-gc-pause", $"{N(s.GcMs, "F1")} ms  ({N(Share(s.GcMs, s.Seconds * 1000), "F2")} %)   max {N(s.GcMaxMs, "F1")} ms")
            .Row("hud-dbg-heap", $"{Mb(info.HeapSizeBytes)} MB   {text.T("hud-dbg-committed")} {Mb(info.TotalCommittedBytes)} MB" +
                                 $"   {text.T("hud-dbg-fragmented")} {Mb(info.FragmentedBytes)} MB")
            .Row("hud-dbg-generations", Generations(info))
            .Row("hud-dbg-gc-mode", $"{(GCSettings.IsServerGC ? "server" : "workstation")}, {GCSettings.LatencyMode}, " +
                                    $"concurrent {AppContext.GetData("System.GC.Concurrent")?.ToString() ?? "default"}")
            .Row("hud-dbg-gc-last", $"#{info.Index} gen{info.Generation} {(info.Compacted ? "compacting" : "")}" +
                                    $"{(info.Concurrent ? " background" : "")}  promoted {Mb(info.PromotedBytes)} MB  " +
                                    $"pause {N(info.PauseDurations[0].TotalMilliseconds, "F1")} ms");
        var dearest = input.Frames.OrderByDescending(static f => f.AllocKb).Take(ShownAllocFrames).ToArray();
        _ = text.Line(text.T("hud-dbg-alloc-frames"));
        foreach (var frame in dearest.Bounded(ShownAllocFrames))
            _ = text.Line($"{N(frame.At, "F1"),7} s  {frame.AllocKb,8} KiB  ({text.T("hud-dbg-main-thread")} {frame.MainAllocKb} KiB)" +
                          $"  {N(frame.DtMs, "F1")} ms", 4);
    }

    private static string Mb(long bytes) => N(bytes / 1048576.0, "F0");

    private static string Generations(GCMemoryInfo info)
    {
        var sizes = info.GenerationInfo;
        if (!Assert(sizes.Length >= 3) || !Assert(sizes.Length <= 8)) return "";
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < Math.Min(sizes.Length, GenNames.Length); i++)
            _ = text.Append(GenNames[i]).Append(' ').Append(Mb(sizes[i].SizeAfterBytes)).Append("  ");
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
        _ = text.Row("hud-dbg-alloc-sampled", $"{N(e.Sampled * perSecond, "F1")} MB/s").Line(text.T("hud-dbg-alloc-threads"));
        foreach (var (thread, bytes) in First(e.Threads, ShownThreads).Bounded(ShownThreads))
            _ = text.Line($"{thread,-28} {N(bytes * perSecond, "F1"),7} MB/s  {N(Share(bytes, e.Sampled), "F0"),4} %", 4);
        _ = text.Line(text.T("hud-dbg-alloc-types"));
        foreach (var (thread, type, bytes) in First(e.Types, ShownTypes).Bounded(ShownTypes))
            _ = text.Line($"{Clip(type),-56} {N(bytes * perSecond, "F1"),7} MB/s  {thread}", 4);
        var main = LockMs(e);
        _ = text.Blank().Row("hud-dbg-locks-main", $"{N(main, "F1")} ms  ({N(main / Math.Max(1, s.Frames), "F2")} ms/frame)");
        foreach (var (owner, ms) in First(e.MainWaitsBy, ShownWaits).Bounded(ShownWaits))
            _ = text.Line($"{text.T("hud-dbg-locks-held-by")} {owner,-24} {N(ms, "F1"),8} ms", 4);
        _ = text.Line(text.T("hud-dbg-locks-threads"));
        foreach (var (thread, ms, count) in First(e.Waits, ShownWaits).Bounded(ShownWaits))
            _ = text.Line($"{thread,-28} {N(ms, "F1"),8} ms  {count,7} x", 4);
    }

    private static string Clip(string type) =>
        type.Length > 56 ? "…" + type[^55..] : type;

    // The main thread's lock waits over the capture
    internal static double LockMs(RuntimeEvents.Snapshot events)
    {
        if (!NotNull(events.MainWaits)) return 0;
        var sum = 0.0;
        foreach (var (_, ms) in events.MainWaits.Bounded(1 << 16)) sum += ms;
        return sum;
    }

    // Those that ended within the frame: (At - Dt, At]
    internal static double LockIn(RuntimeEvents.Snapshot events, DebugFrame frame)
    {
        if (!NotNull(events.MainWaits)) return 0;
        var (from, sum) = (frame.At - frame.DtMs / 1000f, 0.0);
        foreach (var (at, ms) in events.MainWaits.Bounded(1 << 16))
            if (at > from && at <= frame.At) sum += ms;
        return sum;
    }

    private static void WorldSection(DebugText text, DebugInput input)
    {
        Rows(text, "hud-dbg-s-world", input.World);
        _ = text.Row("hud-dbg-entities", DebugText.Int(input.EntityTotal));
        foreach (var (code, count) in First(input.Entities, ShownEntities).Bounded(ShownEntities)) _ = text.Pair(code, DebugText.Int(count), 4);
    }

    private static void ModsSection(DebugText text, DebugInput input)
    {
        Section(text, "hud-dbg-s-mods");
        _ = text.Line(text.T("hud-dbg-mod-times"));
        foreach (var (mod, ms, top) in input.ModTimes.Bounded(MaxRows))
        {
            _ = text.Pair(mod, $"{N(ms, "F3")} ms/frame", 4);
            foreach (var (name, detail) in top.Bounded(ModTimes.DetailCount)) _ = text.Pair(name, $"{N(detail, "F3")} ms", 8);
        }

        if (input.Scopes.Length > 0) _ = text.Blank().Line(text.T("hud-dbg-scopes"));
        foreach (var (name, ms, calls) in input.Scopes.Bounded(MaxRows))
            _ = text.Pair(name, $"{N(ms, "F2")} ms   {calls} x   {N(calls > 0 ? ms / calls * 1000 : double.NaN, "F1")} us/call", 4);
        var code = input.Mods.Count(static m => m.Kind == "Code");
        _ = text.Blank().Row("hud-dbg-mods-loaded", $"{input.Mods.Length}  ({code} code, {input.Mods.Length - code} content)");
        foreach (var (id, name, version, kind, source) in input.Mods.Bounded(HarmonyAudit.MaxLoadedMods))
            if (kind == "Code") _ = text.Pair(id, $"{version,-12} {name}  [{source}]", 4);
        var content = string.Join(", ", input.Mods.Where(static m => m.Kind != "Code").Select(static m => m.Id + " " + m.Version));
        if (content.Length > 0) _ = text.Line(text.T("hud-dbg-content-mods") + " " + content, 4);
    }

    private static void HarmonySection(DebugText text, HarmonyAudit audit)
    {
        Section(text, "hud-dbg-s-harmony");
        _ = text.Row("hud-dbg-patched", $"{audit.Methods.Length}   {text.T("hud-dbg-owners")} {audit.Owners.Length}" +
                                        $"   {text.T("hud-dbg-shared")} {audit.Shared}");
        foreach (var (owner, mod, methods) in First(audit.Owners, ShownMods).Bounded(ShownMods))
            _ = text.Pair(owner, $"{methods,5}  {mod}", 4);
        if (Array.Exists(audit.Methods, static m => m.Owners >= 2)) _ = text.Blank().Line(text.T("hud-dbg-shared-methods"));
        var shown = 0;
        foreach (var method in audit.Methods.Bounded(HarmonyAudit.MaxMethods))
        {
            if (method.Owners < 2 || ++shown > ShownMethods) continue;
            _ = text.Line($"{text.T("hud-dbg-risk-" + method.Risk.ToString().ToLowerInvariant()),-7} {method.Name}", 4);
            foreach (var link in method.Chain.Bounded(HarmonyAudit.MaxPatches))
                _ = text.Line($"{link.Kind,-10} {link.Owner,-24} prio {link.Priority,4}{(link.Skips ? "  " + text.T("hud-dbg-can-skip") : "")}" +
                              $"  {link.Method}", 12);
            if (method.Risk == PatchRisk.High) _ = text.Line(text.T(Effect(method)), 12);
        }

        if (shown > ShownMethods) _ = text.Line(text.T("hud-dbg-more-in-file", shown - ShownMethods, "harmony.txt"), 4);
    }

    internal static string Effect(PatchedMethod method) =>
        method.Chain.Where(static l => l.Kind == PatchKind.Transpiler).Select(static l => l.Owner).Distinct().Count() > 1
            ? "hud-dbg-effect-transpilers"
            : "hud-dbg-effect-skip";

    private static void LogSection(DebugText text, DebugExtra extra)
    {
        Section(text, "hud-dbg-s-log");
        _ = text.Row("hud-dbg-log-counts", $"{extra.LogErrors} / {extra.LogWarnings}");
        var from = Math.Max(0, extra.Log.Length - ShownLog);
        foreach (var line in extra.Log.AsSpan(from).Bounded(ShownLog)) _ = text.Line(line.Length > DebugText.MaxWidth * 2
            ? line[..(DebugText.MaxWidth * 2)] + " ..." : line, 4);
    }
}

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
        [("s-mode", ModeCode(input.Mode)), ("s-seconds", s.Seconds), ("s-backlog", input.TessBacklog), .. input.Scene];

    private static double ModeCode(string mode)
    {
        if (mode.StartsWith("spike", StringComparison.Ordinal)) return 2;
        return mode == "now" ? 1 : 0;
    }

    private static bool Differ(double before, double after) => Math.Abs(before - after) > 0.5;

    // Why the captures are not comparable; empty when they are. Older summaries are checked for what they hold.
    internal static string Unlike(System.Func<string, object[], string> t, Dictionary<string, double> previous, DebugInput input, DebugSummary s)
    {
        var now = Scene(input, s).ToDictionary(static n => n.Key, static n => n.Value, StringComparer.Ordinal);
        var reasons = new List<string>();
        bool Both(string key, out double before, out double after)
        {
            after = now.GetValueOrDefault(key, double.NaN);
            return previous.TryGetValue(key, out before) && double.IsFinite(before) && double.IsFinite(after);
        }

        string[] modes = ["record", "now", "spike"];
        if (Both("s-mode", out var m0, out var m1) && Differ(m0, m1) && (uint)m0 < modes.Length && (uint)m1 < modes.Length)
            reasons.Add(t("hud-dbg-unlike-mode", [modes[(int)m0], modes[(int)m1]]));
        if (Both("s-seconds", out var t0, out var t1) && Math.Min(t0, t1) < ShortSeconds)
            reasons.Add(t("hud-dbg-unlike-short", [N(t0, "F1"), N(t1, "F1")]));
        if (Both("s-backlog", out var b0, out var b1) && Math.Max(b0, b1) > LoadingBacklog)
            reasons.Add(t("hud-dbg-unlike-loading", [N(b0, "F0"), N(b1, "F0")]));
        if (Both("s-vd", out var v0, out var v1) && Differ(v0, v1)) reasons.Add(t("hud-dbg-unlike-vd", [N(v0, "F0"), N(v1, "F0")]));
        if (Both("s-pixels", out var p0, out var p1) && Math.Abs(p0 - p1) > 0.01 * Math.Max(p0, p1))
            reasons.Add(t("hud-dbg-unlike-window", [N(p0 / 1e6, "F1"), N(p1 / 1e6, "F1")]));
        if (Both("s-renderer", out var r0, out var r1) && Differ(r0, r1)) reasons.Add(t("hud-dbg-unlike-renderer", []));
        if (Both("s-tris", out var g0, out var g1) && Math.Min(g0, g1) > 0 && Math.Max(g0, g1) / Math.Min(g0, g1) >= TrianglesRatio)
            reasons.Add(t("hud-dbg-unlike-tris", [N(g0 / 1e6, "F0"), N(g1 / 1e6, "F0")]));
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
        if (Unlike(text.T, previous, input, s) is { Length: > 0 } unlike) _ = text.Line(text.T("hud-dbg-compare-unlike", unlike));
        foreach (var (key, now) in Numbers(s).Bounded(MaxRows))
        {
            if (!previous.TryGetValue(key, out var before) || !double.IsFinite(before) || !double.IsFinite(now)) continue;
            var change = Math.Abs(before) > 0 ? 100 * (now - before) / Math.Abs(before) : double.NaN;
            var shown = double.IsFinite(change) ? change.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " %" : "";
            _ = text.Row("hud-dbg-n-" + key, $"{N(before, "F2"),10} -> {N(now, "F2"),10}   {shown}");
        }
    }
}
