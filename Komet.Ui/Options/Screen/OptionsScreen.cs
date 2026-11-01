using Komet.Api;
using Komet.Core;
using Komet.Hud;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Options;

// Sodium-style options: sections as tabs (Vintage Story, Komet, KometOptions mods, Help), a page list with a search,
// the scrolling page and the detail of the lit row. Changes stage until Enter (one batch); Escape drops them; buttons
// and keys act at once.
internal sealed partial class OptionsScreen : GuiDialog
{
    // The footer's chips after the version (the edition, what the update check found) and what a click on them opens
    public Func<IReadOnlyList<(string Text, Rgba Color)>>? VersionState { get; set; }
    public Action? OpenVersion { get; set; }

    private const int MaxPages = 48, MaxStaged = 256, MaxQuery = 64, MaxHits = 4096, PrefetchMs = 5000;

    private readonly KometPages _komet;
    private readonly HudSettings _settings;
    private readonly StyleLibrary _styles;
    private readonly StyleScene _scene;
    private readonly Dictionary<OptionRow, double> _staged = [];
    private readonly HashSet<OptionRow> _failed = [];
    private readonly HudCanvas _canvas;
    private readonly Backdrop _backdrop;
    private readonly HudFonts _fonts = new();
    private readonly HotkeyCapturer _capturer = new(); // the game's, as its controls tab uses it
    private readonly Dictionary<OptionRow, double> _widest = []; // a slider's widest value, measured once per font size
    private readonly HitMap[] _maps = [new(), new(), new(), new()]; // the canvas holds at most three: shown, waiting, painting
    private HitMap _map = new(); // the one being composed
    private object? _lastShown; // the map of the picture on screen when the highlight last followed it
    private OptionPage[] _pages = [];
    private string _page = "", _query = "";
    private bool _reviewing; // the footer's change count clicked: the list shows the staged rows

    // A search or the staged changes in place of the page
    private bool Filtering => _query.Length > 0 || _reviewing;
    private double _scroll;
    private bool _dirty = true, _searching, _advanced; // _advanced: HudSettings.ShowAdvanced as the pages were built
    private int _epoch;
    private (int Width, int Height) _frame;
    private double _bottom; // where the screen ended when it was last composed
    private OptionRow? _hover;
    private Hit? _drag;
    private (IGameSettingsHandler Handler, Action<string> Tab)? _embedded;
    private OptionRow? _binding;

    public OptionsScreen(ICoreClientAPI capi, HudSettings settings, KometActions actions) : base(capi)
    {
        _canvas = new HudCanvas(capi, background: true); // Cairo paints on a worker, the main thread only records
        _backdrop = new Backdrop(capi);
        _settings = settings;
        _styles = new StyleLibrary(capi);
        _scene = new StyleScene(capi);
        _white = new LoadedTexture(capi);
        _globe = new WorldGlobe(capi);
        _atlas = new WorldAtlas(capi);
        _prefetch = capi.Event?.RegisterGameTickListener(Prefetch, PrefetchMs, PrefetchMs) ?? 0;
        Restyle(); // the HUD takes the style before the screen first opens
        capi.Event?.EnqueueMainThreadTask(_backdrop.Prepare, "komet-backdrop"); // where the GL context is
        _komet = new KometPages(settings, actions) { Styles = _styles, Api = capi };
        _komet.StylesReloaded += Reloaded;
        ShaderOverview.Refreshed += Refreshed;
        KometStyles.Changed += StylesChanged; // static: a named handler, so Dispose can take it off again
        settings.Changed += () =>
        {
            _dirty = true;
            StyleAsked();
            if (settings.ShowAdvanced == _advanced) return;
            _advanced = settings.ShowAdvanced; // other knobs show or go: the pages again, on the page that is open
            if (_pages.Length > 0) Rebuild(_page);
        };
        if (!NotNull(capi.ChatCommands)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").WithDescription(KometText.Translate("cmd-hud"))
            .HandleWith(_ => Open(KometPages.Overview)
                ? TextCommandResult.Success()
                : TextCommandResult.Error(KometText.Translate("cmd-hud-open-failed")));
    }

    public override string? ToggleKeyCombinationCode => null;
    public override double DrawOrder => 0.95; // over the escape menu (0.89)
    public override double InputOrder => -1; // before it (0), so typing into the search reaches the search
    // every key is the screen's while it is open: arrows, Tab, Q/E, R, Enter and typing into the search, before any hotkey
    public override bool CaptureAllInputs() => true;

    // Raised per composition, which the HUD keeps out of steady frames
    public event Action? Composing;

    // page may name a row after '#' (the benchmark's "shaders-overview#SheyderMod"): it opens with that row chosen
    public bool Open(string? page = null)
    {
        if (!Assert(page is null || page.Length > 0)) return false;
        string? chosen = null;
        if (page?.IndexOf('#', StringComparison.Ordinal) is > 0 and var at) (page, chosen) = (page[..at], page[(at + 1)..]);
        if (IsOpened() && _embedded is not null) Leave();
        Restyle();
        Rebuild(page ?? KometPages.Overview);
        if (chosen is not null && Array.Find(_pages, p => p.Id == _page) is { } shown)
            for (var i = 0; i < Math.Min(shown.Count, OptionPage.MaxRows); i++)
                if (shown[i].Label == chosen) _cursor = shown[i];
        return IsOpened() || TryOpen();
    }

    public void Embed(IGameSettingsHandler handler, Action<string> tab)
    {
        if (!NotNull(handler) || !NotNull(tab)) return;
        _embedded = (handler, tab);
        if (!IsOpened()) Rebuild("vs-general");
        Restyle();
        _dirty = true;
        _ = IsOpened() || TryOpen();
    }

    // Game left its settings (menu closed or own tab shown): the screen goes
    public void Leave()
    {
        if (_embedded is not { } embedded || !NotNull(embedded.Handler)) return;
        _embedded = null;
        _ = !IsOpened() || TryClose();
    }

    // Only with nothing to step back from: GuiManager treats true as "removed", closing a still-open screen on the
    // second ask
    public override bool OnEscapePressed()
    {
        if (_binding is not null) Unbound();
        return !Back() && Close();
    }

    // The footer's change count: the staged rows listed, each where it can be changed again or undone (Del)
    private void Review()
    {
        if (_staged.Count == 0) return;
        (_reviewing, _query, _searching, _scroll, _cursor, _dirty) = (!_reviewing, "", false, 0, null, true);
    }

    private bool Back()
    {
        var back = _focus is not null || _query.Length > 0 || _searching || _reviewing;
        if (_focus is not null) _focus = null;
        else (_query, _searching, _reviewing) = ("", false, false);
        _dirty |= back;
        return back;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        _fade = 0; // the blur and the dim come in again
        (_decodeAt, _titleKey) = (0, ""); // the title decodes as the screen opens
    }

    public override void OnGuiClosed()
    {
        Unbound();
        (_drag, _hover, _searching, _focus) = (null, null, false, null);
        _motions.Clear();
        (_glow, _target) = (default, default);
        _staged.Clear();
        _backdrop.Release();
        _scene.Release();
        (_dolly, _clock, _slide) = (-1, 0, -1); // the clock starts over: nothing may wait for a time of the last opening
        _press.At = -1;
        Restyle(); // a change of style still moving ends here
        _canvas.Drop(); // the next opening waits for its own picture: no old page, scroll or click map
        _dirty = true;
        if (_embedded is not { } embedded || !NotNull(embedded.Handler)) return;
        _embedded = null;
        _ = embedded.Handler.OnBackPressed(); // back to the escape menu, as the game's Back button
    }

    private void Rebuild(string page)
    {
        _pages = Pages();
        _advanced = _komet?.Advanced ?? false;
        if (!Assert(_pages.Length > 0)) return;
        (_page, _scroll, _query, _dirty, _reviewing) = ((Array.Find(_pages, p => p.Id == page) ?? _pages[0]).Id, 0, "", true, false);
        _staged.Clear();
        _widest.Clear();
        _motions.Clear(); // the rows are made anew
        (_focus, _cursor) = (null, null);
    }

    private OptionPage[] Pages()
    {
        List<OptionPage> pages = [.. EngineOptions.Pages(capi, _embedded is null ? null : OpenTab), .. _komet.Build()];
        for (var i = 0; i < Math.Min(KometOptions.Count, KometOptions.MaxPages); i++)
            pages.Add(Features.Placed(KometOptions.At(i)));
        // the in-game docs, last: the Help section, then Modding with its Templates page
        pages.AddRange(KometDocs.With(KometDocs.Pages(capi), _komet.TemplatesPage(KometDocs.ModdingSection)));
        return Assert(pages.Count is > 0 and <= MaxPages) ? [.. pages] : [.. pages.Take(MaxPages)];
    }

    private void OpenTab(string handler)
    {
        if (_embedded is not { } embedded || !NotNull(handler)) return;
        _embedded = null;
        _staged.Clear();
        _ = TryClose();
        embedded.Tab(handler);
    }

    private double Value(OptionRow row) => _staged.TryGetValue(row, out var value) ? value : row.Get();

    private void Stage(OptionRow row, double value)
    {
        if (!Finite(value)) return;
        if (row.Live)
        {
            SetGuarded(row, value);
            _ = _staged.Remove(row);
            _dirty = true;
            return;
        }

        if (Math.Abs(value - row.Get()) < 1e-9) _ = _staged.Remove(row);
        else if (Assert(_staged.Count < MaxStaged) || _staged.ContainsKey(row)) _staged[row] = value;
        _dirty = true;
    }

    // A setter may be another mod's (KometOptions pages): one that throws must not take the GUI handler and the client
    // with it, nor keep the other staged rows from applying
    private void SetGuarded(OptionRow row, double value)
    {
        try
        {
            row.Set(value);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (_failed.Add(row)) capi.Logger?.Warning("Komet options: setting '{0}' failed: {1}", row.Label, e);
        }
    }

    private void Apply()
    {
        var changes = _staged.ToArray();
        _staged.Clear();
        EngineOptions.Batch(() =>
        {
            foreach (var (row, value) in changes.Bounded(MaxStaged)) SetGuarded(row, value);
        });
        _dirty = true;
        if (changes.Length > 0) KometOptions.RaiseApplied();
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!NotNull(args) || !IsOpened()) return;
        args.Handled = true; // nothing under the screen takes a click
        if (_binding is not null)
        {
            _ = _capturer.OnMouseDown(args); // a mouse button binds as a key does
            return;
        }

        var (x, y) = (args.X - _canvas.X, args.Y - _canvas.Y);
        (_searching, _flash) = (false, Flash);
        var hit = At(x, y, false);
        _focus = null; // a slider's click takes it again
        if (hit is null || !Assert(hit.W > 0)) return;
        hit.Click((x - hit.X) / hit.W, args.Button == EnumMouseButton.Right);
        Press(hit.X, hit.Y, hit.W, hit.H);
        _drag = hit.Drag ? hit : null;
        _dirty = true;
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (!NotNull(args) || !IsOpened()) return;
        var (x, y) = (args.X - _canvas.X, args.Y - _canvas.Y);
        _mouse = (x, y);
        Retarget();
        if (_drag is { } drag && Assert(drag.W > 0))
        {
            drag.Click(Math.Clamp((x - drag.X) / drag.W, 0, 1), false);
            (args.Handled, _dirty) = (true, true);
            return;
        }

        var hover = At(x, y, true)?.Option;
        if (ReferenceEquals(hover, _hover)) return;
        (_hover, _dirty) = (hover, true);
        if (hover is not null) _cursor = hover; // the light follows the mouse as it does the keys
    }

    private Hit? At(double x, double y, bool row)
    {
        if (!Finite(x) || !Finite(y)) return null;
        var map = OnScreen;
        if (Under(map.Hits, x, y, row) is { } hit) return hit;
        var (vx, vy, vw, vh) = map.View;
        return x >= vx && x < vx + vw && y >= vy && y < vy + vh ? Under(map.Rows, x, y, row) : null;
    }

    // Per mouse move, so a loop, not a capturing Find
    private static Hit? Under(List<Hit> hits, double x, double y, bool? row)
    {
        if (!Assert(hits.Count <= MaxHits)) return null;
        for (var i = 0; i < Math.Min(hits.Count, MaxHits); i++)
            if ((row is null || hits[i].Row == row) && hits[i].Covers(x, y))
                return hits[i];
        return null;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        if (NotNull(args) && IsOpened()) args.Handled = _drag is not null || (_binding is not null && _capturer.OnMouseUp(args, Bound));
        _drag = null;
    }

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!NotNull(args) || !IsOpened() || !Finite(args.deltaPrecise)) return;
        args.SetHandled();
        var (sx, sy, sw, sh) = OnScreen.SideView;
        var (mx, my) = _mouse;
        if (mx >= sx && mx < sx + sw && my >= sy && my < sy + sh) _sideScroll -= args.deltaPrecise * _fonts.BadgeRow * 3;
        else _scroll -= args.deltaPrecise * _fonts.BadgeRow * 3;
        _dirty = true;
    }

    public override void OnKeyPress(KeyEvent args)
    {
        if (NotNull(args) && _binding is not null) args.Handled = true;
        if (!NotNull(args) || !_searching || !Assert(_query.Length <= MaxQuery)) return;
        if (!char.IsControl(args.KeyChar) && _query.Length < MaxQuery) (_query, _scroll, _reviewing) = (_query + args.KeyChar, 0, false);
        (args.Handled, _dirty) = (true, true);
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (!NotNull(args)) return;
        if (_binding is not null)
        {
            if (_capturer.OnKeyDown(args) && !_capturer.IsCapturing()) Unbound(); // Escape
            (args.Handled, _dirty) = (true, true);
            return;
        }

        if (args.KeyCode == (int)GlKeys.Escape && Back())
        {
            args.Handled = true;
            return;
        }

        if (TabKeys(args) || Keyed(args))
        {
            (args.Handled, _dirty) = (true, true);
            return;
        }

        if (!_searching || !Assert(_query.Length <= MaxQuery)) return;
        if (args.KeyCode == (int)GlKeys.BackSpace && _query.Length > 0) _query = _query[..^1];
        else if (args.KeyCode is (int)GlKeys.Enter or (int)GlKeys.KeypadEnter) _searching = false;
        else if (args.KeyCode != (int)GlKeys.Escape) return; // a plain key: its character comes as a key press
        (args.Handled, _dirty) = (true, true);
    }

    public override void OnKeyUp(KeyEvent args)
    {
        if (!NotNull(args) || _binding is null) return;
        _ = _capturer.OnKeyUp(args, Bound);
        args.Handled = true;
    }

    // Next key, combination or mouse button binds the row; Escape keeps it
    private void Binding(OptionRow row)
    {
        if (_binding is not null || !_capturer.BeginCapture()) return;
        (_binding, _searching) = (row, false);
    }

    // Sometimes a few frames after key up
    private void Bound()
    {
        if (_binding is { } row && !_capturer.WasCancelled && NotNull(_capturer.CapturedKeyComb))
            EngineOptions.Bind(row.Code, _capturer.CapturedKeyComb);
        (_binding, _dirty) = (null, true);
    }

    private void Unbound()
    {
        if (_capturer.IsCapturing()) _capturer.EndCapture(true); // the game's hotkeys work again
        (_binding, _dirty) = (null, true);
    }

    public override void Dispose()
    {
        base.Dispose();
        ShaderOverview.Refreshed -= Refreshed;
        KometStyles.Changed -= StylesChanged;
        Komet.Core.Listeners.Unregister(capi.Event, _prefetch); // still reading when the world closes before the map came
        _prefetch = 0;
        Unbound();
        _canvas.Dispose();
        _titleCanvas?.Dispose();
        _backdrop?.Dispose();
        _scene?.Dispose();
        _styles?.Dispose();
        _white?.Dispose();
        _globe?.Dispose();
        _atlas?.Dispose();
    }

    // The shader packs changed on a click: the pages anew, on the page that is open
    private void Refreshed()
    {
        if (_pages.Length > 0) Rebuild(_page);
    }

    private void StylesChanged(object? sender, EventArgs e) => Reloaded();

    // The styles found again (a designer's edit, a mod's Register): the one in use is taken anew
    private void Reloaded()
    {
        _styles.Reload();
        _style = StyleSheet.Fallback();
        Restyle();
        if (_pages.Length > 0) Rebuild(_page); // the style choice lists them
    }

    // The click map that goes with one picture: tabs, page list, keys and search (Hits), the list's rows (counted only
    // inside View) and the page list's view. Clicks read the map of the picture on screen, which lags the newest
    // composition.
    private sealed class HitMap
    {
        public readonly List<Hit> Hits = [], Rows = [];
        public readonly List<Plate> Backs = []; // grounds drawn under the picture (the panels)
        public (double X, double Y, double W, double H) View, SideView;
        public StyleSheet? Style; // the style the picture was made in
        public (double X, double Y, double W, double H) Underline; // the open tab's
        public readonly Dictionary<string, (double X, double Y, double W, double H)> Caps = []; // the keys' caps by label
        public readonly List<(double Y, double H)> Bands = []; // the list's items as they show, top to bottom (they slide in)
        public string Page = ""; // the page the picture shows
        public (double X, double Y, double W, double H, string Text, double Tracking) Title = (0, 0, 0, 0, "", 0); // the page's
    }

    // A ground under the picture; Ticks: corner marks of that length
    private readonly record struct Plate(double X, double Y, double W, double H, Rgba Color, double Ticks = 0, Rgba Tick = default,
        bool Panel = false);

    private HitMap OnScreen => _canvas.Shown as HitMap ?? _map;

    // A map no picture of the canvas holds, emptied for the next composition
    private HitMap FreeMap()
    {
        var free = Array.Find(_maps, map => !_canvas.Holds(map)) ?? _maps[^1];
        free.Hits.Clear();
        free.Rows.Clear();
        free.Backs.Clear();
        free.Underline = default;
        free.Caps.Clear();
        free.Bands.Clear();
        free.Title = (0, 0, 0, 0, "", 0);
        _ = Assert(!ReferenceEquals(free, _canvas.Shown));
        return free;
    }

    // Click gets x fraction and right-button; Row boxes only mark hover. Glow: highlight brightness; Group: its glide
    // column
    private sealed record Hit(double X, double Y, double W, double H, Action<double, bool> Click, bool Drag = false,
        OptionRow? Option = null, bool Row = false, double Glow = 0, int Group = 0)
    {
        public bool Covers(double x, double y) => Finite(x) && Finite(y) && x >= X && x < X + W && y >= Y && y < Y + H;
    }
}
