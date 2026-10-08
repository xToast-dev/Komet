namespace Komet.Hud;

// Everything a protocol reports that only the main thread may read, copied when the capture ends. A row's label is a lang key, a
// client setting's is the setting's own name. The pool thread adds Harmony, the log and the previous protocol.
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
}

// The pool thread's half: the full registry, the log's warnings and errors since the capture began, the last protocol's numbers
internal sealed record DebugExtra(HarmonyAudit Audit, string[] Log, int LogErrors, int LogWarnings, Dictionary<string, double>? Previous,
    string PreviousStamp);
