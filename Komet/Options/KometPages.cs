namespace Komet.Options;

// Komet's own pages, through the same OptionPage API a mod uses: the HUD's look, its tools (measuring, updates, reset), then one page
// per knob page of Knobs, in table order, each ending with what an A/B test needs next (the values the panels showed, a benchmark).
// Built per composition, so a language change shows at once.
internal sealed class KometPages(HudSettings settings, Action dump, Action<float> bench, Action verify)
{
    public const string Hud = "komet-hud", Tools = "komet-tools";
    private const int MaxKnobPages = 8;

    private static readonly string[] Corners = ["topleft", "topright", "bottomleft", "bottomright", "custom"];

    public OptionPage[] Build()
    {
        var section = T("section-komet");
        if (!NotNull(settings)) return [];
        List<OptionPage> pages = [HudPage(section), Measure(Version(new OptionPage(Tools, T("page-tools"), section)))];
        var knobs = Knobs.BuiltIn;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page is { } page && pages.TrueForAll(p => p.Id != "komet-" + page) &&
                Assert(pages.Count <= MaxKnobPages)) pages.Add(FeaturePage(page, section));
        _ = Assert(pages.Count > 1);
        return [.. pages];
    }

    private OptionPage HudPage(string section)
    {
        var s = settings;
        if (!NotNull(s) || !Assert(Corners.Length == (int)HudCorner.BottomRight + 2)) return new OptionPage(Hud, "HUD", section);
        return new OptionPage(Hud, T("page-hud"), section).Group(T("display"))
            .Switch(T("visible"), () => s.Visible, on => s.Visible = on)
            .Choice(T("corner"), Array.ConvertAll(Corners, c => T("corner-" + c)),
                () => s.Pinned.Count > 0 ? Corners.Length - 1 : (int)s.Corner,
                i =>
                {
                    if (i < Corners.Length - 1) s.SetCorner((HudCorner)i);
                })
            .Slider(T("opacity"), HudSettings.OpacityRange, () => s.Opacity, v => s.Opacity = v)
            .Slider(T("scale"), HudSettings.ScaleRange, () => s.FontScale, v => s.FontScale = v)
            .Group(T("panels"))
            .Switch(T("graph"), () => s.ShowGraph, on => s.ShowGraph = on)
            .Switch(T("system"), () => s.ShowSystem, on => s.ShowSystem = on)
            .Switch(T("passes"), () => s.ShowPasses, on => s.ShowPasses = on)
            .Switch(T("modtimes"), () => s.ShowModTimes, on => s.ShowModTimes = on)
            .Switch(T("mods"), () => s.ShowMods, on => s.ShowMods = on)
            .Switch(T("counters"), () => s.ShowCounters, on => s.ShowCounters = on)
            .Switch(T("log"), () => s.ShowLog, on => s.ShowLog = on)
            .Switch(T("debuglog"), () => s.ShowDebugLog, on => s.ShowDebugLog = on)
            .Switch(T("detail"), () => s.Detail, on => s.Detail = on);
    }

    private OptionPage Measure(OptionPage page)
    {
        var s = settings;
        if (!NotNull(page) || !NotNull(s)) return page;
        return page.Group(T("measure"))
            .Slider(T("interval"), HudSettings.IntervalRange, () => s.Interval, v => s.Interval = v)
            .Slider(T("bench"), HudSettings.BenchRange, () => s.BenchSeconds, v => s.BenchSeconds = v)
            .Button(T("benchmark"), T("do-bench", s.BenchSeconds), () => bench((float)s.BenchSeconds))
            .Button(T("values"), T("do-copy"), dump)
            .Group(T("reset"))
            .Button(T("positions"), T("do-reset"), s.ResetPositions)
            .Button(T("defaults"), T("do-reset"), s.ResetDefaults);
    }

    private OptionPage Version(OptionPage page)
    {
        var s = settings;
        if (!NotNull(page) || !NotNull(s)) return page;
        return page.Group(T("version"))
            .Switch(T("updates"), () => s.UpdateCheck, on =>
            {
                s.UpdateAsked = true;
                s.UpdateCheck = on;
            })
            .Button(T("checksum"), T("do-verify"), verify);
    }

    // The page's knobs in table order, a header wherever the group changes, then other mods' placed on it; a held knob's row is locked
    private OptionPage FeaturePage(string name, string section)
    {
        var page = new OptionPage("komet-" + name, T("page-" + name), section) { Capacity = OptionPage.MaxRows };
        var knobs = Knobs.BuiltIn;
        string? group = null;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
        {
            var (knob, index) = (knobs[i], i);
            if (knob.Page != name || !NotNull(knob.Group)) continue;
            if (knob.Group != group)
            {
                group = knob.Group;
                _ = page.Group(T(group));
            }

            var (unit, hint) = (knob.Unit.Length > 0 ? " " + knob.Unit : "", T(knob.Label + "-hint"));
            _ = (knob.IsSwitch
                ? page.Switch(T(knob.Label), () => settings.Knob(index) != 0, on => settings.SetKnob(index, on ? 1 : 0), hint)
                : page.Slider(T(knob.Label), knob.Min, knob.Max, 1, () => settings.Knob(index),
                    v => settings.SetKnob(index, (int)v), unit, hint)).LockedWhen(() => Features.LockText(index));
        }

        return Features.AddRows(page, true).Group(T("actions"))
            .Button(T("values"), T("do-copy"), dump)
            .Button(T("benchmark"), T("do-bench", settings.BenchSeconds), () => bench((float)settings.BenchSeconds));
    }

    // Komet's own text on the options screen, its own pages and the game's
    internal static string T(string key, params object[] args)
    {
        return NotNull(key) && Assert(key.Length > 0) ? HudText.Translate("settings-" + key, args) : "";
    }
}

