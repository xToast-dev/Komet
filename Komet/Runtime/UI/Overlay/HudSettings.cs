using System.Globalization;
using Cairo;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Komet.Runtime.UI.Overlay;

[JsonConverter(typeof(StringEnumConverter))] internal enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight }

internal readonly record struct HudRange(double Min, double Max, double Step, double Factor, string Unit)
{
    public bool Contains(double value) => Finite(value) && value >= Min && value <= Max;
    public string Text(double value) => Assert(Factor > 0) && Assert(Unit.Length > 0) ? HudSettings.Format(value * Factor, "N0") + " " + Unit : "";
}

// Properties with a setter persist as JSON in ModConfig; every setter raises `Changed`. Numbers are always metric and culture-invariant.
internal sealed class HudSettings
{
    private const string FileName = "komet-hud.json";
    public const int SlowEvery = 4, ModsEvery = 32, HistoryFrames = 2000, LogScrollLines = 3, DoubleClickMs = 400;
    public const double PanelGap = 6, ScreenMargin = 8, SnapGrid = 8, SnapDistance = 12;
    public static readonly HudRange OpacityRange = new(0, 1, 0.05, 100, "%"), ScaleRange = new(0.5, 2, 0.1, 100, "%");
    public static readonly HudRange IntervalRange = new(0.1, 1, 0.05, 1000, "ms"), BenchRange = new(5, 120, 5, 1, "s");
    private const double BaseFontSize = 14, BaseTitleSize = 16;

    public bool Visible { get; set => Set(ref field, value); }
    public HudCorner Corner { get; private set { if (Assert(Enum.IsDefined(value))) Set(ref field, value); } }
    public double Opacity { get; set { if (Assert(OpacityRange.Contains(value))) Set(ref field, value); } } = 0.6;
    public double Interval { get; set { if (Assert(IntervalRange.Contains(value))) Set(ref field, value); } } = 0.25;
    public double BenchSeconds { get; set { if (Assert(BenchRange.Contains(value))) Set(ref field, value); } } = 30;
    public bool ShowGraph { get; set => Set(ref field, value); } = true;
    public bool ShowSystem { get; set => Set(ref field, value); } = true;
    public bool ShowPasses { get; set { Set(ref field, value); if (!value && !ShowModTimes) Detail = false; } } = true;
    public bool ShowModTimes { get; set { Set(ref field, value); if (!value && !ShowPasses) Detail = false; } }
    public bool Detail { get; set => Set(ref field, value && (ShowPasses || ShowModTimes)); }   // detail lines only exist in the passes and mod timings panels
    public bool ShowMods { get; set => Set(ref field, value); }
    public bool ShowLog { get; set => Set(ref field, value); }
    public bool ShowDebugLog { get; set => Set(ref field, value); }
    public bool ShaderUseCache { get; set { Set(ref field, value); Features.ShaderUseCache.Enabled = value; } } = true;
    public Dictionary<int, double[]> Pinned { get; } = [];
    public event Action? Changed;

    [JsonIgnore] public CairoFont Text   { get; } = CairoFont.WhiteDetailText().WithLineHeightMultiplier(0.9);
    [JsonIgnore] public CairoFont Header { get; } = CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold);
    [JsonIgnore] public CairoFont Title  { get; } = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);

    public double FontScale
    {
        get;
        set
        {
            if (!Assert(ScaleRange.Contains(value))) return;
            Text.UnscaledFontsize = Header.UnscaledFontsize = BaseFontSize * value;
            Title.UnscaledFontsize = BaseTitleSize * value;
            Set(ref field, value);
        }
    } = 1.0;

    private void Set<T>(ref T target, T value)
    {
        if (EqualityComparer<T>.Default.Equals(target, value)) return;
        target = value;
        Changed?.Invoke();
    }

    public void SetCorner(HudCorner corner)
    {
        if (!Assert(Enum.IsDefined(corner))) return;
        Pinned.Clear();
        Corner = corner;
    }

    public void ResetPositions()
    {
        Pinned.Clear();
        NotifyChanged();
    }

    public void NotifyChanged() => Changed?.Invoke();

    public void ResetDefaults()
    {
        var d = new HudSettings();
        (Corner, Opacity, FontScale, Interval, BenchSeconds) = (d.Corner, d.Opacity, d.FontScale, d.Interval, d.BenchSeconds);
        (ShowGraph, ShowSystem, ShowPasses, ShowModTimes, Detail, ShowMods, ShowLog, ShowDebugLog) = (d.ShowGraph, d.ShowSystem, d.ShowPasses, d.ShowModTimes, d.Detail, d.ShowMods, d.ShowLog, d.ShowDebugLog);
        ResetPositions();
    }

    public static HudSettings Load(ICoreClientAPI capi)
    {
        try
        {
            var loaded = capi.LoadModConfig<HudSettings>(FileName) ?? new();
            return Assert(loaded.Text.UnscaledFontsize > 0) && Assert(loaded.Pinned.Count < 100) ? loaded : new();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            capi.Logger.Warning("Komet HUD: {0} unreadable, using defaults ({1})", FileName, e.Message);
            return new();
        }
    }

    public void Save(ICoreClientAPI capi)
    {
        try { capi.StoreModConfig(this, FileName); }
        catch (IOException e) { capi.Logger.Warning("Komet HUD: {0} not written ({1})", FileName, e.Message); }
    }

    public static string Format(double value, string format)
        => double.IsNaN(value) || !Assert(format.Length is 2 or 3) ? "" : value.ToString(format, CultureInfo.InvariantCulture);

    // Lang.Get hands the key back when the translation is missing
    public static string Translate(string key, params object[] args)
    {
        var full = "komet:" + key;
        var text = Lang.Get(full, args);
        return Assert(key.Length > 0) && Assert(text != full) ? text : key;
    }

    public void RegisterHotkeys(ICoreClientAPI capi)
    {
        Hotkey(capi, "toggle", shift: false, () => Visible = !Visible);
        Hotkey(capi, "detail", shift: true, () => Detail = !Detail);
    }

    private static void Hotkey(ICoreClientAPI capi, string name, bool shift, Func<bool> toggle)
    {
        if (!Assert(name.Length > 0)) return;
        var code = "komet-hud-" + name;
        capi.Input.RegisterHotKey(code, Translate("hotkey-hud-" + name), GlKeys.F7, HotkeyType.HelpAndOverlays, shiftPressed: shift);
        capi.Input.SetHotKeyHandler(code, _ =>
        {
            capi.ShowChatMessage(Translate($"hud-{name}-{(toggle() ? "on" : "off")}"));
            return true;
        });
    }
}
