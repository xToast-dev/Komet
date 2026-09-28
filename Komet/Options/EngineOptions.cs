using System.Globalization;
using HarmonyLib;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Options.KometPages;

namespace Komet.Options;

// The game's graphics tab as three option pages, each control with what the engine's own handler does besides setting the property
// (GuiCompositeSettings, Vintage Story 1.22.7): a shader reload, a framebuffer rebuild, the window mode, the preset turned "custom".
// The watchers the engine registers on ClientSettings do the rest, and ClientSettings saves itself. Labels and hints are the game's.
internal static partial class EngineOptions
{
    private const string Custom = "preset-custom";
    private const int MaxLanguages = 64;

    [Flags]
    private enum Effect
    {
        None = 0,
        Reload = 1, // shaders
        Rebuild = 2, // framebuffers
        Custom = 4 // the preset no longer describes the settings
    }

    private static Effect? _batched; // while Batch runs: the reloads and rebuilds asked for so far, done once at its end
    private static ICoreClientAPI? _capi;

    // tab opens one of the game's own screens by handler name; without it (screen opened on its own) the buttons leading there are left out
    public static OptionPage[] Pages(ICoreClientAPI capi, Action<string>? tab = null)
    {
        if (!NotNull(capi)) return [];
        _capi = capi;
        var section = T("section-game");
        List<OptionPage> pages = [General(capi, section), Quality(capi, section), Performance(section), Mouse(section)];
        if (Controls(section, tab) is { } controls) pages.Add(controls);
        pages.AddRange([Accessibility(capi, section), Sound(section), Interface(capi, section, tab)]);
        if (Developer(section) is { } developer) pages.Add(developer);
        return [.. pages];
    }

    private static OptionPage General(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-general", T("page-general"), section);
        var presets = GraphicsPreset.Presets;
        if (!NotNull(capi) || !NotNull(presets) || !Assert(presets.Count > 0)) return page;
        _ = Presets(capi, page)
            .Slider(Name("viewdist"), 32, 1536, 32, () => ClientSettings.ViewDistance,
                v => Apply(capi, () => ClientSettings.ViewDistance = (int)v, Effect.Custom), " " + T("unit-blocks"),
                Hover(capi.IsSinglePlayer ? "viewdist-singleplayer" : "viewdist"))
            .Slider(Name("fov"), 20, 150, 1, () => ClientSettings.FieldOfView, v => ClientSettings.FieldOfView = (int)v,
                "°", Hover("fov"))
            .Choice(Name("windowmode"), Names("windowmode-", "normal", "fullscreen", "maxborderless", "fullscreen-ontop"),
                () => ClientSettings.GameWindowMode, WindowMode)
            .Choice(Name("windowborder"), Names("windowborder-", "resizable", "fixed", "hidden"),
                () => ClientSettings.WindowBorder, Border)
            .Choice(Name("vsync"), [Lang.Get("Off"), Lang.Get("On"), Lang.Get("On + Sleep")],
                () => ClientSettings.VsyncMode, i => ClientSettings.VsyncMode = i, Hover("vsync"))
            .Slider(Name("maxfps"), 15, 241, 1, () => Math.Clamp(ClientSettings.MaxFPS, 15, 241),
                v => ClientSettings.MaxFPS = (int)v, "", Hover("maxfps"))
            .Format(v => v >= 241 ? Lang.Get("unlimited") : ((int)v).ToString(CultureInfo.InvariantCulture));
        return Appearance(capi, page);
    }

    // The presets as the scale they are, from the least to the most: a slider over them. "Custom" is no step of it but what the
    // slider shows once a single setting departs from the preset (Effect.Custom)
    private static OptionPage Presets(ICoreClientAPI capi, OptionPage page)
    {
        GraphicsPreset[] steps = [.. GraphicsPreset.Presets.Where(p => p.Langcode != Custom).Take(OptionPage.MaxOptions)];
        if (!NotNull(capi) || !NotNull(page) || !Assert(steps.Length > 1)) return page;
        var custom = Lang.Get(Custom);
        return page.Slider(Name("preset"), 0, steps.Length - 1, 1,
                () => Array.FindIndex(steps, p => p.PresetId == ClientSettings.GraphicsPresetId),
                v => Preset(capi, steps[Math.Clamp((int)Math.Round(v), 0, steps.Length - 1)].PresetId), "", T("preset-hint"))
            .Format(v => Index((int)Math.Round(v), steps.Length) ? Lang.Get(steps[(int)Math.Round(v)].Langcode) : custom);
    }

    // Gamma and colour grading; sepia and contrast follow the day while the dynamic grading is on
    private static OptionPage Appearance(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page) || !Assert(page.Count > 0)) return page;
        return page.Group(Lang.Get("setting-column-appear"))
            .Slider(Name("gamma"), 30, 300, 5, () => Math.Round(ClientSettings.GammaLevel * 100),
                v => ClientSettings.GammaLevel = (float)(v / 100), "", Hover("gamma"))
            .Switch(Name("dynamiccolorgrading"), () => ClientSettings.DynamicColorGrading, on => Grading(capi, on),
                Hover("dynamiccolorgrading"))
            .Slider(Name("contrast"), 100, 200, 10, () => Math.Round(ClientSettings.ExtraContrastLevel * 100) + 100,
                v => Apply(capi, () => ClientSettings.ExtraContrastLevel = (float)((v - 100) / 100), Effect.Custom), "%",
                Hover("contrast"))
            .EnabledWhen(() => !ClientSettings.DynamicColorGrading)
            .Slider(Name("sepia"), 0, 100, 5, () => Math.Round(ClientSettings.SepiaLevel * 100),
                v => ClientSettings.SepiaLevel = (float)(v / 100), "", Hover("sepia"))
            .EnabledWhen(() => !ClientSettings.DynamicColorGrading);
    }

    private static OptionPage Quality(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-quality", T("page-quality"), section);
        string[] ssao = [Lang.Get("Off"), Lang.Get("Medium quality"), Lang.Get("High quality")];
        string[] shadows = [Lang.Get("Off"), Lang.Get("Low quality"), Lang.Get("Medium quality"),
            Lang.Get("High quality"), Lang.Get("Very high quality")];
        if (!NotNull(capi) || !Assert(shadows.Length == 5 && ssao.Length == 3)) return page;
        _ = page.Group(Lang.Get("setting-column-graphics"))
            .Slider(Name("shadows"), 0, 4, 1, () => ClientSettings.ShadowMapQuality,
                v => Apply(capi, () => ClientSettings.ShadowMapQuality = (int)v, Effect.Rebuild | Effect.Reload | Effect.Custom),
                "", Hover("dynashade")).Format(v => Named(shadows, v))
            .Switch(Name("smoothshadows"), () => ClientSettings.SmoothShadows,
                on => Apply(capi, () => ClientSettings.SmoothShadows = on, Effect.Custom), Hover("smoothshadows"))
            .Slider(Name("ssao"), 0, 2, 1, () => ClientSettings.SSAOQuality, // in game the engine's watcher reloads and rebuilds
                v => Apply(capi, () => ClientSettings.SSAOQuality = (int)v, Effect.Custom), "", Hover("ssao"))
            .Format(v => Named(ssao, v))
            .Switch(Name("bloom"), () => ClientSettings.Bloom,
                on => Apply(capi, () => ClientSettings.Bloom = on, Effect.Reload | Effect.Custom), Hover("bloom"))
            .Slider(Name("abloom"), 0, 100, 10, () => ClientSettings.AmbientBloomLevel,
                v => Apply(capi, () => ClientSettings.AmbientBloomLevel = (float)v, Effect.Reload | Effect.Custom), "%",
                Hover("abloom"))
            .Switch(Name("godrays"), () => ClientSettings.GodRayQuality > 0,
                on => Apply(capi, () => ClientSettings.GodRayQuality = on ? 1 : 0, Effect.Reload | Effect.Custom),
                Hover("godrays"))
            .Switch(Name("fxaa"), () => ClientSettings.FXAA,
                on => Apply(capi, () => ClientSettings.FXAA = on, Effect.Reload | Effect.Custom), Hover("fxaa"));
        return Effects(capi, page);
    }

    private static OptionPage Effects(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page) || !Assert(page.Count > 0)) return page;
        return page.Choice(Name("clouds"), Names("settings-clouds-", "off", "volumetric", "classic"),
                () => ClientSettings.CloudRenderMode, i => ClientSettings.CloudRenderMode = i,
                Lang.Get("settings-hover-" + "clouds"))
            .Switch(Name("grasswaves"), () => ClientSettings.WavingFoliage,
                on => Apply(capi, () => ClientSettings.WavingFoliage = on, Effect.Reload | Effect.Custom),
                Hover("grasswaves"))
            .Switch(Name("foamandshinyeffect"), () => ClientSettings.LiquidFoamAndShinyEffect,
                on => Apply(capi, () => ClientSettings.LiquidFoamAndShinyEffect = on, Effect.Reload | Effect.Custom),
                Hover("foamandshinyeffect"))
            .Slider(Name("particles"), 0, 100, 2, () => ClientSettings.ParticleLevel,
                v => Apply(capi, () => ClientSettings.ParticleLevel = (int)v, Effect.Custom), "%", Hover("particles"))
            .Slider(Name("dynalight"), 0, 100, 1, () => ClientSettings.MaxDynamicLights,
                v => Apply(capi, () => ClientSettings.MaxDynamicLights = (int)v, Effect.Reload | Effect.Custom), "",
                Hover("dynalight"))
            .Format(v => v <= 0 ? Lang.Get("disabled") : ((int)v).ToString(CultureInfo.InvariantCulture))
            .Slider(Name("resolution"), 25, 100, 25, () => Math.Round(ClientSettings.SSAA * 100),
                v => Apply(capi, () => ClientSettings.SSAA = (float)(v / 100), Effect.Rebuild | Effect.Custom), "%",
                Hover("resolution"));
    }

    private static OptionPage Performance(string section)
    {
        var page = new OptionPage("vs-performance", T("page-performance"), section);
        if (!Assert(section.Length > 0)) return page;
        return page.Choice(Name("optimizeram"), [Lang.Get("Optimize somewhat"), Lang.Get("Aggressively optimize ram")],
                () => Math.Clamp(ClientSettings.OptimizeRamMode - 1, 0, 1), i => ClientSettings.OptimizeRamMode = i + 1,
                Hover("optimizeram"))
            .Switch(Name("occlusionculling"), () => ClientSettings.Occlusionculling,
                on => ClientSettings.Occlusionculling = on, Hover("occlusionculling"))
            .Slider(Name("lodbiasfar"), 35, 100, 1, () => Math.Round(ClientSettings.LodBiasFar * 100),
                v => Apply(null, () => ClientSettings.LodBiasFar = (float)(v / 100), Effect.Custom), "%",
                Hover("lodbiasfar"));
    }

    // Set, then what the engine's handler does after it
    private static void Apply(ICoreClientAPI? capi, Action set, Effect effect)
    {
        if (!NotNull(set) || !Assert((effect & ~(Effect.Reload | Effect.Rebuild | Effect.Custom)) == 0)) return;
        set();
        if ((effect & Effect.Custom) != 0 && CustomPreset() is { } custom) ClientSettings.GraphicsPresetId = custom;
        Run(capi ?? _capi, effect);
    }

    // Several settings applied at once (the options window's Apply): one rebuild and one shader reload for all of them
    internal static void Batch(Action apply)
    {
        if (!NotNull(apply) || !Assert(_batched is null)) return;
        _batched = Effect.None;
        try
        {
            apply();
        }
        finally
        {
            var effect = _batched ?? Effect.None;
            _batched = null;
            Run(_capi, effect);
        }
    }

    private static void Run(ICoreClientAPI? capi, Effect effect)
    {
        if (_batched is { } batched)
        {
            _batched = batched | (effect & (Effect.Rebuild | Effect.Reload));
            return;
        }

        if ((effect & Effect.Rebuild) != 0 && NotNull(ScreenManager.Platform)) ScreenManager.Platform.RebuildFrameBuffers();
        if ((effect & Effect.Reload) != 0 && NotNull(capi)) _ = capi.Shader.ReloadShaders();
    }

    // onPresetChanged: every value of the preset under one suppressed reload, then one rebuild and one reload
    internal static void Preset(ICoreClientAPI capi, int index)
    {
        var presets = GraphicsPreset.Presets;
        if (!NotNull(capi) || !Index(index, presets.Count)) return;
        var p = presets[index];
        if (p.Langcode == Custom) return;
        ShaderRegistry.SupressShaderAndBufferReloads = true;
        try
        {
            (ClientSettings.GraphicsPresetId, ClientSettings.ViewDistance, ClientSettings.SmoothShadows) =
                (p.PresetId, p.ViewDistance, p.SmoothLight);
            (ClientSettings.FXAA, ClientSettings.SSAOQuality, ClientSettings.WavingFoliage) = (p.FXAA, p.SSAO, p.WavingFoliage);
            (ClientSettings.LiquidFoamAndShinyEffect, ClientSettings.Bloom, ClientSettings.GodRayQuality) =
                (p.LiquidFoamEffect, p.Bloom, p.GodRays ? 1 : 0);
            (ClientSettings.ShadowMapQuality, ClientSettings.ParticleLevel, ClientSettings.MaxDynamicLights) =
                (p.ShadowMapQuality, p.ParticleLevel, p.DynamicLights);
            (ClientSettings.SSAA, ClientSettings.LodBiasFar) = (p.Resolution, p.LodBiasFar);
        }
        finally
        {
            ShaderRegistry.SupressShaderAndBufferReloads = false;
        }

        Run(capi, Effect.Rebuild | Effect.Reload);
    }

    private static int? CustomPreset()
    {
        if (!NotNull(GraphicsPreset.Presets)) return null;
        var custom = GraphicsPreset.Presets.Find(p => p.Langcode == Custom);
        return custom is not null && Assert(custom.PresetId >= 0) ? custom.PresetId : null;
    }

    private static void WindowMode(int mode)
    {
        if (Index(mode, 4) && NotNull(SetWindowMode)) SetWindowMode(mode);
    }

    // OnWindowBorderChanged: a border other than hidden ends the borderless mode
    private static void Border(int border)
    {
        if (!Index(border, 3)) return;
        ClientSettings.WindowBorder = border;
        if (ClientSettings.GameWindowMode == 2 && border != 2) ClientSettings.GameWindowMode = 0;
        _ = Assert(ClientSettings.GameWindowMode != 2 || border == 2);
    }

    // onDynamicGradingToggled: switched off, the shaders take the player's own sepia and contrast again
    private static void Grading(ICoreClientAPI capi, bool on)
    {
        if (!NotNull(capi)) return;
        ClientSettings.DynamicColorGrading = on;
        if (on || !NotNull(capi.Render.ShaderUniforms)) return;
        capi.Render.ShaderUniforms.SepiaLevel = ClientSettings.SepiaLevel;
        capi.Render.ShaderUniforms.ExtraContrastLevel = ClientSettings.ExtraContrastLevel;
    }

    private static readonly Action<int>? SetWindowMode =
        AccessTools.Method(typeof(GuiCompositeSettings), "SetWindowMode", [typeof(int)]) is { } method
            ? AccessTools.MethodDelegate<Action<int>>(method)
            : null;

    private static string Name(string id) =>
        NotNull(id) && Assert(id.Length > 0) ? Lang.Get("setting-name-" + id) : "";

    private static string Hover(string id) =>
        NotNull(id) && Assert(id.Length > 0) ? Lang.Get("setting-hover-" + id) : "";

    private static string Named(string[] names, double value)
    {
        if (!NotNull(names) || !Finite(value)) return "";
        var i = (int)Math.Round(value);
        return Index(i, names.Length) ? names[i] : "";
    }

    private static string[] Names(string prefix, params string[] ids) =>
        NotNull(prefix) && Assert(ids.Length is > 0 and <= OptionPage.MaxOptions)
            ? Array.ConvertAll(ids, id => Lang.Get(prefix + id))
            : [];
}
