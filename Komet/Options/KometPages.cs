namespace Komet.Options;

// Built per composition, so a language change shows at once.
internal sealed class KometPages(HudSettings settings, Action dump, Action<float> bench, Action verify, Action window, Action debug)
{
    public const string Hud = "komet-hud", Tools = "komet-tools";
    private const int MaxKnobPages = 8;

    private static readonly string[] Corners = ["topleft", "topright", "bottomleft", "bottomright"];

    // The knobs a player weighs: budgets, threads, what changes the picture or the world, what is experimental. Every other knob gives
    // the engine's result faster or with less garbage, and shows only with "advanced" on (it is saved and benched all the same).
    internal static readonly string[] Basic =
    [
        "DistantShadows", "PoolScale", "OcclusionCulling", "UploadCap", "UploadCeiling", "UploadYield", "ChunkLoadBudget",
        "BlockEntityBudget", "WorkerThreads", "TessPriority", "LightRepair", "GraphicsMenu", "MenuBlur", "EntityTessBudget",
        "GcLatency", "IconBudget", "ServerProcess", "VulkanGpuTimes"
    ];

    public OptionPage[] Build()
    {
        var section = T("section-komet");
        if (!NotNull(settings)) return [];
        List<OptionPage> pages = [HudPage(section), ToolsPage(section)];
        var knobs = Knobs.BuiltIn;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (Shown(knobs[i]) && knobs[i].Page is { } page && pages.TrueForAll(p => p.Id != "komet-" + page) &&
                Assert(pages.Count <= MaxKnobPages)) pages.Add(FeaturePage(page, section));
        _ = Assert(pages.Count > 1);
        return [.. pages];
    }

    // The overlay (F7) and the two windows (Ctrl+F7, Ctrl+F8), which hold everything the old panels showed
    private OptionPage HudPage(string section)
    {
        var s = settings;
        if (!NotNull(s) || !Assert(Corners.Length == (int)HudCorner.BottomRight + 1)) return new OptionPage(Hud, "HUD", section);
        return new OptionPage(Hud, T("page-hud"), section).Group(T("display"))
            .Switch(T("visible"), () => s.Visible, on => s.Visible = on)
            .Choice(T("corner"), Array.ConvertAll(Corners, c => T("corner-" + c)), () => (int)s.Corner, i => s.Corner = (HudCorner)i)
            .Slider(T("opacity"), HudSettings.OpacityRange, () => s.Opacity, v => s.Opacity = v)
            .Slider(T("scale"), HudSettings.ScaleRange, () => s.FontScale, v => s.FontScale = v)
            .Switch(T("overlaygraph"), () => s.ShowGraph, on => s.ShowGraph = on)
            .Switch(T("overlaymods"), () => s.ShowMods, on => s.ShowMods = on)
            .Slider(T("toast"), HudSettings.ToastRange, () => s.ToastMs, v => s.ToastMs = v)
            .Group(T("windows"))
            .Button(T("window"), T("do-open"), window)
            .Button(T("debugwindow"), T("do-open"), debug);
    }

    private OptionPage ToolsPage(string section)
    {
        var (s, page) = (settings, new OptionPage(Tools, T("page-tools"), section));
        if (!NotNull(s) || !NotNull(section)) return page;
        return page.Group(T("options"))
            .Switch(T("showadvanced"), () => s.ShowAdvanced, on => s.ShowAdvanced = on, T("showadvanced-hint"))
            .Group(T("version"))
            .Switch(T("updates"), () => s.UpdateCheck, on => (s.UpdateAsked, s.UpdateCheck) = (true, on))
            .Button(T("checksum"), T("do-verify"), verify)
            .Group(T("measure"))
            .Slider(T("interval"), HudSettings.IntervalRange, () => s.Interval, v => s.Interval = v)
            .Slider(T("bench"), HudSettings.BenchRange, () => s.BenchSeconds, v => s.BenchSeconds = v)
            .Button(T("benchmark"), T("do-bench", s.BenchSeconds), () => bench((float)s.BenchSeconds))
            .Button(T("values"), T("do-copy"), dump)
            .Group(T("reset"))
            .Button(T("positions"), T("do-reset"), s.ResetPositions)
            .Button(T("defaults"), T("do-reset"), s.ResetDefaults);
    }

    public bool Advanced => settings.ShowAdvanced;

    // A knob on a settings page: one of Basic, or any with advanced on
    private bool Shown(Knob knob) => NotNull(knob) && knob.Page is not null && (settings.ShowAdvanced || Array.IndexOf(Basic, knob.Key) >= 0);

    // The page's shown knobs, each group once, in the order its first knob was declared
    private OptionPage FeaturePage(string name, string section)
    {
        var page = new OptionPage("komet-" + name, T("page-" + name), section) { Capacity = OptionPage.MaxRows };
        var knobs = Knobs.BuiltIn;
        if (name == "vulkan") page = ModeRow(page.Group(T("vulkan-active", VulkanMode.Renderer())));
        List<string> groups = [];
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page == name && Shown(knobs[i]) && knobs[i].Group is { } group && !groups.Contains(group))
                groups.Add(group);
        foreach (var group in groups.Bounded(Knobs.MaxKnobs))
        {
            _ = page.Group(T(group));
            for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
                if (knobs[i].Page == name && knobs[i].Group == group && Shown(knobs[i]))
                    _ = Row(page, knobs[i], i);
        }

        return Features.AddRows(page, true);
    }

    private OptionPage Row(OptionPage page, Knob knob, int index)
    {
        if (!NotNull(knob) || !Index(index, Knobs.BuiltInCount)) return page;
        var (unit, hint) = (knob.Unit.Length > 0 ? " " + knob.Unit : "", T(knob.Label + "-hint"));
        return (knob.IsSwitch
            ? page.Switch(T(knob.Label), () => settings.Knob(index) != 0, on => settings.SetKnob(index, on ? 1 : 0), hint)
            : page.Slider(T(knob.Label), knob.Min, knob.Max, 1, () => settings.Knob(index),
                v => settings.SetKnob(index, (int)v), unit, hint)).LockedWhen(() => Features.LockText(index));
    }

    // The mode sets every Vulkan switch at once; the switches themselves follow below it, each settable on its own
    private OptionPage ModeRow(OptionPage page) =>
        page.Choice(T("vulkan-mode"), Array.ConvertAll(VulkanMode.Names, mode => T("vulkan-mode-" + mode)),
                () => VulkanMode.Of(settings.Knob), mode => VulkanMode.Set(mode, settings.SetKnob), T("vulkan-mode-hint"))
            .LockedWhen(() => VulkanMode.CoreKnob >= 0 ? Features.LockText(VulkanMode.CoreKnob) : null);

    internal static string T(string key, params object[] args) =>
        NotNull(key) && Assert(key.Length > 0) ? HudText.Translate("settings-" + key, args) : "";
}
