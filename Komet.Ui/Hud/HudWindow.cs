using Komet.Core;
namespace Komet.Hud;

// Ctrl+F7. Recomposed only on change: input, scroll, new numbers (at most every RefreshMs), fonts or frame size.
internal sealed class HudWindow : HudDialog
{
    public static readonly string[] Tabs = ["overview", "frames", "system", "render", "threads", "log", "settings", "version"];
    public const int Overview = 0, Frames = 1, System = 2, Render = 3, Threads = 4, Log = 5, Settings = 6, Version = 7;

    private const double MaxWidth = 520, MaxHeight = 560, RefreshMs = 400;
    private const float Depth = 60;

    private readonly HudSettings _settings;
    private readonly double[] _scroll = new double[Tabs.Length], _content = new double[Tabs.Length]; // per tab
    private readonly Action<HudUi, int> _view;
    private readonly System.Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> _badges;
    private readonly (double X, double Y, double W, double H)[] _tabAt = new (double, double, double, double)[Settings];
    private int _tab;
    private double _bodyTop, _titleH;

    public HudWindow(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, SnapGrid snap, Action<HudUi, int> view,
        System.Func<(string Text, Rgba Fill, Rgba Color, Action? Click)[]> badges) : base(capi, settings, fonts, snap, Depth)
    {
        (_settings, _view, _badges) = (settings, view, badges);
        _tab = Math.Max(0, Array.IndexOf(Tabs, settings.WindowTab)); // a tab a saved file still names ("mods") opens the overview
    }

    public override double DrawOrder => 0.9;

    public int Tab => _tab;

    public event Action? Changed; // Tab changed or window opened/closed: the HUD switches its collectors

    public Action? OpenDebug { get; set; }

    public bool Shows(int tab) => IsOpened() && _tab == tab;

    public void Toggle(int? tab = null)
    {
        if (!Assert(tab is null || Index(tab.Value, Tabs.Length))) return;
        if (tab is { } wanted) Select(wanted);
        Show(tab is null);
    }

    public void Select(int tab)
    {
        if (!Index(tab, Tabs.Length)) return;
        Invalidate(true);
        if (tab == _tab) return;
        _tab = tab;
        _settings.WindowTab = Tabs[tab];
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
        var size = Fitted(MaxWidth, MaxHeight);
        if (Recompose(size, RefreshMs)) Compose(size);
        if (!Canvas.Ready) return;
        Present(_settings.WindowX, _settings.WindowY);
        if (_tab < Settings) Motion.Slide(Canvas, _tabAt[_tab], Depth, HudUi.Accent);
        Hover();
    }

    private void Compose((double W, double H) size)
    {
        if (!Assert(size.W > 0)) return;
        HudUi.Ground(Canvas, size.W, size.H, HudUi.Back, scaled(4));
        var chrome = new HudUi(Canvas, Fonts, (scaled(8), size.W - scaled(16), 0, size.H, 0), Hits);
        Title(chrome, size.W);
        TabBar(chrome, size.W);
        Body(size);
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

        if (framed) x += ui.Spaced(x, y + (row - Fonts.StrongRow) / 2, Fonts.StrongRow, Fonts.Strong, "KOMET", ui.Px(3)) + ui.Px(10);
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
        ui.Gear(right - ui.Px(12), y + (row - ui.Px(12)) / 2, ui.Px(12), _tab == Settings ? Rgba.White(1) : HudUi.Dim);
        ui.Click(right - ui.Px(16), y, ui.Px(20), row, () => Select(_tab == Settings ? Overview : Settings));
        right -= ui.Px(26);
        var debug = KometText.Translate("hud-v-debug");
        _ = ui.LinkText(right - ui.TextW(Fonts.Body, debug), y + (row - Fonts.BodyRow) / 2, debug, () => OpenDebug?.Invoke());
    }

    // A tabs style writes the tabs as the menu's: capitals, spaced
    private void TabBar(HudUi ui, double width)
    {
        var (top, h, framed) = (_titleH + ui.Px(2), Fonts.BodyRow + ui.Px(10), HudUi.Framed);
        var (x, tracking) = (framed ? ui.X - ui.Px(5) : ui.Px(6), ui.Px(1.2));
        for (var i = 0; i < Settings; i++)
        {
            var (label, tab) = (KometText.Cached("hud-tab-", Tabs[i]), i);
            var idle = framed ? HudUi.Faint : Rgba.White(0.55);
            var color = i == _tab ? Rgba.White(1) : idle;
            double w;
            if (framed)
            {
                label = label.ToUpperInvariant();
                w = ui.Spaced(x + ui.Px(6), top + ui.Px(5), Fonts.BodyRow, Fonts.Small, label, tracking, color) + ui.Px(12);
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
    }

    private void Body((double W, double H) size)
    {
        var bar = scaled(6);
        var (top, bottom) = (_bodyTop + scaled(4), size.H - scaled(10));
        _scroll[_tab] = Math.Clamp(_scroll[_tab], 0, Math.Max(0, _content[_tab] - (bottom - top)));
        Canvas.Clip(0, _bodyTop, size.W, size.H - _bodyTop);
        var ui = new HudUi(Canvas, Fonts, (scaled(10), size.W - scaled(20) - bar, top, bottom, _scroll[_tab]), Hits);
        _view(ui, _tab);
        Canvas.Unclip();
        _content[_tab] = ui.Y + scaled(6);
        ScrollBar(size.W - bar - scaled(4), bar - scaled(2), top, bottom - top, _content[_tab], _scroll[_tab]);
    }

    protected override bool Grabs(double x, double y, EnumMouseButton button) =>
        Finite(x) && y < _titleH && button == EnumMouseButton.Left;

    protected override void Moved(double x, double y)
    {
        if (Finite(x) && Finite(y)) (_settings.WindowX, _settings.WindowY) = (x, y);
    }

    protected override void Scrolled(double notches)
    {
        if (Finite(notches)) _scroll[_tab] += notches * Fonts.BodyRow * 3;
    }
}
