using Komet.Core;
using Newtonsoft.Json.Linq;

namespace Komet.Options;

// The player's knob values, his preset and which knobs he set himself. komet.json holds them beside the HUD's display
// settings: HudSettings is the file's object and hands Preset, Chosen, Profiled and the knob keys through to this. The
// file holds only the knobs the player set; a knob it leaves out follows KnobDefault, a key written into it by hand counts
// as set.
internal sealed class KometSettings
{
    private static readonly int[] KnobDefaults = Knobs.Snapshot(builtIn: true);
    private const string CullingKnob = "OcclusionCulling";
    public const int Layout = 2; // komet.json's layout, Profiled

    private static string _preset = Presets.Auto; // the loaded settings' (one instance a client)

    // The knobs, as top-level keys of the file (HudSettings' extension data)
    private readonly Dictionary<string, JToken> _json = [];

    // The player's choice; the bench writes the statics, not these
    private readonly int[] _knobs = Followed();

    public event Action? Changed;

    internal Dictionary<string, JToken> Json => _json;

    // The value a knob the player never set follows: what this computer needs (HardwareProfile), else the preset's,
    // else Komet's default
    internal static int KnobDefault(int knob)
    {
        if (!Index(knob, KnobDefaults.Length)) return 0;
        var key = Knobs.At(knob).Key;
        var suggested = HardwareProfile.Suggestion(key) ?? Presets.Value(_preset, key);
        return suggested is { } value && Knobs.InRange(knob, value) ? value : KnobDefaults[knob];
    }

    private static int[] Followed()
    {
        var values = new int[KnobDefaults.Length];
        for (var i = 0; i < Math.Min(values.Length, Knobs.MaxKnobs); i++) values[i] = KnobDefault(i);
        return values;
    }

    // Presets: auto, quality, balanced or speed
    public string Preset
    {
        get => _preset;
        set
        {
            if (!Presets.Known(value) || value == _preset) return;
            Activate(value);
            Changed?.Invoke();
        }
    }

    private static void Activate(string preset)
    {
        _preset = preset;
        PresetChanged?.Invoke();
    }

    // The preset in use (auto, quality, balanced or speed) and its changes: mod adapters follow it
    public static string Current => _preset;

    public static event Action? PresetChanged;

    // A preset chosen: the knobs it governs follow it again, every knob not set by the player takes its value
    public void ChoosePreset(string name)
    {
        if (!Presets.Known(name)) return;
        Preset = name;
        _ = Chosen.RemoveAll(static key => Presets.Governs(key));
        for (var i = 0; i < Math.Min(_knobs.Length, Knobs.MaxKnobs); i++)
            if (!Chosen.Contains(Knobs.At(i).Persisted))
                SetKnob(i, KnobDefault(i));
    }

    // The player set a knob the preset governs: the preset shows as adjusted
    public bool Adjusted => Chosen.Exists(static key => Presets.Governs(key));

    // Knobs the player set (saved keys); the others follow KnobDefault, so the detected values move with the hardware
    public List<string> Chosen { get; } = [];

    // The file's layout. 0: before Chosen, every knob saved (its values that differ from Komet's default and from what it
    // follows count as chosen once); a file without the key reads as 0 (Reading), so does the bench's. 1: every knob
    // saved, the chosen ones listed in Chosen. 2 (Layout): only the chosen knobs saved, each saved key is chosen (Chosen
    // still written for older builds).
    public int Profiled { get; set; } = Layout;

    // The file is about to be read into these
    internal void Reading()
    {
        Profiled = 0;
    }

    // Every knob back to what suits this computer (Komet's default where nothing is suggested)
    public void UseDetected()
    {
        for (var i = 0; i < Math.Min(_knobs.Length, Knobs.MaxKnobs); i++) SetKnob(i, KnobDefault(i));
        Chosen.Clear();
    }

    public int Knob(int knob) =>
        Index(knob, _knobs.Length) ? _knobs[knob] : 0;

    public void SetKnob(int knob, int value)
    {
        if (!Index(knob, _knobs.Length) || !Knobs.InRange(knob, value)) return;
        var key = Knobs.At(knob).Persisted;
        if (value == KnobDefault(knob)) _ = Chosen.Remove(key); // back to following the computer
        else if (!Chosen.Contains(key)) Chosen.Add(key);
        _ = Knobs.Write(knob, value);
        if (_knobs[knob] != value)
        {
            _knobs[knob] = value;
            Changed?.Invoke();
        }

        Features.Poll();
    }

    // The values read from the file (or the defaults) written to the knobs; a key out of range is logged and its default
    // applies
    internal void Apply(ILogger logger, string fileName)
    {
        var knobs = Knobs.BuiltIn;
        List<string> rejected = [];
        if (Profiled >= Layout) Chosen.Clear(); // the saved keys say it
        RendererMigrated();
        CullingMigrated();
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
        {
            var key = knobs[i].Persisted;
            var value = KnobDefault(i);
            var saved = int.MinValue;
            if (_json.TryGetValue(key, out var token) && !TryKnob(token, i, out saved)) rejected.Add(key);
            else if (token is not null && Kept(key, i, saved, value))
            {
                value = saved;
                if (!Chosen.Contains(key)) Chosen.Add(key);
            }

            _knobs[i] = value;
            _ = Knobs.Write(i, value);
        }

        _json.Clear();
        HashSet<string> known = [];
        foreach (var knob in knobs.Bounded(Knobs.MaxKnobs)) _ = known.Add(knob.Persisted);
        _ = Chosen.RemoveAll(key => !known.Contains(key)); // knobs since gone
        Profiled = Layout;
        if (rejected.Count > 0 && NotNull(logger))
            logger.Warning("Komet HUD: {0} holds values out of range for {1}, their defaults apply", fileName,
                string.Join(", ", rejected));
    }

    // Whether a saved value is the player's, by the file's layout (Profiled)
    private bool Kept(string key, int knob, int saved, int followed)
    {
        if (!Assert(Profiled >= 0)) return false;
        return Profiled switch
        {
            0 => Chosen.Contains(key) || (saved != KnobDefaults[knob] && saved != followed),
            1 => Chosen.Contains(key),
            _ => true // layout 2, or a newer one: each saved key is chosen
        };
    }

    // A file from before 3.0's renderer choice: Vulkan-only also needed VulkanCore, VulkanTerrain and VulkanScene, and
    // the hybrid (all on but VulkanOnly) is gone. Vulkan only where VulkanOnly was on with all its parts, else OpenGL
    private void RendererMigrated()
    {
        if (!_json.TryGetValue(VulkanMode.Knob, out var only) || !NotNull(only)) return;
        foreach (var part in (ReadOnlySpan<string>)["VulkanCore", "VulkanTerrain", "VulkanScene"])
            if (_json.TryGetValue(part, out var on) && on is JValue { Type: JTokenType.Boolean, Value: false })
            {
                _json[VulkanMode.Knob] = new JValue(false);
                return;
            }
    }

    // A file from before the culling level: OcclusionCulling was a switch, GpuTerrainCulling and FaceSorting two more.
    // Off stays 0; on is 2 unless the rows were off (1, the depth pyramid alone).
    private void CullingMigrated()
    {
        if (_json.TryGetValue(CullingKnob, out var culling) && culling is JValue { Type: JTokenType.Boolean, Value: bool on })
        {
            var rows = !_json.TryGetValue("GpuTerrainCulling", out var r) || r is not JValue { Type: JTokenType.Boolean, Value: false };
            var level = rows ? 2 : 1;
            _json[CullingKnob] = new JValue(on ? level : 0);
        }
        else if (_json.TryGetValue("GpuTerrainCulling", out var rowsOnly) && rowsOnly is JValue { Type: JTokenType.Boolean, Value: false })
            _json[CullingKnob] = new JValue(1);

        _ = _json.Remove("GpuTerrainCulling");
        _ = _json.Remove("FaceSorting");
    }

    // Switch as bool, number as int; anything not a long is out of range (BigInteger throws on a (long) cast).
    private static bool TryKnob(JToken token, int knob, out int value)
    {
        value = token switch
        {
            JValue { Type: JTokenType.Boolean, Value: bool on } => on ? 1 : 0,
            JValue { Type: JTokenType.Integer, Value: long number and >= int.MinValue and <= int.MaxValue } =>
                (int)number,
            _ => int.MinValue
        };
        return NotNull(token) && Knobs.InRange(knob, value);
    }

    // The file is about to be written: each knob the player set under its saved name, a switch as a bool
    internal void Writing()
    {
        var knobs = Knobs.BuiltIn;
        _json.Clear();
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (Chosen.Contains(knobs[i].Persisted))
                _json[knobs[i].Persisted] = knobs[i].IsSwitch ? new JValue(_knobs[i] != 0) : new JValue(_knobs[i]);
        Profiled = Layout;
    }
}

// One choice for the knobs a player weighs, quality against speed against loading; every other knob follows the computer
// (HardwareProfile) or Komet's default. "auto" is balanced, or speed on a weak computer. A knob the player sets himself
// (KometSettings.Chosen) keeps his value whatever the preset.
internal static class Presets
{
    public const string Auto = "auto";
    public static readonly string[] Names = [Auto, "quality", "balanced", "speed"];

    // The knob's preset values (Knob.Presets: quality, balanced, speed), null where no preset sets it
    private static PresetValues? Of(string key) =>
        NotNull(key) && Knobs.Find(key) is var knob and >= 0 ? Knobs.At(knob).Presets : null;

    public static bool Governs(string key) => Of(key) is not null;

    public static bool Known(string name) => NotNull(name) && Array.IndexOf(Names, name) >= 0;

    // A named preset's value for the knob, null where it sets none
    public static int? Value(string preset, string key)
    {
        if (Of(key) is not { } values || !Known(preset)) return null;
        var (quality, balanced, speed) = values;
        return Resolved(preset) switch
        {
            "quality" => quality,
            "speed" => speed,
            _ => Assert(balanced >= 0) ? balanced : 0
        };
    }

    public static string Resolved(string preset)
    {
        if (preset != Auto) return preset;
        return HardwareProfile.Weak ? "speed" : "balanced";
    }
}
