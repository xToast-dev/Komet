namespace Komet.Runtime.UI.Overlay;

internal sealed class HudSettingsDialog : HudDialog
{
    private const double SegmentGap = 4, KnobSize = 12, MinSliderWidth = 160;
    private const int Custom = 4, MaxSegments = 8;   // fifth corner segment, display only: panels were dragged
    private static readonly Rgba Knob = Rgba.White(0.9), Passive = new(0.85, 0.55, 0.15, 1);
    private static readonly string[] Corners = ["topleft", "topright", "bottomleft", "bottomright", "custom"];
    private static readonly string[] Labels = ["visible", "corner", "opacity", "scale", "updates", "graph", "system", "passes", "modtimes", "log", "debuglog", "detail", "interval", "bench", "positions", "values", "benchmark", "defaults", "shadercache", "mods", "checksum"];
    private static readonly HudRange[] Ranges = [HudSettings.OpacityRange, HudSettings.ScaleRange, HudSettings.IntervalRange, HudSettings.BenchRange];

    private readonly Action _dump, _verify;
    private readonly Action<float> _bench;
    private int _page;

    public HudSettingsDialog(ICoreClientAPI capi, HudSettings settings, Action dump, Action<float> bench, Action verify) : base(capi, settings, "settings")
    {
        (_dump, _verify, _bench) = (dump, verify, bench);
        if (!NotNull(capi.ChatCommands) || !Assert(Corners.Length == Custom + 1)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").WithDescription(HudSettings.Translate("cmd-hud"))
            .HandleWith(_ => TryOpen() ? TextCommandResult.Success() : TextCommandResult.Error(HudSettings.Translate("cmd-hud-open-failed")));
    }

    protected override double Build()
    {
        var s = Settings;
        double segmentGap = scaled(SegmentGap), knob = scaled(KnobSize);
        string[] onOff = [Translate("on"), Translate("off")], cornerNames = Names("corner", Corners), pages = Names("page", ["hud", "ab"]);
        string[] buttons = [Translate("do-reset"), Translate("do-copy"), Translate("do-bench", s.BenchSeconds), Translate("do-verify")];
        double labelW = LabelColumn(Labels), valueW = 0, buttonW = 0;
        foreach (var range in Ranges.Bounded(MaxSegments)) valueW = Math.Max(valueW, HudCanvas.TextWidth(s.Text, range.Text(range.Max)));
        foreach (var text in buttons.Bounded(MaxSegments)) buttonW = Math.Max(buttonW, HudCanvas.BadgeWidth(s.Header, text));
        var controlW = Math.Max(Math.Max(GroupWidth(onOff), GroupWidth(cornerNames)), Math.Max(scaled(MinSliderWidth) + Gap + valueW, buttonW));
        double controlX = Pad + labelW + Gap, width = controlX + controlW + Pad;
        if (!Assert(labelW > 0) || !Assert(valueW > 0) || !Assert(controlW > 0) || !Index(_page, 2)) return 0;
        Title();
        Rows.Add((RowH, y => Segments(y, pages, _page, i => { _page = i; Dirty = true; }, left: Pad, span: width - (2 * Pad))));
        if (_page == 0)
        {
            Header("display");
            Row("visible", y => Switch(y, s.Visible, on => s.Visible = on));
            Row("corner", y => Segments(y, cornerNames, s.Pinned.Count > 0 ? Custom : (int)s.Corner, i => s.SetCorner((HudCorner)i), passive: Custom));
            Row("opacity", y => Slider(y, s.Opacity, HudSettings.OpacityRange, v => s.Opacity = v));
            Row("scale", y => Slider(y, s.FontScale, HudSettings.ScaleRange, v => s.FontScale = v));
            Row("updates", y => Switch(y, s.UpdateCheck, on => { s.UpdateAsked = true; s.UpdateCheck = on; }));
            Header("panels");
            Row("graph", y => Switch(y, s.ShowGraph, on => s.ShowGraph = on));
            Row("system", y => Switch(y, s.ShowSystem, on => s.ShowSystem = on));
            Row("passes", y => Switch(y, s.ShowPasses, on => s.ShowPasses = on));
            Row("mods", y => Switch(y, s.ShowMods, on => s.ShowMods = on));
            Row("modtimes", y => Switch(y, s.ShowModTimes, on => s.ShowModTimes = on));
            Row("log", y => Switch(y, s.ShowLog, on => s.ShowLog = on));
            Row("debuglog", y => Switch(y, s.ShowDebugLog, on => s.ShowDebugLog = on));
            Row("detail", y => Switch(y, s.Detail, on => s.Detail = on, enabled: s.ShowPasses || s.ShowModTimes));
            Header("measure");
            Row("interval", y => Slider(y, s.Interval, HudSettings.IntervalRange, v => s.Interval = v));
            Row("bench", y => Slider(y, s.BenchSeconds, HudSettings.BenchRange, v => s.BenchSeconds = v));
            Header("actions");
            Row("positions", y => Button(y, buttons[0], s.ResetPositions));
            Row("values", y => Button(y, buttons[1], _dump));
            Row("benchmark", y => Button(y, buttons[2], () => _bench((float)s.BenchSeconds)));
            Row("checksum", y => Button(y, buttons[3], _verify));
            Row("defaults", y => Button(y, buttons[0], s.ResetDefaults));
        }
        else
        {
            Header("patches");
            Row("shadercache", y => Switch(y, s.ShaderUseCache, on => s.ShaderUseCache = on));
            Header("actions");
            Row("values", y => Button(y, buttons[1], _dump));
            Row("benchmark", y => Button(y, buttons[2], () => _bench((float)s.BenchSeconds)));
        }
        return width;

        double GroupWidth(string[] names)
        {
            if (!Index(names.Length - 1, MaxSegments)) return 0;
            double widest = 0;
            foreach (var name in names.Bounded(MaxSegments)) widest = Math.Max(widest, HudCanvas.BadgeWidth(s.Header, name));
            return (names.Length * widest) + ((names.Length - 1) * segmentGap);
        }

        void Switch(double y, bool on, Action<bool> set, bool enabled = true) => Segments(y, onOff, on ? 0 : 1, i => set(i == 0), enabled: enabled);

        void Segments(double y, string[] names, int selected, Action<int> select, int passive = -1, bool enabled = true, double? left = null, double? span = null)
        {
            if (!Index(names.Length - 1, MaxSegments) || !Index(selected, names.Length)) return;
            var w = ((span ?? controlW) - ((names.Length - 1) * segmentGap)) / names.Length;
            if (!Assert(w > 0)) return;
            for (var i = 0; i < Math.Min(names.Length, MaxSegments); i++)
            {
                var index = i;
                var segX = (left ?? controlX) + (i * (w + segmentGap));
                var color = (i == selected, i == passive) switch { (true, true) => Passive, (true, false) => HudCanvas.Accent, (false, true) => HudCanvas.Neutral with { A = 0.4 }, _ => HudCanvas.Neutral };
                if (!enabled) color = color with { A = 0.4 };
                else if (i != passive) Hits.Add(new HitBox(segX, y, w, RowH, _ => select(index)));
                _ = Canvas.Badge(segX, y, RowH, s.Header, names[i], color, w);
            }
        }

        void Slider(double y, double value, HudRange range, Action<double> set)
        {
            if (!Assert(range.Max > range.Min) || !Assert(range.Step > 0) || !Assert(range.Contains(value))) return;
            double w = controlW - Gap - valueW, fraction = Math.Clamp((value - range.Min) / (range.Max - range.Min), 0, 1);
            if (!Assert(w > 0)) return;
            var text = range.Text(value);
            Canvas.Bar(controlX, y, RowH, w, fraction, 0, HudCanvas.Accent);
            Canvas.Fill(controlX + (w * fraction) - (knob / 2), y + ((RowH - knob) / 2), knob, knob, Knob, knob / 2);
            Canvas.Text(width - Pad - HudCanvas.TextWidth(s.Text, text), y, RowH, s.Text, text);
            Hits.Add(new HitBox(controlX, y, w, RowH, f => set(Math.Round(range.Min + (Math.Round(f * (range.Max - range.Min) / range.Step) * range.Step), 2)), Drag: true));
        }

        void Button(double y, string text, Action click)
        {
            if (!Assert(text.Length > 0)) return;
            Hits.Add(new HitBox(controlX, y, controlW, RowH, _ => click()));
            _ = Canvas.Badge(controlX, y, RowH, s.Header, text, HudCanvas.Neutral, controlW);
        }
    }

    private string[] Names(string key, string[] values)
        => Assert(key.Length > 0) && Assert(values.Length > 0) ? Array.ConvertAll(values, value => Translate($"{key}-{value}")) : [];
}
