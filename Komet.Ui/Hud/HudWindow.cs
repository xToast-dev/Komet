using Komet.Core;
using Komet.Ui.Hud;

namespace Komet.Hud;

// The window's views: the flat tab bar, and the pages behind the gear (settings) and the title's badges (version)
internal sealed record HudViews(IHudTab[] Bar, IHudTab Settings, IHudTab Version);

// Ctrl+F7: one dockable window over every tool, a flat tab bar of IHudTab views. Recomposed only on change: input,
// scroll, new numbers (at most every RefreshMs), fonts or frame size.
internal sealed class HudWindow : HudDialog
{
    private const int MaxViews = 32;
    private double _barNeed;

    private const double MaxWidth = 520, MaxHeight = 560, WideWidth = 860, WideHeight = 640, RefreshMs = 400;
    private const float Depth = 60;

    private readonly Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> _badges;
    private readonly IHudTab[] _bar, _views; // the bar's tabs first, then the pages
    private readonly double[] _scroll, _content; // per view
    private readonly HudSettings _settings;
    private readonly (double X, double Y, double W, double H)[] _tabAt;
    private readonly HudViews _given;
    private double _bodyTop, _titleH;
    private int _at; // the shown view's index in _views

    public HudWindow(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, SnapGrid snap, HudViews views,
        Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> badges) : base(capi, settings, fonts, snap, Depth)
    {
        (_settings, _given, _badges, _bar) = (settings, views, badges, views.Bar);
        _views = [.. views.Bar, views.Settings, views.Version];
        _ = Assert(_bar.Length > 0 && _views.Length <= MaxViews);
        (_scroll, _content, _tabAt) = (new double[_views.Length], new double[_views.Length],
            new (double, double, double, double)[_bar.Length]);
        Body = new HudBody(() => _scroll[_at], y =>
        {
            _scroll[_at] = Finite(y) ? Math.Max(0, y) : 0;
            Invalidate(true);
        });
        _at = Math.Max(0, IndexOf(Find(settings.WindowTab))); // a tab a saved file still names ("mods") opens the first
    }

    public override double DrawOrder => 0.9;

    // The shown view (also while closed: the one the window opens on)
    public IHudTab Current => _views[_at];

    // The shown view's scrolling body, as the views see it
    private HudBody Body { get; }

    public Action? OpenDebug { get; init; }

    public event Action? Changed; // view changed or window opened/closed: the HUD switches its collectors

    // A view by its id, null when none has it
    public IHudTab? Find(string id)
    {
        foreach (var view in _views.Bounded(MaxViews))
        {
            if (view.Id == id) return view;
        }

        return null;
    }

    private int IndexOf(IHudTab? view) => view is null ? -1 : Array.IndexOf(_views, view);

    public bool Shows(IHudTab view) => IsOpened() && ReferenceEquals(Current, view);

    // Opens on the view (or the last shown); without one an open window closes
    public void Toggle(IHudTab? view = null)
    {
        if (view is not null) Select(view);

        Show(view is null);
    }

    public void Select(IHudTab view)
    {
        var at = IndexOf(view);
        if (!Assert(at >= 0)) return;

        Invalidate(true);
        if (at == _at) return;

        _at = at;
        _settings.WindowTab = view.Id;
        Changed?.Invoke();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Invalidate(true);
        Changed?.Invoke();
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        Changed?.Invoke();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !IsOpened()) return;

        var wide = Current.Wide;
        var size = Fitted(wide ? WideWidth : MaxWidth, wide ? WideHeight : MaxHeight);
        // at least as wide as the tab strip measured last: more tabs (Debug, Inspector), a larger font or a longer
        // language cut the last tabs off at the fixed width
        var room = capi.Render.FrameWidth - 2 * scaled(Margin);
        size = (Math.Max(size.W, Math.Min(room, _barNeed)), size.H);
        if (Current.Live()) Invalidate();

        if (Recompose(size, RefreshMs)) Compose(size);

        if (!Canvas.Ready) return;

        Present(_settings.WindowX, _settings.WindowY);
        if (_at < _bar.Length) Motion.Slide(Canvas, _tabAt[_at], Depth, HudUi.Accent);

        Hover();
    }

    private void Compose((double W, double H) size)
    {
        if (!Assert(size.W > 0)) return;

        HudUi.Ground(Canvas, size.W, size.H, HudUi.Back, scaled(4));
        var chrome = new HudUi(Canvas, Fonts, (scaled(8), size.W - scaled(16), 0, size.H, 0), Hits);
        Title(chrome, size.W);
        TabBar(chrome, size.W);
        ComposeBody(size);
        Canvas.End(Hits);
    }

    private void Title(HudUi ui, double width)
    {
        var framed = HudUi.Framed;
        var logo = framed && HudUi.Logo is { } image ? HudCanvas.Fitted(image, ui.Px(34)) : 0;
        var block = Math.Max(1, Math.Round(logo / 14));
        var frame = logo > 0 ? logo + 4 * block : 0;
        var (y, row) = (ui.Px(framed ? 7 : 5), Math.Max(Fonts.StrongRow + ui.Px(4), frame));
        _titleH = y + row + (framed ? ui.Px(3) : 0);
        // a tabs style: the window's head (title and tabs) on the menu's band, as the menu's header
        if (framed) ui.Fill(0, 0, width, _titleH + ui.Px(2) + Fonts.BodyRow + ui.Px(10), HudUi.Band);

        var x = ui.X;
        if (logo > 0 && HudUi.Logo is { } shown)
        {
            var top = y + (row - frame) / 2;
            HudUi.PixelFrame(Canvas, x, top, frame, block, (Rgba.Black(0.6), HudUi.Soft));
            ui.Image(shown, x + 2 * block, top + 2 * block, logo);
            x += frame + block + ui.Px(8);
        }

        if (framed)
        {
            x += ui.Spaced(x, y + (row - Fonts.StrongRow) / 2, Fonts.StrongRow, Fonts.Strong, "KOMET", ui.Px(3)) +
                 ui.Px(10);
        }
        else
        {
            ui.Text(x, y, row, Fonts.Strong, "Komet");
            x += ui.TextW(Fonts.Strong, "Komet") + ui.Px(6);
        }

        foreach (var (text, fill, color, click) in _badges().Bounded(4))
        {
            var w = ui.TextW(Fonts.Small, text) + ui.Px(10);
            ui.Fill(x, y + (row - Fonts.SmallRow) / 2, w, Fonts.SmallRow, fill, ui.Px(3));
            ui.Text(x + ui.Px(5), y + (row - Fonts.SmallRow) / 2, Fonts.SmallRow, Fonts.Small, text, color);
            if (click is not null) ui.Click(x, y + (row - Fonts.SmallRow) / 2, w, Fonts.SmallRow, click);

            x += w + ui.Px(4);
        }

        var right = width - ui.Px(8);
        var size = ui.Px(10);
        ui.Cross(right - size, y + (row - size) / 2, size, Rgba.White(0.85));
        ui.Click(right - size - ui.Px(4), y, size + ui.Px(8), row, () => TryClose());
        right -= size + ui.Px(14);
        var settings = _given.Settings;
        var on = ReferenceEquals(Current, settings);
        ui.Gear(right - ui.Px(12), y + (row - ui.Px(12)) / 2, ui.Px(12), on ? Rgba.White(1) : HudUi.Dim);
        ui.Click(right - ui.Px(16), y, ui.Px(20), row, () => Select(ReferenceEquals(Current, settings) ? _bar[0] : settings));
        right -= ui.Px(26);
        var debug = KometText.Translate("hud-v-debug");
        _ = ui.LinkText(right - ui.TextW(Fonts.Body, debug), y + (row - Fonts.BodyRow) / 2, debug,
            () => OpenDebug?.Invoke());
    }

    // A tabs style writes the tabs as the menu's: capitals, spaced
    private void TabBar(HudUi ui, double width)
    {
        var (top, h, framed) = (_titleH + ui.Px(2), Fonts.BodyRow + ui.Px(10), HudUi.Framed);
        var (x, tracking) = (framed ? ui.X - ui.Px(5) : ui.Px(6), ui.Px(1.2));
        for (var i = 0; i < Math.Min(_bar.Length, MaxViews); i++)
        {
            var (label, tab) = (KometText.Cached("hud-tab-", _bar[i].Id), _bar[i]);
            var idle = framed ? HudUi.Faint : Rgba.White(0.55);
            var color = i == _at ? Rgba.White(1) : idle;
            double w;
            if (framed)
            {
                label = label.ToUpperInvariant();
                w = ui.Spaced(x + ui.Px(6), top + ui.Px(5), Fonts.BodyRow, Fonts.Small, label, tracking, color) +
                    ui.Px(12);
            }
            else
            {
                w = ui.TextW(Fonts.Body, label) + ui.Px(10);
                ui.Text(x + ui.Px(5), top + ui.Px(5), Fonts.BodyRow, Fonts.Body, label, color);
            }

            _tabAt[i] = (x, top + h - ui.Px(2), w, ui.Px(2));
            ui.Click(x, top, w, h, () => Select(tab));
            x += w;
        }

        ui.Fill(0, top + h, width, Math.Max(1, ui.Px(1)), HudUi.Divider);
        _bodyTop = top + h + 1;
        var need = Math.Ceiling(x + ui.Px(14));
        if (Math.Abs(need - _barNeed) >= 1)
        {
            _barNeed = need;
            Invalidate(); // the next page at the width the strip needs
        }
    }

    private void ComposeBody((double W, double H) size)
    {
        var bar = scaled(6);
        var (top, bottom) = (_bodyTop + scaled(4), size.H - scaled(10));
        _scroll[_at] = Math.Clamp(_scroll[_at], 0, Math.Max(0, _content[_at] - (bottom - top)));
        Canvas.Clip(0, _bodyTop, size.W, size.H - _bodyTop);
        var ui = new HudUi(Canvas, Fonts, (scaled(10), size.W - scaled(20) - bar, top, bottom, _scroll[_at]), Hits);
        Current.Draw(ui, Body);
        Canvas.Unclip();
        _content[_at] = ui.Y + scaled(6);
        ScrollBar(size.W - bar - scaled(4), bar - scaled(2), top, bottom - top, _content[_at], _scroll[_at]);
    }

    protected override bool Grabs(double x, double y, EnumMouseButton button) =>
        Finite(x) && y < _titleH && button == EnumMouseButton.Left;

    protected override void Moved(double x, double y)
    {
        if (Finite(x) && Finite(y)) (_settings.WindowX, _settings.WindowY) = (x, y);
    }

    protected override void Scrolled(double notches)
    {
        if (Finite(notches)) _scroll[_at] += notches * Fonts.BodyRow * 3;
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (NotNull(args) && IsOpened() && Current.Key(args, Body))
        {
            args.Handled = true;
            Invalidate(true);
            return;
        }

        base.OnKeyDown(args);
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (NotNull(args) && IsOpened() && Current.Char(args.KeyChar, Body))
        {
            args.Handled = true;
            Invalidate(true);
            return;
        }

        base.OnKeyPress(args);
    }
}
