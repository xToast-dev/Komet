using System.Globalization;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Options.KometPages;

namespace Komet.Options;

// The game's other tabs (GuiCompositeSettings.OnMouseOptions, OnAccessibilityOptions, OnSoundOptions, OnInterfaceOptions,
// OnDeveloperOptions, Vintage Story 1.22.7), each control with its handler's conversions and side effects; the engine's watchers on
// ClientSettings (volume, audio device, GUI scale, GL debug) do the rest. The controls are a page of their own, and the game's graphics
// tab as it was is a button at the end of the interface page. A language takes effect at the next start, as in the game.
internal static partial class EngineOptions
{
    private static OptionPage Mouse(string section)
    {
        var page = new OptionPage("vs-mouse", Lang.Get("setting-mouse-header"), section);
        if (!NotNull(section) || !Assert(section.Length > 0)) return page;
        return page.Slider(Name("mousesensivity"), 1, 200, 5, () => ClientSettings.MouseSensivity,
                v => ClientSettings.MouseSensivity = (int)v)
            .Slider(Name("mousesmoothing"), 0, 95, 5, () => Math.Clamp(100 - ClientSettings.MouseSmoothing, 0, 95),
                v => ClientSettings.MouseSmoothing = 100 - (int)v)
            .Slider(Name("mousewheelsensivity"), 1, 100, 1, () => Math.Round(ClientSettings.MouseWheelSensivity * 10),
                v => ClientSettings.MouseWheelSensivity = (float)(v / 10))
            .Format(v => (v / 10).ToString("0.#", CultureInfo.InvariantCulture) + "x")
            .Switch(Name("directmousemode"), () => ClientSettings.DirectMouseMode, DirectMouse, Hover("directmousemode"))
            .Switch(Name("invertyaxis"), () => ClientSettings.InvertMouseYAxis, on => ClientSettings.InvertMouseYAxis = on)
            .Choice(Name("itemCollectMode"), [Lang.Get("Always collect items"), Lang.Get("Only collect items when sneaking")],
                () => ClientSettings.ItemCollectMode, i => ClientSettings.ItemCollectMode = i);
    }

    private static OptionPage Accessibility(ICoreClientAPI capi, string section)
    {
        var page = new OptionPage("vs-accessibility", Lang.Get("setting-accessibility-header"), section);
        if (!NotNull(capi) || !Assert(section.Length > 0)) return page;
        return page.Switch(Name("togglesprint"), () => ClientSettings.ToggleSprint, on => ClientSettings.ToggleSprint = on,
                Hover("togglesprint"))
            .Switch(Name("bobblehead"), () => ClientSettings.ViewBobbing, on => ClientSettings.ViewBobbing = on, Hover("bobblehead"))
            .Slider(Name("camerashake"), 0, 100, 1, () => Math.Round(ClientSettings.CameraShakeStrength * 100),
                v => ClientSettings.CameraShakeStrength = (float)(v / 100), " %", Hover("camerashake"))
            .Slider(Name("wireframethickness"), 1, 16, 1, () => Math.Round(ClientSettings.Wireframethickness * 2),
                v => ClientSettings.Wireframethickness = (float)(v / 2), "", Hover("wireframethickness"))
            .Format(v => (v / 2).ToString("0.#", CultureInfo.InvariantCulture) + "x")
            .Choice(Name("wireframecolors"), [Lang.Get("Preset 1"), Lang.Get("Preset 2"), Lang.Get("Preset 3")],
                () => Math.Clamp(ClientSettings.guiColorsPreset - 1, 0, 2), i =>
                {
                    ClientSettings.guiColorsPreset = i + 1;
                    capi.ColorPreset?.OnUpdateSetting();
                }, Hover("wireframecolors"))
            .Slider(Name("instabilityWavingStrength"), 0, 150, 1, () => Math.Round(ClientSettings.InstabilityWavingStrength * 100),
                v => ClientSettings.InstabilityWavingStrength = (float)(v / 100), " %", Hover("instabilityWavingStrength"));
    }

    private static OptionPage Sound(string section)
    {
        var page = new OptionPage("vs-sound", Lang.Get("setting-sound-header"), section);
        string[] frequencies = Names("setting-musicfrequency-", "low", "medium", "often", "veryoften");
        if (!NotNull(section) || !Assert(frequencies.Length == 4)) return page;
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
            .Switch(Name("hrtfmode"), () => ClientSettings.UseHRTFAudio, on => ClientSettings.UseHRTFAudio = on, Hover("hrtfmode"));
        return Devices(page);
    }

    // "Default" (no device named, the system's) and the devices OpenAL lists
    private static OptionPage Devices(OptionPage page)
    {
        var listed = ScreenManager.Platform?.AvailableAudioDevices?.Take(OptionPage.MaxOptions - 1).ToArray() ?? [];
        string?[] devices = [null, .. listed];
        string[] names = [T("default-device"), .. listed];
        if (!NotNull(page) || !Assert(names.Length == devices.Length)) return page;
        return page.Choice(Name("audiooutputdevice"), names, () => Math.Max(0, Array.IndexOf(devices, ClientSettings.AudioDevice)),
            i => ClientSettings.AudioDevice = Index(i, devices.Length) ? devices[i] : null);
    }

    private static OptionPage Interface(ICoreClientAPI capi, string section, Action<string>? tab)
    {
        var page = new OptionPage("vs-interface", Lang.Get("setting-interface-header"), section);
        var largest = ScreenManager.Platform?.ScreenSize.Width > 3000 ? 24 : 16;
        if (!NotNull(capi) || !Assert(largest >= 16)) return page;
        _ = page.Slider(Name("guiscale"), 4, largest, 1, () => Math.Round(ClientSettings.GUIScale * 8),
                v => ClientSettings.GUIScale = (float)(v / 8), "", Hover("guiscale"))
            .Format(v => (v / 8).ToString("0.###", CultureInfo.InvariantCulture) + "x");
        _ = Languages(page);
        _ = page.Switch(Name("autochat"), () => ClientSettings.AutoChat, on => ClientSettings.AutoChat = on, Hover("autochat"))
            .Switch(Name("autochat-selected"), () => ClientSettings.AutoChatOpenSelected,
                on => ClientSettings.AutoChatOpenSelected = on, Hover("autochat-selected"))
            .Switch(Name("blockinfohud"), () => ClientSettings.ShowBlockInfoHud, on => ClientSettings.ShowBlockInfoHud = on,
                Hover("blockinfohud"))
            .Switch(Name("blockinteractioninfohud"), () => ClientSettings.ShowBlockInteractionHelp,
                on => ClientSettings.ShowBlockInteractionHelp = on, Hover("blockinteractioninfohud"))
            .Switch(Name("coordinatehud"), () => ClientSettings.ShowCoordinateHud, on => ClientSettings.ShowCoordinateHud = on,
                Hover("coordinatehud"));
        _ = Hands(capi, Minimap(capi, page));
        if (tab is null) return page; // opened on its own: the game's settings are not behind the screen
        return page.Group(T("group-more"))
            .Button(T("original"), T("open"), () => tab("OnGraphicsOptions"), T("original-hint"));
    }

    // The minimap row only where the world allows a map, as the game's
    private static OptionPage Minimap(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page) || capi.World?.Config?.GetBool("allowMap", true) == false) return page;
        return page.Switch(Name("minimaphud"), () => capi.Settings.Bool["showMinimapHud"], on => capi.Settings.Bool["showMinimapHud"] = on,
            Hover("minimaphud"));
    }

    private static OptionPage Hands(ICoreClientAPI capi, OptionPage page)
    {
        if (!NotNull(capi) || !NotNull(page)) return page;
        return page.Switch(Name("immersivemousemode"), () => ClientSettings.ImmersiveMouseMode,
                on => ClientSettings.ImmersiveMouseMode = on, Hover("immersivemousemode"))
            .Switch(Name("immersivefpmode"), () => ClientSettings.ImmersiveFpMode, on => ClientSettings.ImmersiveFpMode = on,
                Hover("immersivefpmode"))
            .Slider(Name("fpmodeyoffset"), -100, 10, 1, () => Math.Round(ClientSettings.FpHandsYOffset * 100),
                v => ClientSettings.FpHandsYOffset = (float)(v / 100), "", Hover("fpmodeyoffset"))
            .Slider(Name("fpmodefov"), 70, 90, 1, () => ClientSettings.FpHandsFoV, v => ClientSettings.FpHandsFoV = (int)v, "°",
                Hover("fpmodefov"))
            .Switch(Name("developermode"), () => ClientSettings.DeveloperMode, DeveloperMode, Hover("developermode"));
    }

    // Only while developer mode is on, as the game's tab
    private static OptionPage? Developer(string section)
    {
        if (!NotNull(section) || !ClientSettings.DeveloperMode || !Assert(section.Length > 0)) return null;
        return new OptionPage("vs-developer", Lang.Get("setting-dev-header"), section)
            .Switch(Name("errorreporter"), () => ClientSettings.StartupErrorDialog, on => ClientSettings.StartupErrorDialog = on,
                Hover("errorreporter"))
            .Switch(Name("extdebuginfo"), () => ClientSettings.ExtendedDebugInfo, on => ClientSettings.ExtendedDebugInfo = on,
                Hover("extdebuginfo"))
            .Switch(Name("opengldebug"), () => ClientSettings.GlDebugMode, on => ClientSettings.GlDebugMode = on, Hover("opengldebug"))
            .Switch(Name("openglerrorchecking"), () => ClientSettings.GlErrorChecking, on => ClientSettings.GlErrorChecking = on,
                Hover("openglerrorchecking"))
            .Switch(Name("debugtexturedispose"), () => RuntimeEnv.DebugTextureDispose, on => RuntimeEnv.DebugTextureDispose = on,
                Hover("debugtexturedispose"))
            .Switch(Name("debugvaodispose"), () => RuntimeEnv.DebugVAODispose, on => RuntimeEnv.DebugVAODispose = on,
                Hover("debugvaodispose"))
            .Switch(Name("debugsounddispose"), () => RuntimeEnv.DebugSoundDispose, on => RuntimeEnv.DebugSoundDispose = on,
                Hover("debugsounddispose"))
            .Switch(Name("fasterstartup"), () => ClientSettings.OffThreadMipMapCreation,
                on => ClientSettings.OffThreadMipMapCreation = on, Hover("fasterstartup"));
    }

    // The languages the game ships (lang/languages.json), as "Name / English name"; the change shows after a restart
    private static OptionPage Languages(OptionPage page)
    {
        if (!NotNull(page) || ScreenManager.Platform?.AssetManager is null) return page; // no game assets: not in a running client
        GuiCompositeSettings.getLanguages(out var gameCodes, out var gameNames);
        if (!Assert(gameCodes.Length == gameNames.Length) || !Assert(gameCodes.Length is > 0 and <= MaxLanguages)) return page;
        var sorted = gameCodes.Zip(gameNames, (code, name) => (Code: code, Name: Readable(name)))
            .OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var (codes, names) = (Array.ConvertAll(sorted, l => l.Code), Array.ConvertAll(sorted, l => l.Name));
        var restart = Lang.Get("setting-notice-restart");
        return page.Choice(Name("language"), names, () => Math.Max(0, Array.IndexOf(codes, ClientSettings.Language)),
            i => Language(Index(i, codes.Length) ? codes[i] : ClientSettings.Language), Hover("language") + "\n\n" + restart);
    }

    // "Deutsch / German (100% complete)" as "Deutsch", a tab, "German": the name the language gives itself where the game's font can
    // draw its script (Latin, Greek, Cyrillic), the English one otherwise; the other name after the tab, no completeness
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

    // Below Hebrew: the scripts the game's font has glyphs for
    private static bool Drawable(string text) => NotNull(text) && text.All(c => c < '\u0590');

    // What the game's language handler does, less its notice text: the language and the fonts it needs, saved at once. Windows
    // takes the system's fonts for Chinese, Japanese, Korean and Thai; other systems keep sans-serif for them.
    private static void Language(string lang)
    {
        var startup = Lang.CurrentLocale ?? "en"; // the language the game runs in, the one it started with
        if (!NotNull(lang) || !Assert(lang.Length > 0)) return;
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
        if (!NotNull(lang) || !NotNull(startup)) return;
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

    // onDirectMouseModeToggled: the platform switches raw mouse input itself, no watcher does
    private static void DirectMouse(bool on)
    {
        ClientSettings.DirectMouseMode = on;
        _ = Assert(ClientSettings.DirectMouseMode == on);
        if (NotNull(ScreenManager.Platform)) ScreenManager.Platform.SetDirectMouseMode(on);
    }

    // onDeveloperModeChanged: switched off, every debug setting goes with it (the game asks before switching on; here Apply does)
    private static void DeveloperMode(bool on)
    {
        ClientSettings.DeveloperMode = on;
        if (on) return;
        (ClientSettings.StartupErrorDialog, ClientSettings.ExtendedDebugInfo) = (false, false);
        (ClientSettings.GlDebugMode, ClientSettings.GlErrorChecking) = (false, false);
        (RuntimeEnv.DebugTextureDispose, RuntimeEnv.DebugVAODispose, RuntimeEnv.DebugSoundDispose) = (false, false, false);
        _ = Assert(!ClientSettings.DeveloperMode);
    }
}
