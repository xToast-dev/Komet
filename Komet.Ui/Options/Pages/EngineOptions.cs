using HarmonyLib;
using Komet.Api;
using System.Globalization;
using Vintagestory.Client.NoObf;
using Vintagestory.Client;
using static Komet.Options.OptionsText;
using KeyMap = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;

namespace Komet.Options;

// Each control mirrors its GuiCompositeSettings handler (1.22.7); ClientSettings watchers do the rest and it saves
// itself.
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

    // The world is left: neither its client nor the hotkeys' clash list is kept until the next one. Never inside Batch.
    internal static void Forget()
    {
        if (!Assert(_batched is null)) return;
        (_capi, _clashes, _armed) = (null, null, false);
    }

    // Without tab the buttons to the game's screens are left out
    public static OptionPage[] Pages(ICoreClientAPI capi, Action<string>? tab = null)
    {
        if (!NotNull(capi)) return [];
        _capi = capi;
        var section = T("section-game");
        List<OptionPage> pages = [General(capi, section), Quality(section), Performance(section), Mouse(section)];
        if (Controls(section, tab) is { } controls) pages.Add(controls);
        pages.AddRange([Accessibility(capi, section), Sound(section), Interface(capi, section, tab)]);
        if (Developer(section) is { } developer) pages.Add(developer);
        return [.. pages];
    }

    private static OptionPage General(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-general", T("page-general"), section);
        var presets = GraphicsPreset.Presets;
        if (!NotNull(presets) || !Assert(presets.Count > 0)) return page;
        _ = Presets(capi, page)
            .Slider(Name("viewdist"), 32, 1536, 32, () => ClientSettings.ViewDistance,
                v => Apply(() => ClientSettings.ViewDistance = (int)v, Effect.Custom), " " + T("unit-blocks"),
                Hover(capi.IsSinglePlayer ? "viewdist-singleplayer" : "viewdist"))
            .Setting("fov", 20, 150, 1, () => ClientSettings.FieldOfView, v => ClientSettings.FieldOfView = (int)v, "°")
            .Choice(Name("windowmode"), Names("windowmode-", "normal", "fullscreen", "maxborderless", "fullscreen-ontop"),
                () => ClientSettings.GameWindowMode, mode =>
                {
                    if (Index(mode, 4) && NotNull(SetWindowMode)) SetWindowMode(mode);
                })
            .Choice(Name("windowborder"), Names("windowborder-", "resizable", "fixed", "hidden"),
                () => ClientSettings.WindowBorder, Border)
            .Choice(Name("vsync"), [Lang.Get("Off"), Lang.Get("On"), Lang.Get("On + Sleep")],
                () => ClientSettings.VsyncMode, i => ClientSettings.VsyncMode = i, Hover("vsync"))
            .Setting("maxfps", 15, 241, 1, () => Math.Clamp(ClientSettings.MaxFPS, 15, 241),
                v => ClientSettings.MaxFPS = (int)v)
            .Format(v => v >= 241 ? Lang.Get("unlimited") : ((int)v).ToString(CultureInfo.InvariantCulture));
        return Appearance(capi, page);
    }

    // "Custom" is no step: shown once a setting departs from the preset.
    private static OptionPage Presets(ICoreClientAPI capi, OptionPage page)
    {
        GraphicsPreset[] steps = [.. GraphicsPreset.Presets.Where(p => p.Langcode != Custom).Take(OptionPage.MaxOptions)];
        if (!Assert(steps.Length > 1)) return page;
        var custom = Lang.Get(Custom);
        return page.Slider(Name("preset"), 0, steps.Length - 1, 1,
                () => Array.FindIndex(steps, p => p.PresetId == ClientSettings.GraphicsPresetId),
                v => Preset(capi, steps[Math.Clamp((int)Math.Round(v), 0, steps.Length - 1)].PresetId), "", T("preset-hint"))
            // -1 (no preset matches) is normal, not a failed assertion
            .Format(v => (int)Math.Round(v) is var i && (uint)i < (uint)steps.Length ? Lang.Get(steps[i].Langcode) : custom);
    }

    private static OptionPage Appearance(ICoreClientAPI capi, OptionPage page)
    {
        return page.Group(Lang.Get("setting-column-appear"))
            .Setting("gamma", 30, 300, 5, () => Math.Round(ClientSettings.GammaLevel * 100),
                v => ClientSettings.GammaLevel = (float)(v / 100))
            .Setting("dynamiccolorgrading", () => ClientSettings.DynamicColorGrading, on => Grading(capi, on))
            .Setting("contrast", 100, 200, 10, () => Math.Round(ClientSettings.ExtraContrastLevel * 100) + 100,
                v => Apply(() => ClientSettings.ExtraContrastLevel = (float)((v - 100) / 100), Effect.Custom), "%")
            .EnabledWhen(() => !ClientSettings.DynamicColorGrading)
            .Setting("sepia", 0, 100, 5, () => Math.Round(ClientSettings.SepiaLevel * 100),
                v => ClientSettings.SepiaLevel = (float)(v / 100))
            .EnabledWhen(() => !ClientSettings.DynamicColorGrading);
    }

    private static OptionPage Quality(string section)
    {
        var page = new OptionPage("vs-quality", T("page-quality"), section);
        string[] ssao = [Lang.Get("Off"), Lang.Get("Medium quality"), Lang.Get("High quality")];
        string[] shadows = [Lang.Get("Off"), Lang.Get("Low quality"), Lang.Get("Medium quality"),
            Lang.Get("High quality"), Lang.Get("Very high quality")];
        _ = page.Group(Lang.Get("setting-column-graphics"))
            .Slider(Name("shadows"), 0, 4, 1, () => ClientSettings.ShadowMapQuality,
                v => Apply(() => ClientSettings.ShadowMapQuality = (int)v, Effect.Rebuild | Effect.Reload | Effect.Custom),
                "", Hover("dynashade")).Format(v => Named(shadows, v))
            .Setting("smoothshadows", () => ClientSettings.SmoothShadows,
                on => Apply(() => ClientSettings.SmoothShadows = on, Effect.Custom))
            .Setting("ssao", 0, 2, 1, () => ClientSettings.SSAOQuality, // in game the engine's watcher reloads and rebuilds
                v => Apply(() => ClientSettings.SSAOQuality = (int)v, Effect.Custom))
            .Format(v => Named(ssao, v))
            .Setting("bloom", () => ClientSettings.Bloom,
                on => Apply(() => ClientSettings.Bloom = on, Effect.Reload | Effect.Custom))
            .Setting("abloom", 0, 100, 10, () => ClientSettings.AmbientBloomLevel,
                v => Apply(() => ClientSettings.AmbientBloomLevel = (float)v, Effect.Reload | Effect.Custom), "%")
            .Setting("godrays", () => ClientSettings.GodRayQuality > 0,
                on => Apply(() => ClientSettings.GodRayQuality = on ? 1 : 0, Effect.Reload | Effect.Custom))
            .Setting("fxaa", () => ClientSettings.FXAA,
                on => Apply(() => ClientSettings.FXAA = on, Effect.Reload | Effect.Custom));
        return Effects(page);
    }

    private static OptionPage Effects(OptionPage page)
    {
        return page.Choice(Name("clouds"), Names("settings-clouds-", "off", "volumetric", "classic"),
                () => ClientSettings.CloudRenderMode, i => ClientSettings.CloudRenderMode = i,
                Lang.Get("settings-hover-" + "clouds"))
            .Setting("grasswaves", () => ClientSettings.WavingFoliage,
                on => Apply(() => ClientSettings.WavingFoliage = on, Effect.Reload | Effect.Custom))
            .Setting("foamandshinyeffect", () => ClientSettings.LiquidFoamAndShinyEffect,
                on => Apply(() => ClientSettings.LiquidFoamAndShinyEffect = on, Effect.Reload | Effect.Custom))
            .Setting("particles", 0, 100, 2, () => ClientSettings.ParticleLevel,
                v => Apply(() => ClientSettings.ParticleLevel = (int)v, Effect.Custom), "%")
            .Setting("dynalight", 0, 100, 1, () => ClientSettings.MaxDynamicLights,
                v => Apply(() => ClientSettings.MaxDynamicLights = (int)v, Effect.Reload | Effect.Custom))
            .Format(v => v <= 0 ? Lang.Get("disabled") : ((int)v).ToString(CultureInfo.InvariantCulture))
            .Setting("resolution", 25, 100, 5, () => Math.Round(ClientSettings.SSAA * 100),
                v => Apply(() => ClientSettings.SSAA = (float)(v / 100), Effect.Rebuild | Effect.Custom), "%");
    }

    private static OptionPage Performance(string section)
    {
        var page = new OptionPage("vs-performance", T("page-performance"), section);
        return page.Choice(Name("optimizeram"), [Lang.Get("Optimize somewhat"), Lang.Get("Aggressively optimize ram")],
                () => Math.Clamp(ClientSettings.OptimizeRamMode - 1, 0, 1), i => ClientSettings.OptimizeRamMode = i + 1,
                Hover("optimizeram"))
            .Setting("occlusionculling", () => ClientSettings.Occlusionculling, on => ClientSettings.Occlusionculling = on)
            .Setting("lodbiasfar", 35, 100, 1, () => Math.Round(ClientSettings.LodBiasFar * 100),
                v => Apply(() => ClientSettings.LodBiasFar = (float)(v / 100), Effect.Custom), "%");
    }

    private static void Apply(Action set, Effect effect)
    {
        set();
        if ((effect & Effect.Custom) != 0 && CustomPreset() is { } custom) ClientSettings.GraphicsPresetId = custom;
        Run(_capi, effect);
    }

    // Several at once (Apply): one rebuild and one shader reload
    internal static void Batch(Action apply)
    {
        if (!Assert(_batched is null)) return;
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

    internal static void ReloadShaders(ICoreClientAPI capi) => Run(capi, Effect.Reload);

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

    internal static void Preset(ICoreClientAPI capi, int index)
    {
        var presets = GraphicsPreset.Presets;
        if (!Index(index, presets.Count)) return;
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

    private static void Border(int border)
    {
        if (!Index(border, 3)) return;
        ClientSettings.WindowBorder = border;
        if (ClientSettings.GameWindowMode == 2 && border != 2) ClientSettings.GameWindowMode = 0;
    }

    private static void Grading(ICoreClientAPI capi, bool on)
    {
        ClientSettings.DynamicColorGrading = on;
        if (on || !NotNull(capi.Render.ShaderUniforms)) return;
        capi.Render.ShaderUniforms.SepiaLevel = ClientSettings.SepiaLevel;
        capi.Render.ShaderUniforms.ExtraContrastLevel = ClientSettings.ExtraContrastLevel;
    }

    private static readonly Action<int>? SetWindowMode =
        AccessTools.Method(typeof(GuiCompositeSettings), "SetWindowMode", [typeof(int)]) is { } method
            ? AccessTools.MethodDelegate<Action<int>>(method)
            : null;

    private static OptionPage Setting(this OptionPage page, string id, Func<bool> get, Action<bool> set) =>
        page.Switch(Name(id), get, set, Hover(id));

    private static OptionPage Setting(this OptionPage page, string id, double min, double max, double step, Func<double> get,
        Action<double> set, string unit = "") =>
        page.Slider(Name(id), min, max, step, get, set, unit, Hover(id));

    private static string Name(string id) =>
        Lang.Get("setting-name-" + id);

    private static string Hover(string id) =>
        Lang.Get("setting-hover-" + id);

    private static string Named(string[] names, double value)
    {
        if (!Finite(value)) return "";
        var i = (int)Math.Round(value);
        return Index(i, names.Length) ? names[i] : "";
    }

    private static string[] Names(string prefix, params string[] ids) =>
        Array.ConvertAll(ids, id => Lang.Get(prefix + id));
}

// The game's other tabs (1.22.7) with each handler's conversions and side effects; ClientSettings watchers do the rest.
internal static partial class EngineOptions
{
    private static OptionPage Mouse(string section)
    {
        var page = new OptionPage("vs-mouse", Lang.Get("setting-mouse-header"), section);
        return page.Slider(Name("mousesensivity"), 1, 200, 5, () => ClientSettings.MouseSensivity,
                v => ClientSettings.MouseSensivity = (int)v)
            .Slider(Name("mousesmoothing"), 0, 95, 5, () => Math.Clamp(100 - ClientSettings.MouseSmoothing, 0, 95),
                v => ClientSettings.MouseSmoothing = 100 - (int)v)
            .Slider(Name("mousewheelsensivity"), 1, 100, 1, () => Math.Round(ClientSettings.MouseWheelSensivity * 10),
                v => ClientSettings.MouseWheelSensivity = (float)(v / 10))
            .Format(v => (v / 10).ToString("0.#", CultureInfo.InvariantCulture) + "x")
            .Setting("directmousemode", () => ClientSettings.DirectMouseMode, DirectMouse)
            .Switch(Name("invertyaxis"), () => ClientSettings.InvertMouseYAxis, on => ClientSettings.InvertMouseYAxis = on)
            .Choice(Name("itemCollectMode"), [Lang.Get("Always collect items"), Lang.Get("Only collect items when sneaking")],
                () => ClientSettings.ItemCollectMode, i => ClientSettings.ItemCollectMode = i);
    }

    private static OptionPage Accessibility(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-accessibility", Lang.Get("setting-accessibility-header"), section);
        return page.Setting("togglesprint", () => ClientSettings.ToggleSprint, on => ClientSettings.ToggleSprint = on)
            .Setting("bobblehead", () => ClientSettings.ViewBobbing, on => ClientSettings.ViewBobbing = on)
            .Setting("camerashake", 0, 100, 1, () => Math.Round(ClientSettings.CameraShakeStrength * 100),
                v => ClientSettings.CameraShakeStrength = (float)(v / 100), " %")
            .Setting("wireframethickness", 1, 16, 1, () => Math.Round(ClientSettings.Wireframethickness * 2),
                v => ClientSettings.Wireframethickness = (float)(v / 2))
            .Format(v => (v / 2).ToString("0.#", CultureInfo.InvariantCulture) + "x")
            .Choice(Name("wireframecolors"), [Lang.Get("Preset 1"), Lang.Get("Preset 2"), Lang.Get("Preset 3")],
                () => Math.Clamp(ClientSettings.guiColorsPreset - 1, 0, 2), i =>
                {
                    ClientSettings.guiColorsPreset = i + 1;
                    capi.ColorPreset?.OnUpdateSetting();
                }, Hover("wireframecolors"))
            .Setting("instabilityWavingStrength", 0, 150, 1, () => Math.Round(ClientSettings.InstabilityWavingStrength * 100),
                v => ClientSettings.InstabilityWavingStrength = (float)(v / 100), " %");
    }

    private static OptionPage Sound(string section)
    {
        var page = new OptionPage("vs-sound", Lang.Get("setting-sound-header"), section);
        string[] frequencies = Names("setting-musicfrequency-", "low", "medium", "often", "veryoften");
        _ = page.Slider(Name("mastersoundlevel"), 0, 100, 1, () => ClientSettings.MasterSoundLevel,
                v => ClientSettings.MasterSoundLevel = (int)v, "%")
            .Slider(Name("soundlevel"), 0, 100, 1, () => ClientSettings.SoundLevel, v => ClientSettings.SoundLevel = (int)v, "%")
            .Slider(Name("entitysoundlevel"), 0, 100, 1, () => ClientSettings.EntitySoundLevel,
                v => ClientSettings.EntitySoundLevel = (int)v, "%")
            .Slider(Name("ambientsoundlevel"), 0, 100, 1, () => ClientSettings.AmbientSoundLevel,
                v => ClientSettings.AmbientSoundLevel = (int)v, "%")
            .Slider(Name("weathersoundlevel"), 0, 100, 1, () => ClientSettings.WeatherSoundLevel,
                v => ClientSettings.WeatherSoundLevel = (int)v, "%")
            .Slider(Name("musiclevel"), 0, 100, 1, () => ClientSettings.MusicLevel, v => ClientSettings.MusicLevel = (int)v, "%")
            .Slider(Name("musicfrequency"), 0, 3, 1, () => ClientSettings.MusicFrequency, v => ClientSettings.MusicFrequency = (int)v)
            .Format(v => Named(frequencies, v))
            .Setting("hrtfmode", () => ClientSettings.UseHRTFAudio, on => ClientSettings.UseHRTFAudio = on);
        return Devices(page);
    }

    private static OptionPage Devices(OptionPage page)
    {
        var listed = ScreenManager.Platform?.AvailableAudioDevices?.Take(OptionPage.MaxOptions - 1).ToArray() ?? [];
        string?[] devices = [null, .. listed];
        string[] names = [T("default-device"), .. listed];
        return page.Choice(Name("audiooutputdevice"), names, () => Math.Max(0, Array.IndexOf(devices, ClientSettings.AudioDevice)),
            i => ClientSettings.AudioDevice = Index(i, devices.Length) ? devices[i] : null);
    }

    private static OptionPage Interface(ICoreClientAPI capi, string section, Action<string>? tab)
    {
        var page = new OptionPage("vs-interface", Lang.Get("setting-interface-header"), section);
        var largest = ScreenManager.Platform?.ScreenSize.Width > 3000 ? 24 : 16;
        _ = page.Setting("guiscale", 4, largest, 1, () => Math.Round(ClientSettings.GUIScale * 8),
                v => ClientSettings.GUIScale = (float)(v / 8))
            .Format(v => (v / 8).ToString("0.###", CultureInfo.InvariantCulture) + "x");
        _ = Languages(page);
        _ = page.Setting("autochat", () => ClientSettings.AutoChat, on => ClientSettings.AutoChat = on)
            .Setting("autochat-selected", () => ClientSettings.AutoChatOpenSelected,
                on => ClientSettings.AutoChatOpenSelected = on)
            .Setting("blockinfohud", () => ClientSettings.ShowBlockInfoHud, on => ClientSettings.ShowBlockInfoHud = on)
            .Setting("blockinteractioninfohud", () => ClientSettings.ShowBlockInteractionHelp,
                on => ClientSettings.ShowBlockInteractionHelp = on)
            .Setting("coordinatehud", () => ClientSettings.ShowCoordinateHud,
                on => ClientSettings.ShowCoordinateHud = on);
        _ = Hands(Minimap(capi, page));
        if (tab is null) return page; // opened on its own: the game's settings are not behind the screen
        return page.Group(T("group-more"))
            .Button(T("original"), T("open"), () => tab("OnGraphicsOptions"), T("original-hint"));
    }

    private static OptionPage Minimap(ICoreClientAPI capi, OptionPage page)
    {
        if (capi.World?.Config?.GetBool("allowMap", true) == false) return page;
        return page.Setting("minimaphud", () => capi.Settings.Bool["showMinimapHud"],
            on => capi.Settings.Bool["showMinimapHud"] = on);
    }

    private static OptionPage Hands(OptionPage page)
    {
        return page.Setting("immersivemousemode", () => ClientSettings.ImmersiveMouseMode,
                on => ClientSettings.ImmersiveMouseMode = on)
            .Setting("immersivefpmode", () => ClientSettings.ImmersiveFpMode, on => ClientSettings.ImmersiveFpMode = on)
            .Setting("fpmodeyoffset", -100, 10, 1, () => Math.Round(ClientSettings.FpHandsYOffset * 100),
                v => ClientSettings.FpHandsYOffset = (float)(v / 100))
            .Setting("fpmodefov", 70, 90, 1, () => ClientSettings.FpHandsFoV, v => ClientSettings.FpHandsFoV = (int)v, "°")
            .Setting("developermode", () => ClientSettings.DeveloperMode, DeveloperMode);
    }

    private static OptionPage? Developer(string section)
    {
        if (!ClientSettings.DeveloperMode) return null;
        return new OptionPage("vs-developer", Lang.Get("setting-dev-header"), section)
            .Setting("errorreporter", () => ClientSettings.StartupErrorDialog,
                on => ClientSettings.StartupErrorDialog = on)
            .Setting("extdebuginfo", () => ClientSettings.ExtendedDebugInfo,
                on => ClientSettings.ExtendedDebugInfo = on)
            .Setting("opengldebug", () => ClientSettings.GlDebugMode, on => ClientSettings.GlDebugMode = on)
            .Setting("openglerrorchecking", () => ClientSettings.GlErrorChecking,
                on => ClientSettings.GlErrorChecking = on)
            .Setting("debugtexturedispose", () => RuntimeEnv.DebugTextureDispose,
                on => RuntimeEnv.DebugTextureDispose = on)
            .Setting("debugvaodispose", () => RuntimeEnv.DebugVAODispose, on => RuntimeEnv.DebugVAODispose = on)
            .Setting("debugsounddispose", () => RuntimeEnv.DebugSoundDispose, on => RuntimeEnv.DebugSoundDispose = on)
            .Setting("fasterstartup", () => ClientSettings.OffThreadMipMapCreation,
                on => ClientSettings.OffThreadMipMapCreation = on);
    }

    private static OptionPage Languages(OptionPage page)
    {
        if (ScreenManager.Platform?.AssetManager is null) return page; // no game assets: not in a running client
        GuiCompositeSettings.getLanguages(out var gameCodes, out var gameNames);
        if (!Assert(gameCodes.Length == gameNames.Length) || !Assert(gameCodes.Length is > 0 and <= MaxLanguages)) return page;
        var sorted = gameCodes.Zip(gameNames, (code, name) => (Code: code, Name: Readable(name)))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).Take(OptionPage.MaxOptions).ToArray();
        var (codes, names) = (Array.ConvertAll(sorted, l => l.Code), Array.ConvertAll(sorted, l => l.Name));
        var restart = Lang.Get("setting-notice-restart");
        return page.Choice(Name("language"), names, () => Math.Max(0, Array.IndexOf(codes, ClientSettings.Language)),
            i => Language(Index(i, codes.Length) ? codes[i] : ClientSettings.Language), Hover("language") + "\n\n" + restart);
    }

    // "Deutsch / German (100% complete)" -> "Deutsch\tGerman": native name if the game's font draws it, else English
    // first
    internal static string Readable(string name)
    {
        if (!NotNull(name)) return "";
        var split = name.IndexOf(" / ", StringComparison.Ordinal);
        var own = (split < 0 ? name : name[..split]).Trim();
        var english = split < 0 ? own : name[(split + 3)..];
        var cut = english.IndexOf(" (", StringComparison.Ordinal);
        english = (cut > 0 ? english[..cut] : english).Trim();
        var main = Drawable(own) && own.Length > 0 ? own : english;
        var aside = main == own ? english : "";
        return Assert(main.Length > 0) && Drawable(aside) && aside.Length > 0 && aside != main ? main + "\t" + aside : main;
    }

    // Below Hebrew: scripts the game's font has glyphs for
    private static bool Drawable(string text) => text.All(c => c < '\u0590');

    // Language and fonts, saved at once. Windows takes system fonts for CJK and Thai; others keep sans-serif.
    private static void Language(string lang)
    {
        var startup = Lang.CurrentLocale ?? "en"; // the language the game runs in, the one it started with
        if (!Assert(lang.Length > 0)) return;
        ClientSettings.Language = lang;
        if (lang.StartsWith("zh-", StringComparison.Ordinal) || lang is "ar" or "ja" or "ko" or "th") LocalFonts(lang, startup);
        else
        {
            if (Array.IndexOf(LocalFontNames, ClientSettings.DefaultFontName) >= 0) ClientSettings.DefaultFontName = "sans-serif";
            if (ClientSettings.DefaultFontName == "sans-serif") ClientSettings.DecorativeFontName = "Lora";
        }

        _ = ClientSettings.Inst.Save(true);
    }

    private static readonly string[] LocalFontNames =
        ["meiryo", "Malgun Gothic", "Leelawadee UI Semilight", "Microsoft YaHei Light", "Microsoft JhengHei UI Light"];

    private static void LocalFonts(string lang, string startup)
    {
        if (RuntimeEnv.OS != OS.Windows)
        {
            if (lang != startup && ClientSettings.DefaultFontName == "sans-serif") ClientSettings.DecorativeFontName = "sans-serif";
            return;
        }

        (ClientSettings.DefaultFontName, ClientSettings.DecorativeFontName) = lang switch
        {
            "ko" => ("Malgun Gothic", "Malgun Gothic"),
            "th" => ("Leelawadee UI Semilight", "Leelawadee UI"),
            "ja" => ("meiryo", "meiryo"),
            "zh-cn" => ("Microsoft YaHei Light", "Microsoft YaHei"),
            "zh-tw" => ("Microsoft JhengHei UI Light", "Microsoft JhengHei UI"),
            _ => (ClientSettings.DefaultFontName, "sans-serif")
        };
    }

    // The platform switches raw input itself, no watcher does
    private static void DirectMouse(bool on)
    {
        ClientSettings.DirectMouseMode = on;
        if (NotNull(ScreenManager.Platform)) ScreenManager.Platform.SetDirectMouseMode(on);
    }

    // Off drops every debug setting (the game asks before on; here Apply does)
    private static void DeveloperMode(bool on)
    {
        ClientSettings.DeveloperMode = on;
        if (on) return;
        (ClientSettings.StartupErrorDialog, ClientSettings.ExtendedDebugInfo) = (false, false);
        (ClientSettings.GlDebugMode, ClientSettings.GlErrorChecking) = (false, false);
        (RuntimeEnv.DebugTextureDispose, RuntimeEnv.DebugVAODispose, RuntimeEnv.DebugSoundDispose) = (false, false, false);
    }
}

// OnControlOptions and the mouse tab (1.22.7) as one page. Keys apply at once; the locked-modifiers switch waits for
// Apply.
internal static partial class EngineOptions
{
    private const int MaxKeys = OptionPage.MaxRows - 16; // the headings, the switch and the buttons besides

    // An unbound key: no key or mouse button has this code, yet the engine indexes its 512-entry key states with it
    // (SystemPlayerControl, UpdateFreeMouse, IsHotKeyPressed), where -1 would throw
    internal const int NoKey = 511;

    private static readonly (HotkeyType Type, string Title)[] KeyKinds =
    [
        (HotkeyType.MovementControls, "Movement controls"), (HotkeyType.MouseModifiers, "Mouse click modifiers"),
        (HotkeyType.CharacterControls, "Actions"), (HotkeyType.HelpAndOverlays, "In-game Help and Overlays"),
        (HotkeyType.GUIOrOtherControls, "User interface & More"), (HotkeyType.InventoryHotkeys, "Inventory hotkeys"),
        (HotkeyType.CreativeOrSpectatorTool, "Creative mode"), (HotkeyType.CreativeTool, ""), (HotkeyType.DevTool, "Debug and Macros"),
        (HotkeyType.MouseControls, "mouseactions")
    ];

    private static readonly string[] Announced = ["shift", "ctrl", "primarymouse", "secondarymouse", "toolmodeselect"];

    private static KeyMap? HotKeys => ScreenManager.hotkeyManager?.HotKeys; // null without a running game (tests)

    private static Dictionary<string, string>? _clashes; // code -> the name of another key bound the same; rebuilt after each change
    private static bool _armed; // the reset asked once and waits for its second click

    private static OptionPage? Controls(string section, Action<string>? tab)
    {
        if (HotKeys is not { } keys) return null;
        (_clashes, _armed) = (null, false);
        var page = new OptionPage("vs-controls", Lang.Get("setting-controls-header"), section) { Capacity = OptionPage.MaxRows }
            .Setting("noseparatectrlkeys", () => !ClientSettings.SeparateCtrl, Locked);
        var hint = T("key-hint");
        foreach (var (type, title) in KeyKinds.Bounded(KeyKinds.Length))
        {
            var modifiers = type == HotkeyType.MouseModifiers;
            var kind = keys.Values.Where(k => k.KeyCombinationType == type).Take(MaxKeys).ToArray();
            if (kind.Length == 0) continue;
            if (title.Length > 0) _ = page.Group(Lang.Get(title));
            foreach (var key in kind.Bounded(MaxKeys))
            {
                var code = key.Code;
                _ = page.Key(key.Name ?? code, code, () => Mapping(code), () => Clash(code).Length > 0, hint);
                if (modifiers) _ = page.EnabledWhen(() => ClientSettings.SeparateCtrl); // locked to sneak and sprint otherwise
            }
        }

        _ = page.Group(T("group-more"))
            .Button(Lang.Get("setting-name-setdefault"), () => _armed ? Lang.Get("Confirm") : T("reset"), ResetKeys,
                Lang.Get("Really reset key controls to default settings?") + "\n\n" + T("reset-hint"));
        return tab is null ? page : page.Button(Lang.Get("setting-name-macroeditor"), T("open"), () => tab("OnMacroEditor"));
    }

    private static string Mapping(string code)
    {
        if (!NotNull(code) || HotKeys is not { } keys || !keys.TryGetValue(code, out var key)) return "";
        if (key.CurrentMapping?.KeyCode == NoKey) return T("key-none");
        var (clash, shown) = (Clash(code), key.CurrentMapping?.ToString() ?? "?");
        return clash.Length > 0 ? shown + Lang.Get("keybind-conflict-sameas", clash) : shown;
    }

    internal static string Clash(string code)
    {
        _clashes ??= Clashes();
        if (!NotNull(code)) return "";
        return _clashes.TryGetValue(code, out var other) ? other : "";
    }

    // The game's clash check (a mouse modifier beside sneak/sprint is by design), comparing parts, not shown text.
    private static Dictionary<string, string> Clashes()
    {
        Dictionary<string, string> clashes = [];
        if (HotKeys is not { } keys) return clashes;
        Dictionary<(int, int?, bool, bool, bool), HotKey> seen = [];
        foreach (var key in keys.Values.Take(OptionPage.MaxRows).Bounded(OptionPage.MaxRows))
        {
            var type = key.KeyCombinationType;
            if (type == HotkeyType.MouseControls || (type == HotkeyType.MouseModifiers && !ClientSettings.SeparateCtrl) ||
                key.CurrentMapping?.KeyCode == NoKey) continue; // unbound keys clash with nothing
            var shown = key.CurrentMapping is { } m ? (m.KeyCode, m.SecondKeyCode, m.Ctrl, m.Alt, m.Shift) : (-1, null, false, false, false);
            if (seen.TryGetValue(shown, out var other) && !Paired(key, other))
            {
                clashes[key.Code] = other.Name;
                _ = clashes.TryAdd(other.Code, key.Name);
            }

            seen[shown] = key;
        }

        return clashes;
    }

    private static bool Paired(HotKey a, HotKey b)
    {
        var (modifier, other) = a.KeyCombinationType == HotkeyType.MouseModifiers ? (a, b) : (b, a);
        return modifier.KeyCombinationType == HotkeyType.MouseModifiers && other.Code is "sneak" or "sprint";
    }

    // The game's CompletedCapture: shift and ctrl follow sneak and sprint while locked
    internal static void Bind(string code, KeyCombination? combination)
    {
        if (HotKeys is not { } keys || combination is null || !NotNull(code) || !keys.TryGetValue(code, out var key)) return;
        var bound = key.Clone();
        bound.CurrentMapping = combination;
        keys[code] = bound;
        ClientSettings.Inst.SetKeyMapping(code, combination);
        var follower = ClientSettings.SeparateCtrl ? null : code switch { "sneak" => "shift", "sprint" => "ctrl", _ => null };
        if (follower is not null && keys.TryGetValue(follower, out var follows)) follows.CurrentMapping = combination;
        if (follower is not null || Array.IndexOf(Announced, code) >= 0) (_capi?.Event as ClientEventAPI)?.TriggerHotkeysChanged();
        _clashes = null;
    }

    // No key at all: the key does nothing until bound again
    internal static void Unbind(string code)
    {
        if (NotNull(code) && Assert(code.Length > 0)) Bind(code, new KeyCombination { KeyCode = NoKey });
    }

    internal static bool Unbound(string code) =>
        NotNull(code) && Assert(code.Length > 0) && HotKeys is { } keys && keys.TryGetValue(code, out var key) && key.CurrentMapping?.KeyCode == NoKey;

    // The game's own texts (interaction help, its controls tab) name an unbound key as such, not as key 511
    internal static void NameUnbound(Harmony harmony)
    {
        var name = AccessTools.DeclaredMethod(typeof(KeyCombination), nameof(KeyCombination.ToString), []);
        if (NotNull(harmony) && NotNull(name)) _ = harmony.Patch(name, new HarmonyMethod(AccessTools.Method(typeof(EngineOptions), nameof(NamedUnbound))));
    }

    // Prefix: the game's own naming runs for every bound key
    private static bool NamedUnbound(KeyCombination __instance, ref string __result)
    {
        if (!NotNull(__instance) || __instance.KeyCode != NoKey) return true;
        __result = T("key-none");
        return false;
    }

    // The game's default binding back
    internal static void Defaulted(string code)
    {
        if (!Assert(code is { Length: > 0 })) return;
        if (HotKeys is { } keys && keys.TryGetValue(code, out var key) && key.DefaultMapping is { } fallback)
            Bind(code, fallback.Clone());
    }

    // The game's defaults as the rows read them: a fresh ClientSettings filled by LoadDefaultValues stands in for the
    // player's while the getters run (its static setters write to Inst, so it must be Inst while it fills); the player's
    // is back before anything else reads it. NaN where a getter fails.
    internal static double[] Defaults(IReadOnlyList<OptionRow> rows)
    {
        var values = new double[Math.Min(rows?.Count ?? 0, OptionPage.MaxRows)];
        Array.Fill(values, double.NaN);
        var live = ClientSettings.Inst;
        if (rows is null || !NotNull(live) || Activator.CreateInstance(typeof(ClientSettings), true) is not ClientSettings fresh) return values;
        try
        {
            ClientSettings.Inst = fresh;
            fresh.LoadDefaultValues();
            for (var i = 0; i < Math.Min(values.Length, OptionPage.MaxRows); i++) values[i] = Read(rows[i]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _capi?.Logger?.Warning("Komet options: the game's defaults could not be read: {0}", e.Message);
        }
        finally
        {
            ClientSettings.Inst = live;
        }

        return values;
    }

    private static double Read(OptionRow row)
    {
        if (!NotNull(row) || row.Kind is OptionKind.Group or OptionKind.Button or OptionKind.Key) return double.NaN;
        try
        {
            return row.Get();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return double.NaN;
        }
    }

    private static void ResetKeys()
    {
        var keys = HotKeys;
        if (!_armed || keys is null)
        {
            _armed = keys is not null;
            return;
        }

        ClientSettings.KeyMapping.Clear();
        foreach (var key in keys.Values.Take(OptionPage.MaxRows).Bounded(OptionPage.MaxRows))
        {
            key.CurrentMapping = key.DefaultMapping?.Clone();
            ClientSettings.Inst.SetKeyMapping(key.Code, key.CurrentMapping);
        }

        (_armed, _clashes) = (false, null);
        (_capi?.Event as ClientEventAPI)?.TriggerHotkeysChanged();
    }

    private static void Locked(bool locked)
    {
        if (HotKeys is not { } keys) return;
        ClientSettings.SeparateCtrl = !locked;
        foreach (var (modifier, leader, own) in new[] { ("shift", "sneak", 1), ("ctrl", "sprint", 3) }.Bounded(2))
        {
            if (!keys.TryGetValue(modifier, out var key)) continue;
            key.CurrentMapping = locked && keys.TryGetValue(leader, out var lead) ? lead.CurrentMapping : new KeyCombination { KeyCode = own };
            ClientSettings.Inst.SetKeyMapping(modifier, key.CurrentMapping);
        }

        _clashes = null;
    }
}
