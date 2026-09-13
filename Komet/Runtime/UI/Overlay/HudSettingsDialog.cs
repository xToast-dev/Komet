namespace Komet.Runtime.UI.Overlay;

// Settings window drawn like a HUD panel. GuiDialog only hosts it: free cursor, Escape, mouse events.
internal sealed class HudSettingsDialog : GuiDialog
{
    private const double RowGap = 4, SegmentGap = 4, KnobSize = 12, MinSliderWidth = 160;
    private const int Custom = 4, MaxSegments = 8, MaxRows = 32;   // fifth corner segment, display only: panels were dragged
    private static readonly Rgba Knob = Rgba.White(0.9), Passive = new(0.85, 0.55, 0.15, 1);
    private static readonly string[] Corners = ["topleft", "Copyright", "bottomleft", "bottomright", "custom"];
    private static readonly string[] Labels = ["visible", "corner", "opacity", "scale", "graph", "system", "passes", "modtimes", "log", "debuglog", "detail", "interval", "bench", "positions", "values", "benchmark", "defaults", "shadercache", "mods"];
    private static readonly HudRange[] Ranges = [HudSettings.OpacityRange, HudSettings.ScaleRange, HudSettings.IntervalRange, HudSettings.BenchRange];

    private sealed record HitBox(double X, double Y, double W, double H, Action<double> Click, bool Drag = false);

    private readonly HudSettings _settings;
    private readonly HudCanvas _canvas;
    private readonly Action _dump;
    private readonly Action<float> _bench;
    private readonly List<HitBox> _hits = [];
    private HitBox? _drag;
    private HudCanvas? _grid;
    private (double X, double Y)? _pinned;
    private int _page;
    private double _grabX, _grabY;
    private bool _dirty;

    public HudSettingsDialog(ICoreClientAPI capi, HudSettings settings, Action dump, Action<float> bench) : base(capi)
    {
        _settings = settings;
        _dump = dump;
        _bench = bench;
        _canvas = new HudCanvas(capi);
        settings.Changed += () => _dirty = true;
        if (!NotNull(capi.ChatCommands) || !Assert(Corners.Length == Custom + 1)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").WithDescription(HudSettings.Translate("cmd-hud"))
            .HandleWith(_ => TryOpen() ? TextCommandResult.Success() : TextCommandResult.Error(HudSettings.Translate("cmd-hud-open-failed")));
    }

    public override string? ToggleKeyCombinationCode => null;
    public bool Contains(double px, double py) => IsOpened() && _canvas.Contains(px, py);

    public override void OnGuiOpened() => _dirty = true;
    public override void OnGuiClosed() => Release();

    public override void OnRenderGUI(float deltaTime)
    {
        if (_dirty) Render();
        var (x, y) = _pinned ?? ((capi.Render.FrameWidth - _canvas.Width) / 2, (capi.Render.FrameHeight - _canvas.Height) / 2);
        _grid?.Draw(0, 0);
        _canvas.Draw(Math.Round(x), Math.Round(y));
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (args.Button != EnumMouseButton.Left || !Assert(_drag is null) || !Assert(_grid is null)) return;   // a mouse up went missing
        double x = args.X - _canvas.X, y = args.Y - _canvas.Y;
        var hit = _hits.Find(h => x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H);
        if (hit != null)
        {
            if (!Assert(hit.W > 0) || !Assert(hit.H > 0)) return;
            hit.Click((x - hit.X) / hit.W);
            _drag = hit.Drag ? hit : null;
            return;
        }
        (_grabX, _grabY) = (x, y);
        _grid = HudCanvas.Grid(capi, scaled(HudSettings.SnapGrid));
    }

    public override void OnMouseMove(MouseEvent args)
    {
        var step = scaled(HudSettings.SnapGrid);
        if (!Assert(step > 0)) return;
        if (_drag != null && Assert(_drag.W > 0)) _drag.Click(Math.Clamp((args.X - _canvas.X - _drag.X) / _drag.W, 0, 1));
        else if (_grid != null) _pinned = (Math.Round((args.X - _grabX) / step) * step, Math.Round((args.Y - _grabY) / step) * step);
        else return;
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args) => Release();

    private void Release()
    {
        _drag = null;
        _grid?.Dispose();
        _grid = null;
    }

    public override void Dispose()
    {
        base.Dispose();
        _grid?.Dispose();
        _canvas.Dispose();
    }

    private void Render()
    {
        _dirty = false;
        _hits.Clear();
        var s = _settings;
        double pad = scaled(HudPanel.Padding), gap = scaled(HudPanel.Gap), segmentGap = scaled(SegmentGap), knob = scaled(KnobSize);
        double rowH = HudCanvas.BadgeHeight(s.Header) + scaled(RowGap), headerRow = HudCanvas.HeaderHeight(s.Header);
        var titleRow = Math.Max(HudCanvas.LineHeight(s.Title), HudCanvas.BadgeHeight(s.Header)) + scaled(RowGap);
        if (!Assert(rowH > 0) || !Assert(headerRow > 0) || !Assert(titleRow > 0) || !Index(_page, 2)) return;
        string[] onOff = [HudSettings.Translate("settings-on"), HudSettings.Translate("settings-off")], cornerNames = Names("corner", Corners), pages = Names("page", ["hud", "ab"]);
        string[] buttons = [HudSettings.Translate("settings-do-reset"), HudSettings.Translate("settings-do-copy"), HudSettings.Translate("settings-do-bench", s.BenchSeconds)];

        double labelW = 0, valueW = 0, buttonW = 0;
        for (var i = 0; i < Labels.Length; i++) labelW = Math.Max(labelW, HudCanvas.TextWidth(s.Text, HudSettings.Translate("settings-" + Labels[i])));
        for (var i = 0; i < Ranges.Length; i++) valueW = Math.Max(valueW, HudCanvas.TextWidth(s.Text, Ranges[i].Text(Ranges[i].Max)));
        foreach (var text in buttons.Bounded(MaxSegments)) buttonW = Math.Max(buttonW, HudCanvas.BadgeWidth(s.Header, text));
        var controlW = Math.Max(Math.Max(GroupWidth(onOff), GroupWidth(cornerNames)), Math.Max(scaled(MinSliderWidth) + gap + valueW, buttonW));
        double controlX = pad + labelW + gap, width = controlX + controlW + pad, right = width - pad;
        if (!Assert(labelW > 0) || !Assert(valueW > 0) || !Assert(controlW > 0)) return;
        var rows = new List<(double Height, Action<double> Draw)>
        {
            (titleRow, y =>
            {
                var title = HudSettings.Translate("hud-title");
                _canvas.Text(pad, y, titleRow, s.Title, title);
                _ = _canvas.Badge(pad + HudCanvas.TextWidth(s.Title, title) + scaled(HudLine.BadgeGap), y, titleRow, s.Header, HudSettings.Translate("settings-title"), HudCanvas.Accent);
                _canvas.Text(right - HudCanvas.TextWidth(s.Title, "×"), y, titleRow, s.Title, "×");
                _hits.Add(new HitBox(right - titleRow, y, titleRow, titleRow, _ => TryClose()));
            }),
            (rowH, y => Segments(y, pages, _page, i => { _page = i; _dirty = true; }, left: pad, span: width - 2 * pad)),
        };

        if (_page == 0)
        {
            Header("display");
            Setting("visible", y => Switch(y, s.Visible, on => s.Visible = on));
            Setting("corner", y => Segments(y, cornerNames, s.Pinned.Count > 0 ? Custom : (int)s.Corner, i => s.SetCorner((HudCorner)i), passive: Custom));
            Setting("opacity", y => Slider(y, s.Opacity, HudSettings.OpacityRange, v => s.Opacity = v));
            Setting("scale", y => Slider(y, s.FontScale, HudSettings.ScaleRange, v => s.FontScale = v));
            Setting("updates", y => Switch(y, s.UpdateCheck, on => { s.UpdateAsked = true; s.UpdateCheck = on; }));
            Header("panels");
            Setting("graph", y => Switch(y, s.ShowGraph, on => s.ShowGraph = on));
            Setting("system", y => Switch(y, s.ShowSystem, on => s.ShowSystem = on));
            Setting("passes", y => Switch(y, s.ShowPasses, on => s.ShowPasses = on));
            Setting("mods", y => Switch(y, s.ShowMods, on => s.ShowMods = on));
            Setting("modtimes", y => Switch(y, s.ShowModTimes, on => s.ShowModTimes = on));
            Setting("log", y => Switch(y, s.ShowLog, on => s.ShowLog = on));
            Setting("debuglog", y => Switch(y, s.ShowDebugLog, on => s.ShowDebugLog = on));
            Setting("detail", y => Switch(y, s.Detail, on => s.Detail = on, enabled: s.ShowPasses || s.ShowModTimes));
            Header("measure");
            Setting("interval", y => Slider(y, s.Interval, HudSettings.IntervalRange, v => s.Interval = v));
            Setting("bench", y => Slider(y, s.BenchSeconds, HudSettings.BenchRange, v => s.BenchSeconds = v));
            Header("actions");
            Setting("positions", y => Button(y, buttons[0], s.ResetPositions));
            Setting("values", y => Button(y, buttons[1], _dump));
            Setting("benchmark", y => Button(y, buttons[2], () => _bench((float)s.BenchSeconds)));
            Setting("defaults", y => Button(y, buttons[0], s.ResetDefaults));
        }
        else
        {
            Header("patches");
            Setting("shadercache", y => Switch(y, s.ShaderUseCache, on => s.ShaderUseCache = on));
            Header("actions");
            Setting("values", y => Button(y, buttons[1], _dump));
            Setting("benchmark", y => Button(y, buttons[2], () => _bench((float)s.BenchSeconds)));
        }

        var height = 2 * pad;
        foreach (var (rowHeight, _) in rows.Bounded(MaxRows)) height += rowHeight;
        if (!Assert(rows.Count > 2) || !Assert(rows.Count <= MaxRows) || !Assert(height > 0)) return;
        _canvas.Begin(width, height);
        if (!Assert(_canvas.Width >= width)) return;   // Begin() refused the size
        _canvas.Fill(0, 0, width, height, HudCanvas.PanelBackground with { A = s.Opacity }, scaled(4));
        var top = pad;
        foreach (var (rowHeight, draw) in rows.Bounded(MaxRows))
        {
            draw(top);
            top += rowHeight;
        }
        _canvas.End();
        return;

        void Header(string key)
        {
            if (!Assert(key.Length > 0)) return;
            if (rows.Count > 1) rows.Add((HudCanvas.RuleHeight, y => _canvas.Rule(pad, y, width - (2 * pad), HudCanvas.RuleHeight)));
            rows.Add((headerRow, y => _canvas.Header(pad, y, width - (2 * pad), headerRow, s.Header, HudSettings.Translate("settings-" + key))));
        }

        void Setting(string key, Action<double> control)
        {
            if (!Assert(Array.IndexOf(Labels, key) >= 0)) return;   // the label column is measured from Labels
            rows.Add((rowH, y =>
            {
                _canvas.Text(pad, y, rowH, s.Text, HudSettings.Translate("settings-" + key));
                control(y);
            }));
        }

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
                else if (i != passive) _hits.Add(new HitBox(segX, y, w, rowH, _ => select(index)));
                _ = _canvas.Badge(segX, y, rowH, s.Header, names[i], color, w);
            }
        }

        void Slider(double y, double value, HudRange range, Action<double> set)
        {
            if (!Assert(range.Max > range.Min) || !Assert(range.Step > 0) || !Assert(range.Contains(value))) return;
            double w = controlW - gap - valueW, fraction = Math.Clamp((value - range.Min) / (range.Max - range.Min), 0, 1);
            if (!Assert(w > 0)) return;
            var text = range.Text(value);
            _canvas.Bar(controlX, y, rowH, w, fraction, 0, HudCanvas.Accent);
            _canvas.Fill(controlX + (w * fraction) - (knob / 2), y + ((rowH - knob) / 2), knob, knob, Knob, knob / 2);
            _canvas.Text(right - HudCanvas.TextWidth(s.Text, text), y, rowH, s.Text, text);
            _hits.Add(new HitBox(controlX, y, w, rowH, f => set(Math.Round(range.Min + Math.Round(f * (range.Max - range.Min) / range.Step) * range.Step, 2)), Drag: true));
        }

        void Button(double y, string text, Action click)
        {
            if (!Assert(text.Length > 0)) return;
            _hits.Add(new HitBox(controlX, y, controlW, rowH, _ => click()));
            _ = _canvas.Badge(controlX, y, rowH, s.Header, text, HudCanvas.Neutral, controlW);
        }
    }

    private static string[] Names(string key, string[] values)
        => Assert(key.Length > 0) && Assert(values.Length > 0) ? Array.ConvertAll(values, value => HudSettings.Translate($"settings-{key}-{value}")) : [];
}
