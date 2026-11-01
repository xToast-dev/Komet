using Komet.Api;
using Vintagestory.Client.NoObf;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// Recomposed only on change, painted by Cairo on a worker (the main thread records and uploads); the hover highlight is
// a translucent rectangle on top.
internal sealed partial class OptionsScreen
{
    // Sizes before GUI scale
    private const double TextScale = 1.4, Margin = 28, Space = 10, PageHeight = 44, GroupHeight = 32, RowHeight = 42, Spacer = 8,
        Pad = 18, AccentBar = 4, ClearWidth = 26;

    private const int MaxItems = 512, MaxLines = 12, MaxWords = 256, MaxSteps = 64, MaxDialogs = 256;
    private const double GearSize = 85, GearShown = 0.55, MinShare = 0.5; // the hotbar's temporal gear: its size, the part above

    private const float DimDepth = 900, ScreenDepth = 1000;
    private const double FadeSeconds = 0.5;
    private const double SoftGlow = 0.04, RowGlow = 0.14, StrongGlow = 0.22, Flash = 0.1, Speed = 20, FlashFade = 7, Settle = 0.003;
    private const int TopGroup = 1, SideGroup = 2, ListGroup = 3;
    private const float GlowDepth = ScreenDepth + 1;
    private double _fade; // how far the blur and the dim have come in since the screen opened, 0 to 1
    private double _sideScroll; // the page list's
    private (double X, double Y) _mouse = (-1, -1); // in canvas pixels
    private Light _glow, _target;
    private double _flash;

    private enum ItemKind
    {
        Page,
        Group,
        Row,
        Space
    }

    private sealed record Item(ItemKind Kind, double Height, OptionPage Page, OptionRow? Row = null, string Text = "");

    private readonly record struct Light(double X, double Y, double W, double H, double Alpha, int Group);

    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !IsOpened()) return;
        if (_embedded is { Handler: GuiDialog menu } && !menu.IsOpened())
        {
            Leave(); // the escape menu closed under the screen
            return;
        }

        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (!Assert(w > 0 && h > 0)) return;
        _clock += deltaTime;
        _fonts.Update(TextScale);
        var bottom = Math.Round(Bottom(h));
        Animate(deltaTime);
        Moving();
        if (_dirty || _epoch != _fonts.Epoch || _frame != (w, h) || Math.Abs(bottom - _bottom) >= 1) Compose(w, h, bottom);
        // HUD depth left behind would let hotbar, chat and minimap beat the screen; no depth test, no depth written.
        capi.Render.GLDisableDepthTest();
        try
        {
            _fade = Finite(_fade) ? Math.Min(1, _fade + deltaTime / FadeSeconds) : 1;
            Underlay(_fade * _fade * (3 - 2 * _fade), deltaTime); // smoothstep: no jolt at either end
            var (alpha, scale) = Dolly(deltaTime);
            if (_canvas.Ready) Picture(alpha, scale, deltaTime);
            Flare(w, h);
        }
        finally
        {
            capi.Render.GLEnableDepthTest(); // as the ortho stage has it
        }
    }

    // The shown picture (the whole frame) with its grounds under it and its lights; alpha and scale about its centre (a
    // change of style)
    private void Picture(double alpha, double scale, float deltaTime)
    {
        if (!Finite(alpha) || !Finite(scale) || !Assert(scale > 0)) return;
        if (!ReferenceEquals(_canvas.Shown, _lastShown)) Retarget(); // a new picture's boxes: the highlight follows them
        if (alpha <= 0.002) return;
        const double x = 0, y = 0;
        _lens = (_canvas.Width / 2, _canvas.Height / 2, scale, alpha);
        Backs(x, y);
        Mark(deltaTime, x, y);
        if (_slide < SlidePending + 0.5 && OnScreen.Page == _slideFor) (_slide, _decodeAt) = (_clock, _clock); // the new page shows
        var still = alpha >= 0.999 && Math.Abs(scale - 1) < 1e-4;
        if (still && _slide >= 0) Sliding(x, y);
        else if (still) _canvas.Draw(x, y, ScreenDepth);
        else _canvas.Fade(x, y, ScreenDepth, alpha, scale);
        Titled(x, y);
        Glow(deltaTime);
        Pressed(x, y);
    }

    private void Compose(int width, int height, double bottom)
    {
        if (_epoch != _fonts.Epoch) _widest.Clear();
        (_dirty, _epoch, _frame, _bottom) = (false, _fonts.Epoch, (width, height), bottom);
        Composing?.Invoke();
        _map = FreeMap();
        (_map.Style, _map.Page) = (_style, Filtering ? "" : _page);
        _canvas.BeginMeasure();
        if (Tabs(width, bottom)) _canvas.End(_map);
    }

    // Above the hotbar, its bars and gear; the frame margin without them (spectator) or when less than half would be
    // left
    private double Bottom(int height)
    {
        var (bottom, top) = (height - scaled(Margin), (double)height);
        var gui = capi.Gui?.OpenedGuis;
        if (gui is null || capi.World?.Player?.WorldData?.CurrentGameMode == EnumGameMode.Spectator) return bottom;
        for (var i = 0; i < Math.Min(gui.Count, MaxDialogs); i++) // every frame: no enumerator
        {
            var dialog = gui[i];
            var bounds = dialog switch
            {
                HudHotbar => dialog.Composers["hotbar"]?.Bounds,
                HudStatbar => dialog.Composers["statbar"]?.Bounds,
                _ => null
            };
            if (bounds is null || !Finite(bounds.renderY) || bounds.renderY <= 0) continue;
            top = Math.Min(top, bounds.renderY);
            // renderGear: gear (85 scaled, 5 around) reaches 0.55 of itself above a line 30 under the composer top
            if (dialog is HudHotbar && capi.World?.Config?.GetBool("temporalStability", true) != false)
                top = Math.Min(top, bounds.renderY + scaled(30) - (Math.Ceiling(scaled(GearSize)) + 10) * GearShown);
        }

        var above = top - S(Space);
        return above - scaled(Margin) >= height * MinShare ? Math.Min(bottom, above) : bottom;
    }

    // The open page's groups and rows (a search or the review: Found); the list draws them at the style's row height
    private List<Item> Items()
    {
        if (_reviewing && _staged.Count == 0) _reviewing = false; // all applied or undone: the page again
        if (Filtering) return Found();
        List<Item> items = [];
        if (Array.Find(_pages, p => p.Id == _page) is not { } page) return items;
        for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
        {
            var row = page[i];
            if (!NotNull(row) || !Assert(items.Count < MaxItems)) continue;
            if (row.Kind == OptionKind.Group) items.Add(new Item(ItemKind.Space, scaled(Spacer), page));
            items.Add(row.Kind == OptionKind.Group
                ? new Item(ItemKind.Group, scaled(GroupHeight), page, Text: row.Shows?.Invoke() ?? row.Label)
                : new Item(ItemKind.Row, scaled(RowHeight), page, row));
        }

        return items;
    }

    private List<Item> Found()
    {
        List<Item> items = [];
        if (!Assert(_query.Length <= MaxQuery)) return items;
        foreach (var page in _pages.Bounded(MaxPages))
        {
            var header = false;
            for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
            {
                var row = page[i];
                if (row.Kind == OptionKind.Group || !Matches(row)) continue;
                if (!header) items.Add(new Item(ItemKind.Page, scaled(PageHeight), page, Text: page.Section + " › " + page.Title));
                header = true;
                items.Add(new Item(ItemKind.Row, scaled(RowHeight), page, row));
            }
        }

        if (items.Count == 0 && _pages.Length > 0)
            items.Add(new Item(ItemKind.Group, scaled(GroupHeight), _pages[0], Text: T("nothing-found")));
        return items;
    }

    private bool Matches(OptionRow row) => _reviewing ? _staged.ContainsKey(row) :
        row.Label.Contains(_query, StringComparison.CurrentCultureIgnoreCase) ||
        (row.Hint ?? "").Contains(_query, StringComparison.CurrentCultureIgnoreCase);

    private static double S(double size) => Finite(size) ? scaled(size) : 0;

    private List<string> Wrap(string text, CairoFont font, double width)
    {
        List<string> lines = [];
        if (!NotNull(text) || !Assert(width > 0)) return lines;
        foreach (var paragraph in text.Split('\n').Bounded(MaxLines)) _canvas.Wrap(font, paragraph, width, MaxWords, lines);
        return lines;
    }

    // The hover light's target: the box under the mouse; the list's rows have the lit row's mark (Mark) instead
    private void Retarget()
    {
        var (x, y) = _mouse;
        var map = OnScreen;
        _lastShown = _canvas.Shown;
        if (!Assert(map.Hits.Count <= MaxHits)) return;
        var hit = Under(map.Hits, x, y, null);
        var (vx, vy, vw, vh) = map.View;
        if (hit is null && x >= vx && x < vx + vw && y >= vy && y < vy + vh) hit = Under(map.Rows, x, y, true);
        if (hit is null || hit.Glow <= 0 || hit.Group == ListGroup)
        {
            _target = _target with { Alpha = 0 };
            return;
        }

        _target = new Light(hit.X, hit.Y, hit.W, hit.H, hit.Glow, hit.Group);
    }

    private void Glow(float deltaTime)
    {
        var (t, k) = (_target, 1 - Math.Exp(-deltaTime * Speed));
        if (t.Alpha > 0 && (_glow.Alpha < Settle || _glow.Group != t.Group))
            _glow = t with { Alpha = _glow.Group == t.Group ? _glow.Alpha : 0 }; // appears where it is: no glide across the screen
        if (t.Alpha > 0) _glow = _glow with { X = Ease(_glow.X, t.X, k), Y = Ease(_glow.Y, t.Y, k), W = Ease(_glow.W, t.W, k),
            H = Ease(_glow.H, t.H, k) };
        _glow = _glow with { Alpha = Ease(_glow.Alpha, t.Alpha, k) };
        _flash *= Math.Exp(-deltaTime * FlashFade);
        var alpha = Math.Min(1, _glow.Alpha + (t.Alpha > 0 ? _flash : 0));
        if (alpha < Settle || _glow.W <= 0 || _glow.H <= 0) return;
        Rect(_canvas.X + _glow.X, _canvas.Y + _glow.Y, _glow.W, _glow.H, _style["glow"] with { A = alpha }, GlowDepth);
    }

    private static double Ease(double from, double to, double k) =>
        Finite(from) && Finite(to) && Finite(k) ? from + (to - from) * k : to;
}
