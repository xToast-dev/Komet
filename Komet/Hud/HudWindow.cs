namespace Komet.Hud;

// The HUD's window (Ctrl+F7), drawn as the mockup: a title row (Komet, its badges, Debug, the gear for the settings, the cross), the
// tabs with the open one underlined, and the tab's view under them, scrolled under a clip. One canvas, composed again when
// something it shows changed: a click, a key, the scroll, new numbers (at most every RefreshMs), the fonts or the frame size.
internal sealed class HudWindow : GuiDialog
{
    // Settings opens from the gear and Version from the title's badges, neither has a tab
    public static readonly string[] Tabs = ["overview", "frames", "system", "render", "threads", "mods", "log", "settings", "version"];
    public const int Overview = 0, Frames = 1, System = 2, Render = 3, Threads = 4, Mods = 5, Log = 6, Settings = 7, Version = 8;

    private const double MaxWidth = 520, MaxHeight = 560, Margin = 24, RefreshMs = 400;
    private const float Depth = 60;

    private readonly HudSettings _settings;
    private readonly HudUiFonts _fonts;
    private readonly HudCanvas _canvas;
    private readonly List<HudUi.Hit> _hits = [];
    private readonly double[] _scroll = new double[Tabs.Length];
    private readonly Action<HudUi, int> _view;
    private readonly System.Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> _badges;
    private readonly HudMotion _motion;
    private readonly SnapGrid _snap;
    private readonly (double X, double Y, double W, double H)[] _tabAt = new (double, double, double, double)[Settings];
    private int _tab;
    private double _content, _bodyTop, _grabX, _grabY, _titleH;
    private (double W, double H) _size;
    private bool _dirty = true, _urgent = true, _moving;
    private long _composed;
    private int _epoch;

    public HudWindow(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, SnapGrid snap, Action<HudUi, int> view,
        System.Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> badges) : base(capi)
    {
        _canvas = new HudCanvas(capi);
        _motion = new HudMotion(capi);
        (_settings, _fonts, _snap, _view, _badges) = (settings, fonts, snap, view, badges);
        _tab = Math.Max(0, Array.IndexOf(Tabs, settings.WindowTab));
        _ = Assert(Tabs.Length == Version + 1) && NotNull(view);
    }

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;
    public override double DrawOrder => 0.9;
    public override bool CaptureAllInputs() => Typing?.Invoke() == true;

    public int Tab => _tab;

    // The tab changed or the window opened or closed: the HUD switches its collectors
    public event Action? Changed;

    public event Action? Composing;

    // A key for the view while it takes text (the mods' search); true when the view took it
    public System.Func<KeyEvent, bool, bool>? Keys { get; set; }
    public System.Func<bool>? Typing { get; set; }
    public Action? OpenDebug { get; set; }

    public bool Shows(int tab) => IsOpened() && _tab == tab;

    public void Toggle(int? tab = null)
    {
        if (!NotNull(_canvas) || !Assert(tab is null || Index(tab.Value, Tabs.Length))) return;
        if (tab is { } wanted) Select(wanted);
        if (!IsOpened()) _ = TryOpen();
        else if (_motion.Closing) _motion.Open();
        else if (tab is null) _ = TryClose();
    }

    // Fades out first; the dialog closes when the fade is done
    public override bool TryClose()
    {
        if (!IsOpened() || !NotNull(_motion)) return false;
        _motion.Close();
        return true;
    }

    private void Finish()
    {
        if (_motion.Closing && IsOpened()) _ = base.TryClose();
        _ = Assert(_tab >= 0);
    }

    public void Select(int tab)
    {
        _ = NotNull(_settings);
        if (!Index(tab, Tabs.Length)) return;
        Invalidate(true);
        if (tab == _tab) return;
        _tab = tab;
        _settings.WindowTab = Tabs[tab];
        Changed?.Invoke();
    }

    // New numbers (urgent: an action of the player, composed on the next frame)
    public void Invalidate(bool urgent = false) => (_dirty, _urgent) = (true, _urgent || urgent);

    public override void OnGuiOpened()
    {
        if (!Assert(Tabs.Length > 0) || !NotNull(_canvas)) return;
        base.OnGuiOpened();
        _motion.Open();
        Invalidate(true);
        Changed?.Invoke();
    }

    public override void OnGuiClosed()
    {
        if (!Assert(_tab >= 0) || !NotNull(_settings)) return;
        _moving = false;
        Changed?.Invoke();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (!Finite(deltaTime) || !IsOpened() || !Assert(w > 0) || !Assert(h > 0)) return;
        _fonts.Update(_settings.FontScale);
        var size = (Math.Min(w - 2 * scaled(Margin), scaled(MaxWidth * _fonts.Scale)),
            Math.Min(h - 2 * scaled(Margin), scaled(MaxHeight * _fonts.Scale)));
        var due = _urgent || Environment.TickCount64 - _composed >= RefreshMs;
        if ((_dirty && due) || _epoch != _fonts.Epoch || size != _size) Compose(size);
        if (!_canvas.Ready) return;
        var x = double.IsFinite(_settings.WindowX) ? _settings.WindowX : (w - _size.W) / 2;
        var y = double.IsFinite(_settings.WindowY) ? _settings.WindowY : (h - _size.H) / 2;
        if (_moving) _snap.Draw();
        _motion.Draw(_canvas, Math.Round(x), Math.Round(y), Depth);
        if (_tab < Settings) _motion.Slide(_canvas, _tabAt[_tab], Depth, HudUi.Accent);
        _motion.Hover(_canvas, _hits, Depth);
        if (_motion.Closed) capi.Event.EnqueueMainThreadTask(Finish, "komet-hud-close");
    }

    private void Compose((double W, double H) size)
    {
        if (!Assert(size.W > 0) || !Assert(size.H > 0)) return;
        (_dirty, _urgent, _epoch, _size, _composed) = (false, false, _fonts.Epoch, size, Environment.TickCount64);
        Composing?.Invoke();
        _hits.Clear();
        if (!_canvas.Blank(size.W, size.H)) return;
        _canvas.Fill(0, 0, size.W, size.H, HudUi.Back, scaled(4));
        var chrome = new HudUi(_canvas, _fonts, (scaled(8), size.W - scaled(16), 0, size.H, 0), _hits);
        Title(chrome, size.W);
        TabBar(chrome, size.W);
        Body(size);
        _canvas.End();
        _ = Assert(_hits.Count <= HudUi.MaxHits);
    }

    private void Title(HudUi ui, double width)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (y, row) = (ui.Px(5), _fonts.StrongRow + ui.Px(4));
        _titleH = y + row;
        ui.Text(ui.X, y, row, _fonts.Strong, "Komet");
        var x = ui.X + ui.TextW(_fonts.Strong, "Komet") + ui.Px(6);
        foreach (var (text, fill, color, click) in _badges().Bounded(4))
        {
            var w = ui.TextW(_fonts.Small, text) + ui.Px(10);
            ui.Fill(x, y + (row - _fonts.SmallRow) / 2, w, _fonts.SmallRow, fill, ui.Px(3));
            ui.Text(x + ui.Px(5), y + (row - _fonts.SmallRow) / 2, _fonts.SmallRow, _fonts.Small, text, color);
            if (click is not null) ui.Click(x, y + (row - _fonts.SmallRow) / 2, w, _fonts.SmallRow, click);
            x += w + ui.Px(4);
        }

        var right = width - ui.Px(8);
        var size = ui.Px(10);
        ui.Cross(right - size, y + (row - size) / 2, size, Rgba.White(0.85));
        ui.Click(right - size - ui.Px(4), y, size + ui.Px(8), row, () => TryClose());
        right -= size + ui.Px(14);
        ui.Gear(right - ui.Px(12), y + (row - ui.Px(12)) / 2, ui.Px(12), _tab == Settings ? Rgba.White(1) : HudUi.Dim);
        ui.Click(right - ui.Px(16), y, ui.Px(20), row, () => Select(_tab == Settings ? Overview : Settings));
        right -= ui.Px(26);
        var debug = HudText.Translate("hud-v-debug");
        _ = ui.LinkText(right - ui.TextW(_fonts.Body, debug), y + (row - _fonts.BodyRow) / 2, debug, () => OpenDebug?.Invoke());
    }

    private void TabBar(HudUi ui, double width)
    {
        if (!NotNull(ui) || !Assert(ui.W > 0)) return;
        var (top, h) = (_titleH + ui.Px(2), _fonts.BodyRow + ui.Px(10));
        var x = ui.Px(6);
        for (var i = 0; i < Settings; i++)
        {
            var (label, tab) = (HudText.Translate("hud-tab-" + Tabs[i]), i);
            var w = ui.TextW(_fonts.Body, label) + ui.Px(10);
            ui.Text(x + ui.Px(5), top + ui.Px(5), _fonts.BodyRow, _fonts.Body, label, i == _tab ? Rgba.White(1) : Rgba.White(0.55));
            _tabAt[i] = (x, top + h - ui.Px(2), w, ui.Px(2));
            ui.Click(x, top, w, h, () => Select(tab));
            x += w;
        }

        ui.Fill(0, top + h, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        _bodyTop = top + h + 1;
        _ = Assert(_bodyTop < _size.H);
    }

    // The tab's view under a clip; the scroll clamped to what it drew, the bar beside it
    private void Body((double W, double H) size)
    {
        if (!Assert(size.W > 0) || !NotNull(_view)) return;
        var bar = scaled(6);
        var (top, bottom) = (_bodyTop + scaled(4), size.H - scaled(10));
        var max = Math.Max(0, _content - (bottom - top));
        _scroll[_tab] = Math.Clamp(_scroll[_tab], 0, max);
        _canvas.Clip(0, _bodyTop, size.W, size.H - _bodyTop);
        var ui = new HudUi(_canvas, _fonts, (scaled(10), size.W - scaled(20) - bar, top, bottom, _scroll[_tab]), _hits);
        _view(ui, _tab);
        _canvas.Unclip();
        _content = ui.Y + scaled(6);
        if (_content <= bottom - top || !Assert(_content > 0)) return;
        var thumb = Math.Max(scaled(24), (bottom - top) * (bottom - top) / _content);
        var at = top + (bottom - top - thumb) * _scroll[_tab] / Math.Max(1, _content - (bottom - top));
        _canvas.Fill(size.W - bar - scaled(4), top, bar - scaled(2), bottom - top, Rgba.White(0.05), scaled(2));
        _canvas.Fill(size.W - bar - scaled(4), at, bar - scaled(2), thumb, Rgba.White(0.28), scaled(2));
    }

    public bool Contains(double px, double py) => IsOpened() && _canvas.Ready && _canvas.Contains(px, py);

    public override void OnMouseDown(MouseEvent args)
    {
        if (!NotNull(args) || !Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (_motion.Closing) return;
        var (x, y) = (args.X - _canvas.X, args.Y - _canvas.Y);
        if (HudUi.HitAt(_hits, x, y) is { } hit)
        {
            _motion.Press(hit);
            hit.Click();
            Invalidate(true);
            return;
        }

        if (y < _titleH && args.Button == EnumMouseButton.Left) (_grabX, _grabY, _moving) = (x, y, true);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (!_moving || !NotNull(args)) return;
        (_settings.WindowX, _settings.WindowY) = (HudMotion.Snap(args.X - _grabX, _size.W, capi.Render.FrameWidth),
            HudMotion.Snap(args.Y - _grabY, _size.H, capi.Render.FrameHeight));
        args.Handled = Assert(_grabX >= 0);
    }

    public override void OnMouseUp(MouseEvent args) => _moving = false;

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!NotNull(args) || !Contains(capi.Input.MouseX, capi.Input.MouseY) || !Finite(args.deltaPrecise)) return;
        _scroll[_tab] -= args.deltaPrecise * _fonts.BodyRow * 3;
        Invalidate(true);
        args.SetHandled();
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (!Assert(_tab >= 0)) return;
        if (!NotNull(args) || Keys?.Invoke(args, false) != true) return;
        args.Handled = true;
        Invalidate(true);
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (!NotNull(args) || Keys?.Invoke(args, true) != true) return;
        args.Handled = true;
        Invalidate(true);
    }

    public override void Dispose()
    {
        base.Dispose();
        _canvas.Dispose();
        _motion.Dispose();
    }
}
