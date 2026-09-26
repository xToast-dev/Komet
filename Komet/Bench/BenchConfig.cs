using System.Globalization;
using System.Text.Json;

namespace Komet.Bench;

internal readonly record struct BenchSetting(int Knob, int Value);

// Engine: every knob starts at Knobs.EngineValues() instead of the player's values; Settings apply on top either way
internal readonly record struct BenchArm(string Name, bool Engine, BenchSetting[] Settings);

// bench.json as scripts/bench.sh flattens it: one profile's overrides of the defaults below. Every list has a cap and every number
// a range, so a typo fails the run at load instead of an hour later, and an unknown key is an error rather than a silently ignored
// wish. Keys starting with an underscore are comments. "sandbox" belongs to the script; "revision" and "env" (what bench.sh built
// and ran the game with) are only echoed into the result.
internal sealed class BenchConfig
{
    public const int MaxBytes = 1 << 20, MaxDepth = 16, MaxArms = 8, MaxLaps = 64, MaxKeys = 64, MaxText = 512;

    // One frame of BenchDriver.MaxStep stays under the 128 blocks EntityPos.SetFromPacket accepts
    public const double MaxSpeed = 30;

    private static readonly string[] TopKeys =
    [
        "name", "output", "modDir", "revision", "env", "world", "settle", "route", "lap", "laps", "warmupLaps", "arms",
        "sandbox"
    ];

    public JsonElement Raw { get; private init; }
    public string Name { get; private set; } = "bench";
    public string Output { get; private set; } = "";
    public string ModDir { get; private set; } = "";
    public string SavegameId { get; private set; } = "";

    public double Settle { get; private set; } =
        150; // after the climb; the queue drains in about 54-75 s at view distance 1536

    public double LapSettle { get; private set; } = 10;
    public double Speed { get; private set; } = 11;
    public double Still { get; private set; } = 10;
    public double Rotate { get; private set; } = 20;
    public double Leg { get; private set; } = 40;
    public double Turn { get; private set; } = 2;
    public int Laps { get; private set; } = 8;
    public int WarmupLaps { get; private set; } = -1; // one per arm unless set
    public IReadOnlyList<BenchArm> Arms { get; private set; } = [];
    public string Frames => Path.Combine(Path.GetDirectoryName(Output) ?? "", "frames.csv");
    public string Status => Output + ".status";

    public static BenchConfig Parse(string json)
    {
        if (!NotNull(json) || json.Length > MaxBytes)
            throw new InvalidDataException($"bench.json: missing or over {MaxBytes} bytes");
        var options = new JsonDocumentOptions
        { MaxDepth = MaxDepth, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var document = Document(json, options);
        var root = document.RootElement;
        Keys(root, "", TopKeys);
        var config = new BenchConfig { Raw = root.Clone() };
        config.Read(root);
        config.Arms = ReadArms(root);
        var arms = config.Arms.Count;
        if (config.WarmupLaps < 0) config.WarmupLaps = arms;
        if (!Assert(arms is > 0 and <= MaxArms) || config.Laps % arms != 0)
            throw Bad("laps",
                string.Create(CultureInfo.InvariantCulture, $"a multiple of the {arms} arms: whole mirrored blocks"));
        return config;
    }

    private static JsonDocument Document(string json, JsonDocumentOptions options)
    {
        if (!Assert(options.MaxDepth > 0) || !NotNull(json)) throw new InvalidDataException("bench.json: no document");
        try
        {
            return JsonDocument.Parse(json, options);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("bench.json: " + e.Message, e);
        }
    }

    // Speeds are capped so that one frame of up to BenchDriver.MaxStep seconds never moves the player past the server's step check
    private void Read(JsonElement root)
    {
        if (!Assert(root.ValueKind == JsonValueKind.Object) || !Assert(Raw.ValueKind == JsonValueKind.Object)) return;
        Name = Text(root, "name", "", false) is { Length: > 0 } name ? name : Name;
        Output = FullPath(root, "output", true);
        ModDir = FullPath(root, "modDir", true);
        _ = Text(root, "revision", "", false);
        Env(root);
        SavegameId = Text(Section(root, "world", ["save", "savegameId"]), "savegameId", "world.", true);
        var settle = Section(root, "settle", ["seconds", "lapSeconds"]);
        Settle = Number(settle, "seconds", "settle.", Settle, 0, 3600);
        LapSettle = Number(settle, "lapSeconds", "settle.", LapSettle, 0, 600);
        Speed = Number(Section(root, "route", ["speed"]), "speed", "route.", Speed, 0.5, MaxSpeed);
        var lap = Section(root, "lap", ["still", "rotate", "leg", "turn"]);
        Still = Number(lap, "still", "lap.", Still, 0, 600);
        Rotate = Number(lap, "rotate", "lap.", Rotate, 0, 600);
        Leg = Number(lap, "leg", "lap.", Leg, 0, 600);
        Turn = Number(lap, "turn", "lap.", Turn, 0.1, 60);
        Laps = Integer(root, "laps", Laps, 1, MaxLaps);
        WarmupLaps = Integer(root, "warmupLaps", WarmupLaps, 0, MaxArms);
    }

    private static void Env(JsonElement root)
    {
        if (!Get(root, "env", out var env)) return;
        if (env.ValueKind != JsonValueKind.Object) throw Bad("env", "an object of NAME: \"value\" strings");
        foreach (var entry in env.EnumerateObject().Bounded(MaxKeys))
            if (entry.Value.ValueKind != JsonValueKind.String)
                throw Bad("env." + entry.Name, "a string");
    }

    private static List<BenchArm> ReadArms(JsonElement root)
    {
        if (!Assert(root.ValueKind == JsonValueKind.Object)) return [];
        if (!Get(root, "arms", out var arms)) return [new BenchArm("A", false, [])];
        if (arms.ValueKind != JsonValueKind.Array || arms.GetArrayLength() is < 1 or > MaxArms)
            throw Bad("arms", $"a list of 1 to {MaxArms} arms");
        List<BenchArm> list = [];
        foreach (var arm in arms.EnumerateArray().Bounded(MaxArms))
        {
            var parsed = ReadArm(arm, list.Count);
            if (list.Exists(other => other.Name == parsed.Name))
                throw Bad("arms", $"unique names, {parsed.Name} twice");
            list.Add(parsed);
        }

        return list;
    }

    private static BenchArm ReadArm(JsonElement arm, int index)
    {
        if (!Index(index, MaxArms) || !Assert(Knobs.Count <= Knobs.MaxKnobs)) throw Bad("arms", "fewer arms");
        var path = string.Create(CultureInfo.InvariantCulture, $"arms[{index}].");
        if (arm.ValueKind != JsonValueKind.Object) throw Bad(path.TrimEnd('.'), "an object");
        Keys(arm, path, ["name", "engine", "set"]);
        var engine = false;
        if (Get(arm, "engine", out var flag))
            engine = flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean()
                : throw Bad(path + "engine", "true or false");
        List<BenchSetting> settings = [];
        if (Get(arm, "set", out var set))
        {
            if (set.ValueKind != JsonValueKind.Object) throw Bad(path + "set", "an object of Knobs names");
            foreach (var entry in set.EnumerateObject().Bounded(MaxKeys))
            {
                var setting = Setting(entry, path + "set.");
                if (settings.Exists(other => other.Knob == setting.Knob))
                    throw Bad(path + "set." + entry.Name, "named once");
                settings.Add(setting);
            }
        }

        return new BenchArm(Text(arm, "name", path, true), engine, [.. settings]);
    }

    private static BenchSetting Setting(JsonProperty entry, string path)
    {
        var knob = Knobs.Find(entry.Name);
        if (!Assert(path.Length > 0) || knob < 0)
            throw Bad(path + entry.Name, "one of the names in Komet/Core/Knobs.cs");
        var value = entry.Value.ValueKind switch
        {
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.Number when entry.Value.TryGetInt32(out var number) => number,
            _ => throw Bad(path + entry.Name, "true, false or a whole number")
        };
        return Knobs.InRange(knob, value) && Index(knob, Knobs.Count)
            ? new BenchSetting(knob, value)
            : throw Bad(path + entry.Name, "a value inside the knob's range");
    }

    private static InvalidDataException Bad(string key, string expected)
    {
        _ = Assert(key.Length > 0) && Assert(expected.Length > 0);
        return new InvalidDataException($"bench.json: {key} must be {expected}");
    }

    private static void Keys(JsonElement obj, string path, string[] allowed)
    {
        if (!Assert(allowed.Length > 0) || !NotNull(path)) return;
        if (obj.ValueKind != JsonValueKind.Object)
            throw Bad(path.Length == 0 ? "the document" : path.TrimEnd('.'), "an object");
        var unknown = obj.EnumerateObject().Bounded(MaxKeys)
            .FirstOrDefault(property => !property.Name.StartsWith('_') && Array.IndexOf(allowed, property.Name) < 0);
        if (unknown.Value.ValueKind != JsonValueKind.Undefined)
            throw new InvalidDataException($"bench.json: unknown key {path}{unknown.Name}");
    }

    // A missing section reads as an empty object, so every key in it keeps its default
    private static JsonElement Section(JsonElement obj, string key, string[] allowed)
    {
        if (!Assert(key.Length > 0) || !Get(obj, key, out var section)) return default;
        Keys(section, key + ".", allowed);
        return section;
    }

    private static bool Get(JsonElement obj, string key, out JsonElement value)
    {
        value = default;
        if (!Assert(key.Length > 0) ||
            !Assert(obj.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined)) return false;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value) &&
               value.ValueKind != JsonValueKind.Null;
    }

    private static double Number(JsonElement obj, string key, string path, double fallback, double min, double max)
    {
        if (!Assert(min <= max) || !Finite(fallback) || !Get(obj, key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Bad(path + key, "a number");
        return number >= min && number <= max
            ? number
            : throw Bad(path + key, string.Create(CultureInfo.InvariantCulture, $"between {min} and {max}"));
    }

    private static int Integer(JsonElement obj, string key, int fallback, int min, int max)
    {
        if (!Assert(min <= max) || !Assert(fallback >= min || fallback < 0) || !Get(obj, key, out var value))
            return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw Bad(key, "a whole number");
        return number >= min && number <= max
            ? number
            : throw Bad(key, string.Create(CultureInfo.InvariantCulture, $"between {min} and {max}"));
    }

    private static string Text(JsonElement obj, string key, string path, bool required)
    {
        if (!Assert(key.Length > 0) || !NotNull(path) || !Get(obj, key, out var value))
            return required ? throw Bad(path + key, "set") : "";
        var text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : throw Bad(path + key, "a string");
        return text.Length is > 0 and <= MaxText
            ? text
            : throw Bad(path + key, $"a string of 1 to {MaxText} characters");
    }

    private static string FullPath(JsonElement obj, string key, bool required)
    {
        var text = Text(obj, key, "", required);
        if (!Assert(key.Length > 0) || !Assert(text.Length <= MaxText) || text.Length == 0) return text;
        if (!Path.IsPathFullyQualified(text) ||
            text.Contains('\0', StringComparison.Ordinal)) // GetFullPath throws on a NUL
            throw Bad(key, "an absolute path");
        return Path.GetFullPath(text);
    }
}
