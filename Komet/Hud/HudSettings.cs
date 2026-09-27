using System.Runtime.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Komet.Hud;

[JsonConverter(typeof(StringEnumConverter))]
internal enum HudCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

internal readonly record struct HudRange(double Min, double Max, double Step, double Factor, string Unit)
{
    public bool Contains(double value) => Finite(value) && value >= Min && value <= Max;

    public string Text(double value) => Assert(Factor > 0) && Assert(Unit.Length < 8)
        ? (HudText.Format(value * Factor, "N0") + " " + Unit).TrimEnd()
        : "";
}

// Properties with a setter persist as JSON in ModConfig, the knobs as top-level keys beside them; every change raises Changed.
internal sealed class HudSettings
{
    private const string FileName = "komet-hud.json";

    // Pins are keyed by panel index, a file with more is not one Komet wrote
    private const int MaxPinned = 100, MaxPanels = 32;

    public const int SlowEvery = 4, LogScrollLines = 3, DoubleClickMs = 400;
    public const double PanelGap = 6, ScreenMargin = 8, SnapStep = 8, SnapDistance = 12;
    public static readonly HudRange OpacityRange = new(0, 1, 0.05, 100, "%"), ScaleRange = new(0.5, 2, 0.1, 100, "%");
    public static readonly HudRange IntervalRange = new(0.1, 1, 0.05, 1000, "ms"), BenchRange = new(5, 120, 5, 1, "s");

    // Before any file is loaded: the statics' own values, Komet's knobs alone
    private static readonly int[] KnobDefaults = Knobs.Snapshot(builtIn: true);

    [JsonExtensionData]
    private readonly Dictionary<string, JToken> _json = []; // the knobs, as top-level keys of the file

    // What the player chose; the bench writes the statics, not these
    private readonly int[] _knobs = (int[])KnobDefaults.Clone();

    public bool Visible { get; set => Set(ref field, value); }

    [JsonProperty] // private setter: Json.NET writes it only with the attribute
    public HudCorner Corner
    {
        get;
        private set { if (Assert(value is >= HudCorner.TopLeft and <= HudCorner.BottomRight)) Set(ref field, value); }
    }

    public double Opacity { get; set { if (Assert(OpacityRange.Contains(value))) Set(ref field, value); } } = 0.6;
    public double Interval { get; set { if (Assert(IntervalRange.Contains(value))) Set(ref field, value); } } = 0.25;
    public double BenchSeconds { get; set { if (Assert(BenchRange.Contains(value))) Set(ref field, value); } } = 30;
    public bool ShowGraph { get; set => Set(ref field, value); } = true;
    public bool ShowSystem { get; set => Set(ref field, value); } = true;
    public bool ShowPasses { get; set => Set(ref field, value); } = true;
    public bool ShowModTimes { get; set => Set(ref field, value); }
    // The rows behind the headline of each section, in every panel
    public bool Detail { get; set => Set(ref field, value); }
    public bool ShowMods { get; set => Set(ref field, value); }
    // Komet's own feature counters, two panels of them: apart from the mods and patches, which are a few rows
    public bool ShowCounters { get; set => Set(ref field, value); }
    public bool ShowLog { get; set => Set(ref field, value); }
    public bool ShowDebugLog { get; set => Set(ref field, value); }
    public bool UpdateCheck { get; set => Set(ref field, value); } // asks GitHub once per start, opt-in
    public bool UpdateAsked { get; set => Set(ref field, value); } // the opt-in dialog is shown until answered
    public Dictionary<int, double[]> Pinned { get; } = [];
    public double FontScale { get; set { if (Assert(ScaleRange.Contains(value))) Set(ref field, value); } } = 1.0;

    public event Action? Changed;

    private void Set<T>(ref T target, T value)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return;
        target = value;
        Changed?.Invoke();
    }

    // Raises Changed when anything changed: a click on the corner already chosen still clears the pins, which the window and the file
    // have to see
    public void SetCorner(HudCorner corner)
    {
        if (!Assert(corner is >= HudCorner.TopLeft and <= HudCorner.BottomRight) ||
            !Assert(Pinned.Count < MaxPinned)) return;
        var unpinned = Pinned.Count > 0;
        Pinned.Clear();
        if (Corner != corner) Corner = corner; // raises Changed
        else if (unpinned) NotifyChanged();
    }

    public void ResetPositions()
    {
        Pinned.Clear();
        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke();

    // The display, the panels and every knob; the update notice is a consent, not a display setting
    public void ResetDefaults()
    {
        var d = new HudSettings();
        (Corner, Opacity, FontScale, Interval, BenchSeconds) =
            (d.Corner, d.Opacity, d.FontScale, d.Interval, d.BenchSeconds);
        (ShowGraph, ShowSystem, ShowPasses, ShowModTimes, Detail, ShowMods, ShowCounters, ShowLog, ShowDebugLog) = (
            d.ShowGraph, d.ShowSystem, d.ShowPasses, d.ShowModTimes, d.Detail, d.ShowMods, d.ShowCounters, d.ShowLog,
            d.ShowDebugLog);
        for (var i = 0; i < Math.Min(_knobs.Length, Knobs.MaxKnobs); i++) SetKnob(i, KnobDefaults[i]);
        ResetPositions();
    }

    public int Knob(int knob) =>
        Index(knob, _knobs.Length) && Assert(_knobs.Length == Knobs.BuiltInCount) ? _knobs[knob] : 0;

    public void SetKnob(int knob, int value)
    {
        if (!Index(knob, _knobs.Length) || !Knobs.InRange(knob, value)) return;
        _ = Knobs.Write(knob, value);
        Set(ref _knobs[knob], value);
        Features.Poll();
    }

    public static HudSettings Load(ICoreClientAPI capi)
    {
        var settings = Read(capi);
        settings.ApplyKnobs(capi.Logger);
        return settings;
    }

    private static HudSettings Read(ICoreClientAPI capi)
    {
        try
        {
            var loaded = capi.LoadModConfig<HudSettings>(FileName) ?? new HudSettings();
            if (!Assert(loaded.Pinned.Count < MaxPinned)) return new HudSettings();
            loaded.DropBadPins(capi.Logger);
            return loaded;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} unreadable, using defaults ({1})", FileName, e.Message);
            return new HudSettings();
        }
    }

    // A pin Komet did not write: a key that is no panel index, or a position that is not two finite numbers
    private void DropBadPins(ILogger logger)
    {
        List<int> bad = [];
        foreach (var (index, at) in Pinned.Bounded(MaxPinned))
            if (index is < 0 or >= MaxPanels || at is not [var x, var y] || !double.IsFinite(x) || !double.IsFinite(y))
                bad.Add(index);
        foreach (var index in bad.Bounded(MaxPinned)) _ = Pinned.Remove(index);
        if (bad.Count > 0 && NotNull(logger))
            logger.Warning("Komet HUD: {0} dropped {1} unreadable panel positions", FileName, bad.Count);
    }

    // Every knob reaches its static (unless held), the file's value or the default; keys of knobs that no longer exist are dropped
    internal void ApplyKnobs(ILogger logger)
    {
        var knobs = Knobs.BuiltIn;
        List<string> rejected = [];
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
        {
            var value = KnobDefaults[i];
            if (_json.TryGetValue(knobs[i].Persisted, out var token) && !TryKnob(token, i, out value))
            {
                rejected.Add(knobs[i].Persisted);
                value = KnobDefaults[i];
            }

            _knobs[i] = value;
            _ = Knobs.Write(i, value);
        }

        _json.Clear();
        if (rejected.Count > 0 && NotNull(logger))
            logger.Warning("Komet HUD: {0} holds values out of range for {1}, their defaults apply", FileName,
                string.Join(", ", rejected));
    }

    // A switch is written as a bool, a number as an int; either reads as the other. Json.NET reads an integer beyond long as a
    // BigInteger, which a (long) cast throws on: anything but a long is out of range.
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

    [OnSerializing]
    private void Serializing(StreamingContext context)
    {
        var knobs = Knobs.BuiltIn;
        _json.Clear();
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            _json[knobs[i].Persisted] = knobs[i].IsSwitch ? new JValue(_knobs[i] != 0) : new JValue(_knobs[i]);
        _ = Assert(_json.Count == _knobs.Length);
    }

    public void Save(ICoreClientAPI capi)
    {
        if (!NotNull(capi)) return;
        try
        {
            capi.StoreModConfig(this, FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} not written ({1})", FileName, e.Message);
        }
    }

    public void RegisterHotkeys(ICoreClientAPI capi)
    {
        if (!NotNull(capi) || !NotNull(capi.Input)) return;
        Hotkey(capi, "toggle", false, () => Visible = !Visible);
        Hotkey(capi, "detail", true, () => Detail = !Detail);
    }

    private static void Hotkey(ICoreClientAPI capi, string name, bool shift, Func<bool> toggle)
    {
        if (!Assert(name.Length > 0)) return;
        var code = "komet-hud-" + name;
        capi.Input.RegisterHotKey(code, HudText.Translate("hotkey-hud-" + name), GlKeys.F7, HotkeyType.HelpAndOverlays,
            shiftPressed: shift);
        capi.Input.SetHotKeyHandler(code, _ =>
        {
            capi.ShowChatMessage(HudText.Translate($"hud-{name}-{(toggle() ? "on" : "off")}"));
            return true;
        });
    }
}
