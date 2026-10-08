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

internal sealed class HudSettings
{
    private const string FileName = "komet-hud.json";

    public const int SlowEvery = 4, MaxPinnedMods = 3;
    public const double ScreenMargin = 8, SnapStep = 8;
    public static readonly HudRange OpacityRange = new(0, 1, 0.05, 100, "%"), ScaleRange = new(0.5, 2, 0.1, 100, "%");
    public static readonly HudRange IntervalRange = new(0.1, 1, 0.05, 1000, "ms"), BenchRange = new(5, 120, 5, 1, "s");
    public static readonly HudRange ToastRange = new(0, 200, 5, 1, "ms");

    // Before any file is loaded: the statics' own values, Komet's knobs alone
    private static readonly int[] KnobDefaults = Knobs.Snapshot(builtIn: true);

    [JsonExtensionData]
    private readonly Dictionary<string, JToken> _json = []; // the knobs, as top-level keys of the file

    // What the player chose; the bench writes the statics, not these
    private readonly int[] _knobs = (int[])KnobDefaults.Clone();

    // The compact overlay (F7); the window (Ctrl+F7) and the debug window (Ctrl+F8) are dialogs of their own
    public bool Visible { get; set => Set(ref field, value); }

    public HudCorner Corner
    {
        get;
        set { if (Assert(value is >= HudCorner.TopLeft and <= HudCorner.BottomRight)) Set(ref field, value); }
    }

    public double Opacity { get; set { if (Assert(OpacityRange.Contains(value))) Set(ref field, value); } } = 0.6;
    public double Interval { get; set { if (Assert(IntervalRange.Contains(value))) Set(ref field, value); } } = 0.25;
    public double BenchSeconds { get; set { if (Assert(BenchRange.Contains(value))) Set(ref field, value); } } = 30;
    // What the overlay shows besides frame rate and frame time
    public bool ShowFps { get; set => Set(ref field, value); } = true;
    public bool ShowLows { get; set => Set(ref field, value); } = true;
    public bool ShowFrametime { get; set => Set(ref field, value); } = true;
    public bool ShowGraph { get; set => Set(ref field, value); } = true;
    public bool ShowMods { get; set => Set(ref field, value); } = true;
    public bool ShowPins { get; set => Set(ref field, value); } = true;

    // A spike over this many ms shows in the overlay for a few seconds with its cause; 0: never
    public double ToastMs { get; set { if (Assert(ToastRange.Contains(value))) Set(ref field, value); } } = 50;

    // Mods the overlay shows by name (Mods tab: a click pins); none: the dearest
    public List<string> PinnedMods { get; } = [];
    // Where the window was left; plain properties: a drag must not end the HUD's interval on every move. Written with the rest.
    public string WindowTab { get; set; } = "";
    public double WindowX { get; set; } = double.NaN; // NaN: centred
    public double WindowY { get; set; } = double.NaN;
    public string DebugLanguage { get; set => Set(ref field, value is "de" ? "de" : "en"); } = "en";
    public bool UpdateCheck { get; set => Set(ref field, value); } // asks GitHub once per start, opt-in
    public bool UpdateAsked { get; set => Set(ref field, value); } // the opt-in dialog is shown until answered
    public double FontScale { get; set { if (Assert(ScaleRange.Contains(value))) Set(ref field, value); } } = 1.0;

    // The settings pages show every knob, not only KometPages.Basic
    public bool ShowAdvanced { get; set => Set(ref field, value); }

    public event Action? Changed;

    private void Set<T>(ref T target, T value)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return;
        target = value;
        Changed?.Invoke();
    }

    // Pinned again: unpinned; past MaxPinnedMods the oldest goes
    public void TogglePin(string mod)
    {
        if (string.IsNullOrEmpty(mod) || !Assert(PinnedMods.Count <= MaxPinnedMods)) return;
        if (!PinnedMods.Remove(mod))
        {
            if (PinnedMods.Count >= MaxPinnedMods) PinnedMods.RemoveAt(0);
            PinnedMods.Add(mod);
        }

        NotifyChanged();
    }

    // The window back to the middle of the screen
    public void ResetPositions() => (WindowX, WindowY) = (double.NaN, double.NaN);

    private void NotifyChanged() => Changed?.Invoke();

    // The display, the panels and every knob; the update notice is a consent, not a display setting
    public void ResetDefaults()
    {
        var d = new HudSettings();
        (Corner, Opacity, FontScale, Interval, BenchSeconds) =
            (d.Corner, d.Opacity, d.FontScale, d.Interval, d.BenchSeconds);
        (ShowFps, ShowLows, ShowFrametime, ShowGraph, ShowMods, ShowPins, ToastMs) =
            (d.ShowFps, d.ShowLows, d.ShowFrametime, d.ShowGraph, d.ShowMods, d.ShowPins, d.ToastMs);
        PinnedMods.Clear();
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
            if (loaded.PinnedMods.Count > MaxPinnedMods) loaded.PinnedMods.RemoveRange(0, loaded.PinnedMods.Count - MaxPinnedMods);
            return Assert(loaded.PinnedMods.Count <= MaxPinnedMods) ? loaded : new HudSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} unreadable, using defaults ({1})", FileName, e.Message);
            return new HudSettings();
        }
    }

    // Keys of knobs that no longer exist are dropped
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

    // F7 the overlay, Ctrl+F7 the window, Ctrl+F8 the debug window
    public void RegisterHotkeys(ICoreClientAPI capi, Action window, Action debug)
    {
        if (!NotNull(capi) || !NotNull(capi.Input) || !NotNull(window) || !NotNull(debug)) return;
        Hotkey(capi, "toggle", GlKeys.F7, false, () =>
        {
            Visible = !Visible;
            capi.ShowChatMessage(HudText.Translate($"hud-toggle-{(Visible ? "on" : "off")}"));
        });
        Hotkey(capi, "window", GlKeys.F7, true, window);
        Hotkey(capi, "debug", GlKeys.F8, true, debug);
    }

    private static void Hotkey(ICoreClientAPI capi, string name, GlKeys key, bool ctrl, Action act)
    {
        if (!Assert(name.Length > 0) || !NotNull(act)) return;
        var code = "komet-hud-" + name;
        capi.Input.RegisterHotKey(code, HudText.Translate("hotkey-hud-" + name), key, HotkeyType.HelpAndOverlays,
            ctrlPressed: ctrl);
        capi.Input.SetHotKeyHandler(code, _ =>
        {
            act();
            return true;
        });
    }
}
