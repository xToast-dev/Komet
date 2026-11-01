using System.Runtime.Serialization;
using Komet.Core;
using Komet.Options;
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

internal sealed class HudSettings
{
    internal const string FileName = "komet.json", OldFileName = "komet-hud.json"; // the name before 3.0

    public const int SlowEvery = 4;
    public const double ScreenMargin = 8, SnapStep = 8;
    public static readonly ValueRange OpacityRange = new(0, 1, 0.05, 100, "%"), ScaleRange = new(0.5, 2, 0.1, 100, "%");
    public static readonly ValueRange IntervalRange = new(0.1, 1, 0.05, 1000, "ms"), BenchRange = new(5, 120, 5, 1, "s");
    public static readonly ValueRange ToastRange = new(0, 200, 5, 1, "ms"), MenuOpacityRange = new(0.2, 1, 0.05, 100, "%");

    // The knob part of the file (Preset, Chosen, Profiled and the knob keys below are its)
    [JsonIgnore]
    public KometSettings Knobs { get; } = new();

    [JsonExtensionData]
    internal Dictionary<string, JToken> KnobJson => Knobs.Json; // the knobs, as top-level keys of the file

    // Overlay (F7); the window (Ctrl+F7) and the debug window (Ctrl+F8) are dialogs of their own
    public bool Visible { get; set => Set(ref field, value); }

    public HudCorner Corner
    {
        get;
        set { if (Assert(value is >= HudCorner.TopLeft and <= HudCorner.BottomRight)) Set(ref field, value); }
    }

    public double Opacity { get; set { if (Assert(OpacityRange.Contains(value))) Set(ref field, value); } } = 0.6;
    public double Interval { get; set { if (Assert(IntervalRange.Contains(value))) Set(ref field, value); } } = 0.25;
    public double BenchSeconds { get; set { if (Assert(BenchRange.Contains(value))) Set(ref field, value); } } = 30;
    public bool ShowFps { get; set => Set(ref field, value); } = true;
    public bool ShowLows { get; set => Set(ref field, value); } = true;
    public bool ShowFrametime { get; set => Set(ref field, value); } = true;
    public bool ShowGraph { get; set => Set(ref field, value); } = true;
    public bool ShowMods { get; set => Set(ref field, value); } = true;

    // A spike over this many ms toasts in the overlay with its cause; 0: never
    public double ToastMs { get; set { if (Assert(ToastRange.Contains(value))) Set(ref field, value); } } = 50;

    public string Preset
    {
        get => Knobs.Preset;
        set => Knobs.Preset = value;
    }

    // Written although layout 2 reads the saved knob keys alone: an older Komet reading the file finds the player's
    // knobs in it
    public List<string> Chosen => Knobs.Chosen;

    public int Profiled
    {
        get => Knobs.Profiled;
        set => Knobs.Profiled = value;
    }

    public string WindowTab { get; set; } = "";

    // Dialog places, NaN: centred. Plain properties: a drag must not end the HUD's interval on every move.
    public double WindowX { get; set; } = double.NaN;
    public double WindowY { get; set; } = double.NaN;
    public double DebugX { get; set; } = double.NaN;
    public double DebugY { get; set; } = double.NaN;

    public string DebugLanguage { get; set => Set(ref field, value is "de" ? "de" : "en"); } = "en";
    public bool DebugOnJoin { get; set => Set(ref field, value); } // /komet debug join: each world's first minute recorded
    public bool UpdateCheck { get; set => Set(ref field, value); } // asks GitHub once per start, opt-in
    public bool UpdateAsked { get; set => Set(ref field, value); } // the opt-in dialog is shown until answered
    public double FontScale { get; set { if (Assert(ScaleRange.Contains(value))) Set(ref field, value); } } = 1.0;

    public bool ShowAdvanced { get; set => Set(ref field, value); }

    // The options screen's look: a style id (KometStyles), how much its panels cover, how much moves (0 none, 1 calm, 2 full)
    public string MenuStyle { get; set => Set(ref field, string.IsNullOrWhiteSpace(value) ? StyleSheet.Pragmatic : StyleSheet.Current(value.Trim())); } =
        StyleSheet.Pragmatic;
    public double MenuOpacity { get; set { if (Assert(MenuOpacityRange.Contains(value))) Set(ref field, value); } } = 0.75;
    public int MenuMotion { get; set => Set(ref field, Math.Clamp(value, 0, 2)); } = 2;

    public event Action? Changed;

    public HudSettings() => Knobs.Changed += NotifyChanged;

    private void Set<T>(ref T target, T value)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return;
        target = value;
        Changed?.Invoke();
    }

    public void ResetPositions() => (WindowX, WindowY, DebugX, DebugY) = (double.NaN, double.NaN, double.NaN, double.NaN);

    private void NotifyChanged() => Changed?.Invoke();

    // The update notice is consent, not a display setting
    public void ResetDefaults()
    {
        var d = new HudSettings();
        (Corner, Opacity, FontScale, Interval, BenchSeconds) =
            (d.Corner, d.Opacity, d.FontScale, d.Interval, d.BenchSeconds);
        (MenuStyle, MenuOpacity, MenuMotion) = (d.MenuStyle, d.MenuOpacity, d.MenuMotion);
        (ShowFps, ShowLows, ShowFrametime, ShowGraph, ShowMods, ToastMs) =
            (d.ShowFps, d.ShowLows, d.ShowFrametime, d.ShowGraph, d.ShowMods, d.ToastMs);
        Knobs.UseDetected();
        ResetPositions();
    }

    public static HudSettings Load(ICoreClientAPI capi)
    {
        HardwareProfile.Detect(capi.Logger);
        var settings = Read(capi);
        settings.Knobs.Apply(capi.Logger, FileName);
        return settings;
    }

    // A komet-hud.json from before 3.0 becomes komet.json once
    private static void Renamed(ILogger logger)
    {
        var (old, path) = (Path.Join(GamePaths.ModConfig, OldFileName), Path.Join(GamePaths.ModConfig, FileName));
        try
        {
            if (!File.Exists(old) || File.Exists(path)) return;
            File.Move(old, path);
            logger.Notification("Komet: settings moved from {0} to {1}", OldFileName, FileName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.Warning("Komet: {0} not renamed to {1} ({2}); defaults apply", OldFileName, FileName, e.Message);
        }
    }

    private static HudSettings Read(ICoreClientAPI capi)
    {
        Renamed(capi.Logger);
        try
        {
            // a saved file may still hold keys of earlier versions (PinnedMods, ShowPins): unknown keys are ignored
            return capi.LoadModConfig<HudSettings>(FileName) ?? new HudSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            capi.Logger.Warning("Komet HUD: {0} unreadable, using defaults ({1})", FileName, e.Message);
            if (e is JsonException) Keep(Path.Join(GamePaths.ModConfig, FileName)); // the next save would overwrite a hand edit's typo
            return new HudSettings();
        }
    }

    private static void Keep(string path)
    {
        try
        {
            File.Copy(path, path + ".broken", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no copy: the warning above names the file
        }
    }

    [OnDeserializing]
    private void Reading(StreamingContext context)
    {
        Knobs.Reading();
    }

    [OnSerializing]
    private void Serializing(StreamingContext context) => Knobs.Writing();

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

    public void RegisterHotkeys(ICoreClientAPI capi, Action window, Action debug)
    {
        if (!NotNull(capi) || !NotNull(capi.Input) || !NotNull(window) || !NotNull(debug)) return;
        Hotkey(capi, "toggle", GlKeys.F7, false, () =>
        {
            Visible = !Visible;
            capi.ShowChatMessage(KometText.Translate($"hud-toggle-{(Visible ? "on" : "off")}"));
        });
        Hotkey(capi, "window", GlKeys.F7, true, window);
        Hotkey(capi, "debug", GlKeys.F8, true, debug);
    }

    private static void Hotkey(ICoreClientAPI capi, string name, GlKeys key, bool ctrl, Action act)
    {
        var code = "komet-hud-" + name;
        capi.Input.RegisterHotKey(code, KometText.Translate("hotkey-hud-" + name), key, HotkeyType.HelpAndOverlays,
            ctrlPressed: ctrl);
        capi.Input.SetHotKeyHandler(code, _ =>
        {
            act();
            return true;
        });
    }
}
