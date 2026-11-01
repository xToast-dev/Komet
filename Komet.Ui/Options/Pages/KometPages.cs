using System.Globalization;
using Komet.Api;
using Komet.Core;
using Komet.Hud;
using Komet.Rendering;
using Vintagestory.API.Util;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// The options screen's own texts: Komet's lang keys under "settings-"
internal static class OptionsText
{
    internal static string T(string key, params object[] args) =>
        KometText.Translate("settings-" + key, args);
}

// What the Komet pages' buttons do, handed in by the HUD: copy the values, run the benchmark for a length in seconds, show
// the checksum, open the window or the debug window
internal sealed record KometActions(Action Dump, Action<float> Bench, Action Verify, Action Window, Action Debug);

// Komet's pages, built each time the screen takes its pages (a language change shows at once). Three tiers: the
// Overview's preset sets the trade-offs; each area's page lists those trade-offs singly (Weighed); with "advanced" the
// Exact page adds every engine-exact switch under masters, so nothing is reachable only in komet.json.
internal sealed class KometPages(HudSettings settings, KometActions actions)
{
    public const string Overview = "komet-overview", ExactId = "komet-exact", Hud = "komet-hud", Style = "komet-style", Tools = "komet-tools",
        Templates = "modding-templates";
    private const int MaxNeeds = 8; // how far a chain of Knob.Needs is followed

    private static readonly string[] Corners = ["topleft", "topright", "bottomleft", "bottomright"];

    // Knob pages in the page list's order; every knob's page is one of these (OptionsTests)
    private static readonly string[] Areas = ["render", "chunks", "misc", "vulkan"];

    private string? _armed; // the reset that asked once and waits for its second click

    public bool Advanced => settings.ShowAdvanced;

    // The styles the Style page offers; the screen sets it
    public StyleLibrary? Styles { get; set; }

    // For the templates' assets; the screen sets it
    public ICoreClientAPI? Api { get; set; }

    public OptionPage[] Build()
    {
        var section = T("section-komet");
        _armed = null;
        List<OptionPage> pages = [OverviewPage(section)];
        foreach (var area in Areas.Bounded(Areas.Length)) pages.Add(AreaPage(area, section));
        if (settings.ShowAdvanced) pages.Add(ExactPage(section));
        pages.Add(HudPage(section));
        if (Styles is not null) pages.Add(StylePage(section)); // the screen's library: none outside a screen
        pages.Add(ToolsPage(section));
        return [.. pages];
    }

    // The preset with this computer's facts, the renderer, the preferences. "Adjusted" only shows; it is not offered.
    private OptionPage OverviewPage(string section)
    {
        var s = settings.Knobs;
        var page = new OptionPage(Overview, T("page-overview"), section);
        var hw = HardwareProfile.Found;
        var names = Array.ConvertAll(Presets.Names, name => T("preset-" + name));
        _ = page.Group(T("preset-group"))
            .Choice(T("preset"), [.. names, T("preset-adjusted")],
                () => s.Adjusted ? Presets.Names.Length : Array.IndexOf(Presets.Names, s.Preset),
                choice => { if (Index(choice, Presets.Names.Length)) s.ChoosePreset(Presets.Names[choice]); },
                T("komet-preset-hint"))
            .Offering(Presets.Names.Length)
            .Button(T("hardware", hw.Cores, Known(hw.RamGb), Known(hw.VramGb), Known(hw.RefreshHz)), T("do-detected"), s.UseDetected,
                T("detected-hint"));
        _ = ModeRow(page.Group(() => T("renderer-group", VulkanMode.Renderer())));
        _ = page.Group(T("general"));
        var knobs = Knobs.BuiltIn; // Knob.General: preferences, not quality against speed
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].General && knobs[i].Page is not null)
                _ = Row(page, knobs[i], i);
        return page;
    }

    // An area's trade-offs by group; its exact switches live on the Exact page
    private OptionPage AreaPage(string area, string section)
    {
        var page = new OptionPage("komet-" + area, T("page-" + area), section) { Capacity = OptionPage.MaxRows };
        var knobs = Knobs.BuiltIn;
        List<int> weighed = [];
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page == area && Single(knobs[i]) && knobs[i].Group is not null)
                weighed.Add(i);
        Grouped(page, weighed);
        if (page.Count == 0) _ = page.Group(T("page-" + area)); // nothing weighed here: the page stands for its exact switches
        return Features.AddRows(page, true);
    }

    // The knobs' rows under a heading per group, the groups in the order they first appear
    private void Grouped(OptionPage page, List<int> members)
    {
        var knobs = Knobs.BuiltIn;
        List<string?> groups = [];
        foreach (var i in members.Bounded(Knobs.MaxKnobs))
            if (!groups.Contains(knobs[i].Group))
                groups.Add(knobs[i].Group);
        foreach (var group in groups.Bounded(Knobs.MaxKnobs))
        {
            if (group is not null) _ = page.Group(T(group));
            foreach (var i in members.Bounded(Knobs.MaxKnobs))
                if (knobs[i].Group == group)
                    _ = Row(page, knobs[i], i);
        }
    }

    // Knob.Weighed: a trade-off a player weighs, a row of its own
    private static bool Single(Knob knob) => knob.Page is not null && knob.Weighed && !knob.General;

    // Every other switch only makes the engine's own result faster: under the area's master switch
    private static bool IsExact(Knob knob) =>
        knob.Page is not null && knob.IsSwitch && !knob.Weighed && !knob.General;

    // Every engine-exact switch on one page: a master over all, then per area its own master and its switches in group
    // order. A switch turned off is marked (it costs speed and is worth naming in a bug report); with no row chosen the
    // detail sums up how many are on and which are off.
    private OptionPage ExactPage(string section)
    {
        var page = new OptionPage(ExactId, T("page-exact"), section) { Capacity = OptionPage.MaxRows };
        var knobs = Knobs.BuiltIn;
        var s = settings.Knobs;
        List<int> all = [];
        foreach (var area in Areas.Bounded(Areas.Length))
            all.AddRange(ExactOf(area));
        if (all.Count == 0) return page;
        _ = page.Group(T("exact-overview"))
            .Switch(T("exact-all", all.Count), () => all.TrueForAll(i => s.Knob(i) != 0),
                on => all.ForEach(i => s.SetKnob(i, on ? 1 : 0)), T("exact-all-hint"));
        foreach (var area in Areas.Bounded(Areas.Length))
        {
            var members = ExactOf(area);
            if (members.Count == 0) continue;
            _ = page.Group(T("page-" + area))
                .Switch(T("exact-area", T("page-" + area), members.Count), () => members.TrueForAll(i => s.Knob(i) != 0),
                    on => members.ForEach(i => s.SetKnob(i, on ? 1 : 0)), T("exact-area-hint"));
            foreach (var i in members.Bounded(Knobs.MaxKnobs))
            {
                var (index, group) = (i, knobs[i].Group is { } g ? T(g) : T("page-" + area));
                _ = Row(page, knobs[i], i)
                    .Tagged(() => s.Knob(index) != 0 ? [] : [(TagIcon.Warning, TagKind.Warn)])
                    .Card(() => [(TagIcon.Comet, TagKind.Plain, T("exact-fact-group", group))]);
            }
        }

        page.Legend = () => ExactLegend(all);
        return page;
    }

    // The area's exact switches in group order (the order the groups first appear)
    private static List<int> ExactOf(string area)
    {
        var knobs = Knobs.BuiltIn;
        List<int> found = [];
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page == area && IsExact(knobs[i]))
                found.Add(i);
        List<string?> groups = [];
        foreach (var i in found.Bounded(Knobs.MaxKnobs))
            if (!groups.Contains(knobs[i].Group))
                groups.Add(knobs[i].Group);
        List<int> ordered = [];
        foreach (var group in groups.Bounded(Knobs.MaxKnobs))
            foreach (var i in found.Bounded(Knobs.MaxKnobs))
                if (knobs[i].Group == group)
                    ordered.Add(i);
        return ordered;
    }

    // How many are on; each one turned off by name
    private List<(TagIcon Icon, TagKind Kind, string Text)> ExactLegend(List<int> all)
    {
        var knobs = Knobs.BuiltIn;
        var s = settings.Knobs;
        var off = all.FindAll(i => s.Knob(i) == 0);
        List<(TagIcon, TagKind, string)> legend =
        [
            (TagIcon.Comet, TagKind.Heading, T("page-exact")),
            (TagIcon.Comet, off.Count == 0 ? TagKind.TakesOver : TagKind.Plain, T("exact-on", all.Count - off.Count, all.Count)),
            (TagIcon.Comet, TagKind.Plain, T("exact-about"))
        ];
        if (off.Count == 0) return legend;
        legend.Add((TagIcon.Warning, TagKind.Heading, T("exact-off-heading")));
        foreach (var i in off.Bounded(Knobs.MaxKnobs))
            legend.Add((TagIcon.Warning, TagKind.Warn, T(knobs[i].Label)));
        return legend;
    }

    private OptionPage HudPage(string section)
    {
        var s = settings;
        return new OptionPage(Hud, T("page-hud"), section).Group(T("display"))
            .Switch(T("visible"), () => s.Visible, on => s.Visible = on)
            .Choice(T("corner"), Array.ConvertAll(Corners, c => T("corner-" + c)), () => (int)s.Corner, i => s.Corner = (HudCorner)i)
            .Slider(T("opacity"), HudSettings.OpacityRange, () => s.Opacity, v => s.Opacity = v)
            .Slider(T("scale"), HudSettings.ScaleRange, () => s.FontScale, v => s.FontScale = v)
            .Slider(T("interval"), HudSettings.IntervalRange, () => s.Interval, v => s.Interval = v)
            .Switch(T("overlayfps"), () => s.ShowFps, on => s.ShowFps = on)
            .Switch(T("overlaylows"), () => s.ShowLows, on => s.ShowLows = on)
            .Switch(T("overlayframetime"), () => s.ShowFrametime, on => s.ShowFrametime = on)
            .Switch(T("overlaygraph"), () => s.ShowGraph, on => s.ShowGraph = on)
            .Switch(T("overlaymods"), () => s.ShowMods, on => s.ShowMods = on)
            .Slider(T("toast"), HudSettings.ToastRange, () => s.ToastMs, v => s.ToastMs = v)
            .Group(T("windows"))
            .Button(T("window"), T("do-open"), actions.Window)
            .Button(T("debugwindow"), T("do-open"), actions.Debug);
    }

    // The screen's look, live as it changes; for designers the folder, a copy of the style shown and a reload
    private OptionPage StylePage(string section)
    {
        var (s, page) = (settings, new OptionPage(Style, T("page-style"), section));
        if (Styles is not { } styles) return page;
        var choices = styles.Choices();
        var motions = new[] { T("motion-off"), T("motion-calm"), T("motion-full") };
        _ = page.Group(T("look"))
            .Choice(T("style"), Array.ConvertAll(choices, c => c.Name), () => Math.Max(0, Array.FindIndex(choices, c => c.Id == s.MenuStyle)),
                i => { if (Index(i, choices.Length)) s.MenuStyle = choices[i].Id; }, T("style-hint")).Live()
            .Slider(T("menu-opacity"), HudSettings.MenuOpacityRange, () => s.MenuOpacity, v => s.MenuOpacity = v).Live()
            .Choice(T("motion"), motions, () => s.MenuMotion, i => s.MenuMotion = i, T("motion-hint")).Live()
            .Group(T("own-styles"))
            .Button(T("style-folder"), T("do-open"), () => OpenFolder(StyleLibrary.UserFolder), T("style-folder-hint", ShaderPacks.Shown(StyleLibrary.UserFolder)))
            .Button(T("style-template"), T("do-copy-style"), () =>
            {
                if (styles.Template(s.MenuStyle) is not { } folder) return;
                StylesReloaded?.Invoke();
                s.MenuStyle = Path.GetFileName(folder);
                OpenFolder(folder);
            }, T("style-template-hint"))
            .Button(T("style-reload"), T("do-reload"), () => StylesReloaded?.Invoke(), T("style-reload-hint"));
        return page;
    }

    // Modding › Templates: every example Komet writes on request, next to the guides that start from them. Without a mod
    // of your own (a shader pack, a style: they show at once), then whole mods to copy into the Mods folder.
    public OptionPage TemplatesPage(string section)
    {
        var (s, page) = (settings, new OptionPage(Templates, T("page-templates"), section));
        _ = page.Group(T("templates-own"))
            .Button(T("add-example-pack"), T("do-create"), () =>
            {
                if (Api is { } api && KometExamples.Create(api, KometExamples.Shader) is not null) ShaderOverview.Reload(api);
            }, T("add-example-pack-hint", ShaderPacks.Shown(ShaderPacks.UserFolder)))
            .Button(T("add-example-style"), T("do-create"), () =>
            {
                if (Api is not { } api || KometExamples.Create(api, KometExamples.Style) is not { } folder) return;
                StylesReloaded?.Invoke();
                s.MenuStyle = Path.GetFileName(folder); // the example's id is its folder's name
            }, T("add-example-style-hint", ShaderPacks.Shown(StyleLibrary.UserFolder)))
            .Group(T("templates-mods"))
            .Button(T("add-mod-template"), T("do-create"), () => Example(KometExamples.Mod),
                T("add-mod-template-hint", ShaderPacks.Shown(KometExamples.ModFolder)))
            .Button(T("add-vulkan-template"), T("do-create"), () => Example(KometExamples.Vulkan),
                T("add-vulkan-template-hint", ShaderPacks.Shown(KometExamples.ModFolder)))
            .Group(T("templates-folders"))
            .Button(T("templates-folder"), T("do-open"), () => OpenFolder(KometExamples.ModFolder),
                T("templates-folder-hint", ShaderPacks.Shown(KometExamples.ModFolder)))
            .Button(T("mods-folder"), T("do-open"), () => OpenFolder(GamePaths.DataPathMods),
                T("mods-folder-hint", ShaderPacks.Shown(GamePaths.DataPathMods)));
        return page;
    }

    // A whole mod written out and its folder opened, to copy into the Mods folder
    private void Example(string kind)
    {
        if (Api is not { } api) return;
        if (KometExamples.Create(api, kind) is { } folder) OpenFolder(folder);
    }

    // Raised after the Style page's reload: the screen takes the styles anew
    public event Action? StylesReloaded;

    private static void OpenFolder(string folder)
    {
        try
        {
            _ = Directory.CreateDirectory(folder);
            NetUtil.OpenUrlInBrowser(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ApiEvents.Logger?.Warning("Komet styles: {0} could not be opened: {1}", folder, e.Message);
        }
    }

    private OptionPage ToolsPage(string section)
    {
        var (s, page) = (settings, new OptionPage(Tools, T("page-tools"), section));
        _ = page.Group(T("options"))
            .Switch(T("showadvanced"), () => s.ShowAdvanced, on => s.ShowAdvanced = on, T("showadvanced-hint"))
            .Group(T("version"))
            .Switch(T("updates"), () => s.UpdateCheck, on => (s.UpdateAsked, s.UpdateCheck) = (true, on), T("updates-hint"))
            .Button(T("checksum"), T("do-verify"), actions.Verify, T("checksum-hint"))
            .Group(T("actions"))
            .Button(T("rebuild"), T("do-rebuild"), TerrainRebuild.Request, T("rebuild-hint"))
            .Slider(T("bench"), HudSettings.BenchRange, () => s.BenchSeconds, v => s.BenchSeconds = v)
            .Button(T("benchmark"), T("do-bench", s.BenchSeconds), () => actions.Bench((float)s.BenchSeconds))
            .Button(T("values"), T("do-copy"), actions.Dump)
            .Group(T("reset"));
        return Reset(Reset(page, "positions", s.ResetPositions), "defaults", s.ResetDefaults);
    }

    // A reset acts on its second click; the first only turns its caption into the question
    private OptionPage Reset(OptionPage page, string label, Action reset)
    {
        return page.Button(T(label), () => _armed == label ? Lang.Get("Confirm") : T("do-reset"), () =>
        {
            if (_armed == label) reset();
            _armed = _armed == label ? null : label;
        }, T("reset-hint"));
    }

    private static string Known(int value) => value > 0 && Assert(value < 1_000_000) ? value.ToString(CultureInfo.InvariantCulture) : "?";

    // A knob's row: dimmed while a knob it needs is off, locked while its feature is held; a slider marks the value it
    // follows now (preset and computer)
    private OptionPage Row(OptionPage page, Knob knob, int index)
    {
        var (unit, hint, s) = (knob.Unit.Length > 0 ? " " + knob.Unit : "", T(knob.Label + "-hint"), settings.Knobs);
        _ = (knob.IsSwitch
            ? page.Switch(T(knob.Label), () => s.Knob(index) != 0, on => s.SetKnob(index, on ? 1 : 0), hint)
                .Standard(() => KometSettings.KnobDefault(index) != 0 ? 1 : 0)
            : page.Slider(T(knob.Label), knob.Min, knob.Max, 1, () => s.Knob(index),
                v => s.SetKnob(index, (int)v), unit, hint).Standard(() => KometSettings.KnobDefault(index)))
            .LockedWhen(() => Features.LockText(index));
        var needed = Needed(knob);
        return needed.Length == 0 ? page : page.EnabledWhen(() => Array.TrueForAll(needed, k => s.Knob(k) != 0));
    }

    // The knobs a knob needs on, the nearest first: its Needs, that one's Needs and so on
    private static int[] Needed(Knob knob)
    {
        List<int> chain = [];
        var key = knob.Needs;
        for (var i = 0; i < MaxNeeds && key is not null; i++)
        {
            var at = Knobs.Find(key);
            if (!Assert(at >= 0) || chain.Contains(at)) break;
            chain.Add(at);
            key = Knobs.At(at).Needs;
        }

        _ = Assert(chain.Count < MaxNeeds);
        return [.. chain];
    }

    private OptionPage ModeRow(OptionPage page) =>
        page.Choice(T("vulkan-mode"), Array.ConvertAll(VulkanMode.Choices, choice => T("vulkan-mode-" + choice.Name)),
                () => VulkanMode.Choice(VulkanMode.Of(settings.Knobs.Knob)),
                choice => { if (Index(choice, VulkanMode.Choices.Length)) VulkanMode.Set(VulkanMode.Choices[choice].Mode, settings.Knobs.SetKnob); },
                T("vulkan-mode-hint"))
            .LockedWhen(() => VulkanMode.CoreKnob >= 0 ? Features.LockText(VulkanMode.CoreKnob) : null);
}
