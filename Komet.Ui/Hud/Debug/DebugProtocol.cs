using Komet.Api;
using Komet.Diagnostics;

namespace Komet.Hud;

// Report of everything Komet measures (DebugReport: its sections, or as text), in sections findings point into; built
// on a pool thread. Findings:
// what stands out, worst first; coarse thresholds say where to look, the section how bad.
internal static partial class DebugProtocol
{
    public const int MaxRows = 512;
    private const int MaxFindings = 16;
    private const double ModMsMedium = 2, ModMsHigh = 5, AllocMedium = 200, AllocHigh = 500;

    // A finding refers to its section by index + 1
    internal static readonly string[] Sections =
    [
        "hud-dbg-s-findings", "hud-dbg-s-capture", "hud-dbg-s-system", "hud-dbg-s-game", "hud-dbg-s-komet",
        "hud-dbg-s-frames",
        "hud-dbg-s-spikes", "hud-dbg-s-memory", "hud-dbg-s-alloc", "hud-dbg-s-runtime", "hud-dbg-s-world",
        "hud-dbg-s-render", "hud-dbg-s-mods",
        "hud-dbg-s-harmony", "hud-dbg-s-log", "hud-dbg-s-hud", "hud-dbg-s-compare", "hud-dbg-s-files"
    ];

    public static DebugReport Report(DebugInput input, DebugExtra extra, DebugSummary summary, string[] files)
    {
        if (!NotNull(input) || !NotNull(extra) || !NotNull(files)) return DebugReport.Empty;

        var text = new DebugText(input.Code);
        Head(text, input, summary);
        var findings = Findings(input, extra, summary);
        Section(text, "hud-dbg-s-findings");
        foreach (var finding in findings.Bounded(MaxFindings))
        {
            _ = text.Line($"{text.T("hud-dbg-level-" + finding.Level.ToString().ToLowerInvariant()),-7} " +
                          $"{text.T(finding.Key, finding.Args)}  > {Number(finding.Where)}");
        }

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
        _ = text.Line(input.HudMeans.Length > 0
            ? input.HudMeans.Replace("\n", "\n  ", StringComparison.Ordinal)
            : text.T("hud-dbg-none"));
        CompareSection(text, extra, input, summary);
        Section(text, "hud-dbg-s-files");
        foreach (var file in files.Bounded(MaxRows))
        {
            _ = text.Line(file);
        }

        ExtraSections(text, input);
        return text.Report.Map(Anonymize);
    }

    private static int Number(string section) =>
        Array.IndexOf(Sections, section) is var at && Index(at, Sections.Length) ? at + 1 : 0;

    private static void Section(DebugText text, string key)
    {
        if (!Assert(Number(key) > 0)) return;

        _ = text.Section(Number(key), key);
    }

    private static void Rows(DebugText text, string section, (string Key, string Value)[] rows)
    {
        Section(text, section);
        foreach (var (key, value) in rows.Bounded(MaxRows))
        {
            _ = text.Row(key, value);
        }
    }

    private static void Head(DebugText text, DebugInput input, DebugSummary summary)
    {
        _ = text.Head(text.T("hud-dbg-title"))
            .Head(
                $"Komet {input.Komet} | Vintage Story {GameVersion.OverallVersion} | {input.Started:yyyy-MM-dd HH:mm:ss zzz}")
            .Head(text.T("hud-dbg-head", input.Mode, DebugText.Num(summary.Seconds, "F1"), summary.Frames,
                DebugText.Num(summary.Fps, "F1"), DebugText.Num(summary.Low1Fps, "F1"),
                DebugText.Num(summary.WorstMs, "F1")));
        if (input.Trigger.Length > 0) _ = text.Head(input.Trigger);
    }

    internal static double Share(double part, double whole) =>
        Assert(part >= 0 || double.IsNaN(part)) && whole > 0 ? 100 * part / whole : double.NaN;

    private static string N(double value, string format) => DebugText.Num(value, format);

    private static double PerMinute(int count, double seconds) =>
        Finite(seconds) && seconds > 0 ? count * 60.0 / seconds : double.NaN;

    private static ReadOnlySpan<T> First<T>(T[] items, int count) => items.AsSpan(0, Math.Min(items.Length, count));

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
            _ = text.Titled(++number, title).Line(body.Replace("\n", "\n  ", StringComparison.Ordinal));
        }
    }

    private static Finding[] Findings(DebugInput input, DebugExtra extra, DebugSummary s)
    {
        var list = new List<Finding>();
        Performance(list, s);
        Context(list, input, extra);
        if (extra.Previous is { } previous && Unlike(static (key, _) => key, previous, input, s).Length > 0)
        {
            list.Add(new Finding(Severity.Info, "hud-dbg-s-compare", "hud-dbg-f-unlike", []));
        }

        if (list.Count == 0) list.Add(new Finding(Severity.Info, "hud-dbg-s-capture", "hud-dbg-f-none", []));

        list.Sort(static (a, b) => a.Level.CompareTo(b.Level));
        return [.. list.Take(MaxFindings)];
    }

    private static void Performance(List<Finding> list, DebugSummary s)
    {
        if (s.Frames == 0) return;

        var gc = Share(s.GcMs, s.Seconds * 1000);
        if (gc >= 2)
        {
            list.Add(new Finding(gc >= 5 ? Severity.High : Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-gc",
                [N(gc, "F1"), s.Gen0, N(s.GcMaxMs, "F1")]));
        }

        if (s.Gen2 > 0) list.Add(new Finding(Severity.Medium, "hud-dbg-s-memory", "hud-dbg-f-gen2", [s.Gen2]));

        if (s.AllocMbPerSec >= AllocMedium)
        {
            list.Add(new Finding(s.AllocMbPerSec >= AllocHigh ? Severity.High : Severity.Medium, "hud-dbg-s-memory",
                "hud-dbg-f-alloc",
                [N(s.AllocMbPerSec, "F0"), N(Share(s.MainAllocMbPerSec, s.AllocMbPerSec), "F0")]));
        }

        if (s.JitMs >= 50)
        {
            list.Add(new Finding(s.JitMs >= 500 ? Severity.Medium : Severity.Low, "hud-dbg-s-runtime", "hud-dbg-f-jit",
                [N(s.JitMs, "F0")]));
        }

        if (s.WorstMs >= 100)
        {
            list.Add(new Finding(Severity.High, "hud-dbg-s-spikes", "hud-dbg-f-worst", [N(s.WorstMs, "F0")]));
        }

        if (Share(s.Over50, s.Steady) >= 1)
        {
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-frames", "hud-dbg-f-over50",
                [s.Over50, N(Share(s.Over50, s.Steady), "F1")]));
        }

        if (s.Low1Fps < 30)
        {
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-frames", "hud-dbg-f-low", [N(s.Low1Fps, "F0")]));
        }

        if (s.RunQueueMs >= 1)
        {
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-runtime", "hud-dbg-f-runqueue",
                [N(s.RunQueueMs, "F1"), N(s.RunQueueMaxMs, "F0")]));
        }
    }

    private static void Context(List<Finding> list, DebugInput input, DebugExtra extra)
    {
        foreach (var (mod, ms, _) in input.ModTimes.Bounded(MaxRows))
        {
            if (ms >= ModMsMedium)
            {
                list.Add(new Finding(ms >= ModMsHigh ? Severity.High : Severity.Medium, "hud-dbg-s-mods",
                    "hud-dbg-f-mod", [mod, N(ms, "F2")]));
            }
        }

        var risky = extra.Audit.Methods.Count(static m => m.Risk == PatchRisk.High);
        if (risky > 0) list.Add(new Finding(Severity.Medium, "hud-dbg-s-harmony", "hud-dbg-f-harmony", [risky]));

        if (extra.LogErrors > 0)
        {
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-log", "hud-dbg-f-log",
                [extra.LogErrors, extra.LogWarnings]));
        }

        if (input.VramPercent >= 90)
        {
            list.Add(new Finding(Severity.High, "hud-dbg-s-render", "hud-dbg-f-vram", [N(input.VramPercent, "F0")]));
        }

        if (input.RamFreePercent < 10)
        {
            list.Add(new Finding(Severity.Medium, "hud-dbg-s-system", "hud-dbg-f-ram",
                [N(input.RamFreePercent, "F0")]));
        }

        if (input.TessBacklog > 500)
        {
            list.Add(new Finding(Severity.Low, "hud-dbg-s-world", "hud-dbg-f-backlog", [input.TessBacklog]));
        }

        foreach (var feature in input.Features.Bounded(MaxRows))
        {
            if (feature.State is KapiFeatureState.Failed or KapiFeatureState.StoodDown
                or KapiFeatureState.EngineChanged)
            {
                list.Add(
                    new Finding(Severity.Info, "hud-dbg-s-komet", "hud-dbg-f-feature", [feature.Id, feature.State]));
            }
        }
    }

    internal enum Severity
    {
        High,
        Medium,
        Low,
        Info
    }

    internal readonly record struct Finding(Severity Level, string Where, string Key, object[] Args);
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
    public (string Title, string Text)[] Extra { get; init; } = [];
    public required string HudMeans { get; init; }
    public required (string Key, string Value)[] System { get; init; }
    public required (string Key, string Value)[] Runtime { get; init; }
    public required (string Key, string Value)[] World { get; init; }
    public required (string Key, string Value)[] Render { get; init; }
    public required (string Name, string Value)[] Client { get; init; }
    public required (string Key, int Value, int Engine)[] Knobs { get; init; }
    public required KapiFeatureInfo[] Features { get; init; }
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
internal sealed record DebugExtra(
    HarmonyAudit Audit,
    string[] Log,
    int LogErrors,
    int LogWarnings,
    Dictionary<string, double>? Previous,
    string PreviousStamp);
