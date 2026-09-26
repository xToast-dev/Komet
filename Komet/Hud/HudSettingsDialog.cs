namespace Komet.Hud;

internal sealed class HudSettingsDialog : HudDialog
{
    private const double SegmentGap = 4, KnobSize = 12, MinSliderWidth = 160;
    private const int Custom = 4, MaxSegments = 8; // fifth corner segment, display only: panels were dragged
    private static readonly Rgba Handle = Rgba.White(0.9), Passive = new(0.85, 0.55, 0.15, 1);
    private static readonly string[] Corners = ["topleft", "topright", "bottomleft", "bottomright", "custom"];

    // One page of HUD settings, then the knobs by the page Knobs gives them: in one page they outgrew HudDialog's row bound
    private static readonly string[] Pages = PageNames();

    private static readonly HudRange[] Ranges =
        [HudSettings.OpacityRange, HudSettings.ScaleRange, HudSettings.IntervalRange, HudSettings.BenchRange];

    private readonly Action<float> _bench;

    private readonly Action _dump, _verify;
    private string[] _onOff = [];
    private int _page;
    private double _valueW; // the slider's value text, right-aligned in the control column

    public HudSettingsDialog(ICoreClientAPI capi, HudSettings settings, HudFonts fonts, SnapGrid snap, Action dump,
        Action<float> bench,
        Action verify) : base(capi, settings, fonts, snap, "settings")
    {
        (_dump, _verify, _bench) = (dump, verify, bench);
        if (!NotNull(capi.ChatCommands) || !Assert(Corners.Length == Custom + 1)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").WithDescription(HudText.Translate("cmd-hud"))
            .HandleWith(_ =>
                TryOpen()
                    ? TextCommandResult.Success()
                    : TextCommandResult.Error(HudText.Translate("cmd-hud-open-failed")));
    }

    protected override double Build()
    {
        var s = Settings;
        _onOff = [Translate("on"), Translate("off")];
        string[] cornerNames = Names("corner", Corners), pages = Names("page", Pages);
        string[] buttons =
        [
            Translate("do-reset"), Translate("do-copy"), Translate("do-bench", s.BenchSeconds), Translate("do-verify")
        ];
        _valueW = ValueWidth();
        var controlW = Math.Max(Math.Max(GroupWidth(_onOff), GroupWidth(cornerNames)),
            Math.Max(scaled(MinSliderWidth) + Gap + _valueW,
                buttons.Max(text => Canvas.BadgeWidth(Fonts, text))));
        if (!Assert(_valueW > 0) || !Assert(controlW > 0) || !Index(_page, Pages.Length)) return 0;
        Title();
        Rows.Add((RowH, y => Segments(y, pages, _page, i =>
        {
            _page = i;
            Dirty = true;
        }, left: Pad, span: Width - 2 * Pad)));
        if (_page == 0)
        {
            HudPage(buttons, cornerNames);
            return controlW;
        }

        FeaturePage(Pages[_page]);
        // Every feature page ends with what an A/B test needs next: the values the panels showed, or a benchmark
        Header("actions");
        Row("values", y => Button(y, buttons[1], _dump));
        Row("benchmark", y => Button(y, buttons[2], () => _bench((float)s.BenchSeconds)));
        return controlW;
    }

    private void HudPage(string[] buttons, string[] cornerNames)
    {
        var s = Settings;
        if (!Assert(buttons.Length == 4) || !Assert(cornerNames.Length == Corners.Length)) return;
        Header("display");
        Row("visible", y => Switch(y, s.Visible, on => s.Visible = on));
        Row("corner",
            y => Segments(y, cornerNames, s.Pinned.Count > 0 ? Custom : (int)s.Corner,
                i => s.SetCorner((HudCorner)i), Custom));
        Row("opacity", y => Slider(y, s.Opacity, HudSettings.OpacityRange, v => s.Opacity = v));
        Row("scale", y => Slider(y, s.FontScale, HudSettings.ScaleRange, v => s.FontScale = v));
        Header("panels");
        Row("graph", y => Switch(y, s.ShowGraph, on => s.ShowGraph = on));
        Row("system", y => Switch(y, s.ShowSystem, on => s.ShowSystem = on));
        Row("passes", y => Switch(y, s.ShowPasses, on => s.ShowPasses = on));
        Row("modtimes", y => Switch(y, s.ShowModTimes, on => s.ShowModTimes = on));
        Row("mods", y => Switch(y, s.ShowMods, on => s.ShowMods = on));
        Row("counters", y => Switch(y, s.ShowCounters, on => s.ShowCounters = on));
        Row("log", y => Switch(y, s.ShowLog, on => s.ShowLog = on));
        Row("debuglog", y => Switch(y, s.ShowDebugLog, on => s.ShowDebugLog = on));
        Row("detail", y => Switch(y, s.Detail, on => s.Detail = on));
        Header("measure");
        Row("interval", y => Slider(y, s.Interval, HudSettings.IntervalRange, v => s.Interval = v));
        Row("bench", y => Slider(y, s.BenchSeconds, HudSettings.BenchRange, v => s.BenchSeconds = v));
        Row("benchmark", y => Button(y, buttons[2], () => _bench((float)s.BenchSeconds)));
        Row("values", y => Button(y, buttons[1], _dump));
        Header("version");
        Row("updates", y => Switch(y, s.UpdateCheck, on =>
        {
            s.UpdateAsked = true;
            s.UpdateCheck = on;
        }));
        Row("checksum", y => Button(y, buttons[3], _verify));
        Header("reset");
        Row("positions", y => Button(y, buttons[0], s.ResetPositions));
        Row("defaults", y => Button(y, buttons[0], s.ResetDefaults));
    }

    // The page's knobs in table order, a header wherever the group changes
    private void FeaturePage(string page)
    {
        var s = Settings;
        var knobs = Knobs.All;
        string? group = null;
        if (!NotNull(s) || !Assert(Rows.Count > 0)) return;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
        {
            var (knob, index) = (knobs[i], i);
            if (knob.Page != page || !NotNull(knob.Group)) continue;
            if (knob.Group != group)
            {
                group = knob.Group;
                Header(group);
            }

            if (knob.IsSwitch) Row(knob.Label, y => Switch(y, s.Knob(index) != 0, on => s.SetKnob(index, on ? 1 : 0)));
            else Row(knob.Label, y => Slider(y, s.Knob(index), Range(knob), v => s.SetKnob(index, (int)v)));
        }
    }

    private static HudRange Range(Knob knob)
    {
        return Assert(knob.Max > knob.Min) && Assert(knob.Unit.Length < 8)
            ? new HudRange(knob.Min, knob.Max, 1, 1, knob.Unit)
            : default;
    }

    private static string[] PageNames()
    {
        List<string> pages = ["hud"];
        var knobs = Knobs.All;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i].Page is { } page && !pages.Contains(page))
                pages.Add(page);
        return Assert(pages.Count > 1) && Assert(pages.Count <= MaxSegments) ? [.. pages] : ["hud"];
    }

    // The widest value text any slider can show, the display's and the knobs'
    private double ValueWidth()
    {
        var width = Ranges.Max(range => Canvas.TextWidth(Fonts.Text, range.Text(range.Max)));
        var knobs = Knobs.All;
        for (var i = 0; i < Math.Min(knobs.Length, Knobs.MaxKnobs); i++)
            if (knobs[i] is { IsSwitch: false, Page: not null })
                width = Math.Max(width, Canvas.TextWidth(Fonts.Text, Range(knobs[i]).Text(knobs[i].Max)));
        return Assert(width > 0) ? width : 0;
    }

    private string[] Names(string key, string[] values)
    {
        return Assert(key.Length > 0) && Assert(values.Length > 0)
            ? Array.ConvertAll(values, value => Translate($"{key}-{value}"))
            : [];
    }

    private double GroupWidth(string[] names)
    {
        if (!Index(names.Length - 1, MaxSegments)) return 0;
        var widest = names.Max(name => Canvas.BadgeWidth(Fonts, name));
        return Assert(widest > 0) ? names.Length * widest + (names.Length - 1) * scaled(SegmentGap) : 0;
    }

    private void Switch(double y, bool on, Action<bool> set)
    {
        if (Assert(_onOff.Length == 2) && NotNull(set)) Segments(y, _onOff, on ? 0 : 1, i => set(i == 0));
    }

    private void Segments(double y, string[] names, int selected, Action<int> select, int passive = -1,
        double? left = null,
        double? span = null)
    {
        double gap = scaled(SegmentGap), x = left ?? ControlX;
        if (!Index(names.Length - 1, MaxSegments) || !Index(selected, names.Length)) return;
        var w = ((span ?? ControlW) - (names.Length - 1) * gap) / names.Length;
        if (!Assert(w > 0)) return;
        for (var i = 0; i < Math.Min(names.Length, MaxSegments); i++)
        {
            var index = i;
            var segX = x + i * (w + gap);
            var color = (i == selected, i == passive) switch
            {
                (true, true) => Passive,
                (true, false) => HudCanvas.Accent,
                (false, true) => HudCanvas.Neutral with { A = 0.4 },
                _ => HudCanvas.Neutral
            };
            if (i != passive) Hits.Add(new HitBox(segX, y, w, RowH, _ => select(index)));
            _ = Canvas.Badge(segX, y, RowH, Fonts, names[i], color, w);
        }
    }

    private void Slider(double y, double value, HudRange range, Action<double> set)
    {
        if (!Assert(range.Max > range.Min) || !Assert(range.Step > 0) || !Assert(range.Contains(value))) return;
        double w = ControlW - Gap - _valueW,
            knob = scaled(KnobSize),
            fraction = Math.Clamp((value - range.Min) / (range.Max - range.Min), 0, 1);
        if (!Assert(w > 0)) return;
        var text = range.Text(value);
        Canvas.Bar(ControlX, y, RowH, w, fraction, 0, HudCanvas.Accent);
        Canvas.Fill(ControlX + w * fraction - knob / 2, y + (RowH - knob) / 2, knob, knob, Handle, knob / 2);
        Canvas.Text(Width - Pad - Canvas.TextWidth(Fonts.Text, text), y, RowH, Fonts.Text, text);
        Hits.Add(new HitBox(ControlX, y, w, RowH,
            f => set(Math.Round(range.Min + Math.Round(f * (range.Max - range.Min) / range.Step) * range.Step, 2)),
            true));
    }

    private void Button(double y, string text, Action click)
    {
        if (!Assert(text.Length > 0) || !Assert(ControlW > 0)) return;
        Hits.Add(new HitBox(ControlX, y, ControlW, RowH, _ => click()));
        _ = Canvas.Badge(ControlX, y, RowH, Fonts, text, HudCanvas.Neutral, ControlW);
    }
}
