using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Options;

// Komet's options screen, laid out like Sodium's: a search bar, a sidebar of sections (Vintage Story, Komet, then every mod that
// registered pages with KometOptions), the chosen page in a scrolling column, the hovered option's description beside it, and
// Undo / Apply / Done at the bottom right. Changes are staged until Apply or Done (the game's settings in one batch: one shader
// reload, one framebuffer rebuild); Escape drops them. Buttons (a benchmark, a reset) and keys act at once. Stands in for the
// game's settings (GraphicsMenu, Embed) or opens on its own (.komet).
internal sealed partial class OptionsScreen : GuiDialog
{
    private const int MaxPages = 48, MaxStaged = 256, MaxQuery = 64, MaxHits = 4096;

    private readonly KometPages _komet;
    private readonly Dictionary<OptionRow, double> _staged = [];
    private readonly HashSet<OptionRow> _failed = []; // rows whose failure was logged
    private readonly HudCanvas _canvas;
    private readonly Backdrop _backdrop;
    private readonly HudFonts _fonts = new();
    private readonly HotkeyCapturer _capturer = new(); // the game's, as its controls tab uses it
    private readonly Dictionary<OptionRow, double> _widest = []; // a slider's widest value, measured once per font size
    private readonly List<Hit> _hits = []; // search, sidebar, buttons
    private readonly List<Hit> _rows = []; // the list's, which count only inside its view
    private (double X, double Y, double W, double H) _view;
    private OptionPage[] _pages = [];
    private string _page = "", _query = "";
    private double _scroll;
    private bool _dirty = true, _searching, _advanced; // _advanced: HudSettings.ShowAdvanced as the pages were built
    private int _epoch;
    private (int Width, int Height) _frame;
    private double _bottom; // where the screen ended when it was last composed
    private OptionRow? _hover;
    private Hit? _drag;
    private (IGameSettingsHandler Handler, Action<string> Tab)? _embedded;
    private OptionRow? _binding; // the key row waiting for its key

    public OptionsScreen(ICoreClientAPI capi, HudSettings settings, Action dump, Action<float> bench, Action verify, Action window,
        Action debug) : base(capi)
    {
        _canvas = new HudCanvas(capi);
        _backdrop = new Backdrop(capi);
        capi.Event?.EnqueueMainThreadTask(_backdrop.Prepare, "komet-backdrop"); // where the GL context is
        _komet = new KometPages(settings, dump, bench, verify, window, debug);
        if (NotNull(settings))
            settings.Changed += () =>
            {
                _dirty = true;
                if (settings.ShowAdvanced == _advanced) return;
                _advanced = settings.ShowAdvanced; // other knobs show or go: the pages again, on the page that is open
                _ = Assert(_komet is not null);
                if (_pages.Length > 0) Fresh(_page);
            };
        if (!NotNull(capi.ChatCommands)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").WithDescription(HudText.Translate("cmd-hud"))
            .HandleWith(_ => Open(KometPages.Hud)
                ? TextCommandResult.Success()
                : TextCommandResult.Error(HudText.Translate("cmd-hud-open-failed")));
    }

    public override string? ToggleKeyCombinationCode => null;
    public override double DrawOrder => 0.95; // over the escape menu (0.89)
    public override double InputOrder => -1; // before it (0), so typing into the search reaches the search
    public override bool CaptureAllInputs() => _binding is not null || (_searching && Assert(_query.Length <= MaxQuery));

    // Raised by every composition (a Cairo pass and a texture upload), which the HUD keeps out of its steady frames
    public event Action? Composing;

    public bool Contains(double x, double y) => IsOpened() && Finite(x) && Finite(y); // the screen covers everything

    // On its own, at a page (the Komet section's first when null)
    public bool Open(string? page = null)
    {
        if (!Assert(page is null || page.Length > 0) || !NotNull(_komet)) return false;
        if (IsOpened() && _embedded is not null) Leave();
        Fresh(page ?? KometPages.Hud);
        return IsOpened() || TryOpen();
    }

    // In place of the game's settings; tab opens one of the game's own screens by handler name (original graphics tab, macro editor)
    public void Embed(IGameSettingsHandler handler, Action<string> tab)
    {
        if (!NotNull(handler) || !NotNull(tab)) return;
        _embedded = (handler, tab);
        if (!IsOpened()) Fresh("vs-general");
        _dirty = true;
        _ = IsOpened() || TryOpen();
    }

    // The game left its settings (the escape menu closed, or one of its own tabs is shown): the screen goes, without going back
    public void Leave()
    {
        if (_embedded is not { } embedded || !NotNull(embedded.Handler)) return;
        _embedded = null;
        _ = !IsOpened() || TryClose();
    }

    public override bool OnEscapePressed()
    {
        if (_binding is not null) Unbound();
        else if (_picker is not null) (_picker, _dirty) = (null, true);
        else if (_query.Length > 0 || _searching) (_query, _searching, _dirty) = ("", false, true);
        else return Close();
        return true;
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        _fade = 0; // the blur and the dim come in again
        _ = Assert(_binding is null); // a capture ends with the screen
    }

    public override void OnGuiClosed()
    {
        Unbound();
        (_drag, _hover, _searching, _picker) = (null, null, false, null);
        (_glow, _target) = (default, default);
        _staged.Clear();
        if (_embedded is not { } embedded || !NotNull(embedded.Handler)) return;
        _embedded = null;
        _ = embedded.Handler.OnBackPressed(); // back to the escape menu, as the game's Back button
    }

    // The pages as they are now, the section of the one asked for, the list scrolled to it
    private void Fresh(string page)
    {
        if (!NotNull(page)) return;
        _pages = Pages();
        _advanced = _komet?.Advanced ?? false;
        if (!Assert(_pages.Length > 0)) return;
        (_page, _scroll, _query, _dirty) = ((Array.Find(_pages, p => p.Id == page) ?? _pages[0]).Id, 0, "", true);
        _staged.Clear();
        _widest.Clear();
    }

    private OptionPage[] Pages()
    {
        if (!NotNull(_komet)) return [];
        List<OptionPage> pages = [.. EngineOptions.Pages(capi, _embedded is null ? null : OpenTab), .. _komet.Build()];
        for (var i = 0; i < Math.Min(KometOptions.Count, KometOptions.MaxPages); i++)
            pages.Add(Features.Placed(KometOptions.At(i)));
        return Assert(pages.Count is > 0 and <= MaxPages) ? [.. pages] : [.. pages.Take(MaxPages)];
    }

    // One of the game's own tabs, over this screen's place in the escape menu
    private void OpenTab(string handler)
    {
        if (_embedded is not { } embedded || !NotNull(handler)) return;
        _embedded = null;
        _staged.Clear();
        _ = TryClose();
        embedded.Tab(handler);
    }

    private double Value(OptionRow row) => NotNull(row) && _staged.TryGetValue(row, out var value) ? value : row.Get();

    private void Stage(OptionRow row, double value)
    {
        if (!NotNull(row) || !Finite(value)) return;
        if (Math.Abs(value - row.Get()) < 1e-9) _ = _staged.Remove(row);
        else if (Assert(_staged.Count < MaxStaged) || _staged.ContainsKey(row)) _staged[row] = value;
        _dirty = true;
    }

    // Every staged change, in the order it was made; the game's reloads once at the end
    private void Apply()
    {
        var changes = _staged.ToArray();
        _staged.Clear();
        if (!Assert(changes.Length <= MaxStaged)) return;
        EngineOptions.Batch(() =>
        {
            foreach (var (row, value) in changes.Bounded(MaxStaged)) row.Set(value);
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
        if (_picker is not null && hit?.Group != PickerGroup)
        {
            (_picker, _dirty) = (null, true); // a click beside the open window only closes it
            return;
        }

        if (hit is null || !Assert(hit.W > 0)) return;
        hit.Click((x - hit.X) / hit.W, args.Button == EnumMouseButton.Right);
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
    }

    // The box under the point: the screen's own first, then the list's inside its view; row: the boxes that mark a hovered option
    private Hit? At(double x, double y, bool row)
    {
        if (!Finite(x) || !Finite(y)) return null;
        if (!row && PickAt(x, y) is { } name) return name;
        if (Under(_hits, x, y, row) is { } hit) return hit;
        var (vx, vy, vw, vh) = _view;
        return x >= vx && x < vx + vw && y >= vy && y < vy + vh ? Under(_rows, x, y, row) : null;
    }

    // The first box at the point, of either kind with row null; on every mouse move, so a loop rather than a capturing Find
    private static Hit? Under(List<Hit> hits, double x, double y, bool? row)
    {
        if (!NotNull(hits) || !Assert(hits.Count <= MaxHits)) return null;
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
        if (ScrollNames(args.deltaPrecise)) return; // the names move under their scissor: nothing to compose
        _scroll -= args.deltaPrecise * _fonts.BadgeRow * 3;
        _dirty = true;
    }

    // Typing goes to the search while it has the focus
    public override void OnKeyPress(KeyEvent args)
    {
        if (NotNull(args) && _binding is not null) args.Handled = true;
        if (!NotNull(args) || !_searching || !Assert(_query.Length <= MaxQuery)) return;
        if (!char.IsControl(args.KeyChar) && _query.Length < MaxQuery) (_query, _scroll) = (_query + args.KeyChar, 0);
        (args.Handled, _dirty) = (true, true);
    }

    public override void OnKeyDown(KeyEvent args)
    {
        if (NotNull(args) && _binding is not null)
        {
            if (_capturer.OnKeyDown(args) && !_capturer.IsCapturing()) Unbound(); // Escape
            (args.Handled, _dirty) = (true, true);
            return;
        }

        if (!NotNull(args) || !_searching || !Assert(_query.Length <= MaxQuery)) return;
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

    // A key row waits for its key; the next key, key combination or mouse button binds it, Escape leaves it as it was
    private void Binding(OptionRow row)
    {
        if (!NotNull(row) || _binding is not null || !_capturer.BeginCapture()) return;
        (_binding, _picker, _searching) = (row, null, false);
    }

    // The capture ended with a key (the capturer calls it, sometimes a few frames after the key went up)
    private void Bound()
    {
        if (_binding is { } row && !_capturer.WasCancelled && NotNull(_capturer.CapturedKeyComb))
            EngineOptions.Bind(row.Code, _capturer.CapturedKeyComb);
        (_binding, _dirty) = (null, true);
        _ = Assert(!_capturer.IsCapturing());
    }

    private void Unbound()
    {
        if (_capturer.IsCapturing()) _capturer.EndCapture(true); // the game's hotkeys work again
        (_binding, _dirty) = (null, true);
        _ = Assert(!_capturer.IsCapturing());
    }

    public override void Dispose()
    {
        base.Dispose();
        Unbound();
        if (NotNull(_canvas)) _canvas.Dispose();
        _names?.Dispose();
        _backdrop?.Dispose();
    }

    // Click gets the x fraction and whether it was the right button; a Row box only marks its option as hovered. Glow: the
    // highlight's brightness while hovered (0: none); Group: its column, within which the highlight glides from box to box
    private sealed record Hit(double X, double Y, double W, double H, Action<double, bool> Click, bool Drag = false,
        OptionRow? Option = null, bool Row = false, double Glow = 0, int Group = 0)
    {
        public bool Covers(double x, double y) => Finite(x) && Finite(y) && x >= X && x < X + W && y >= Y && y < Y + H;
    }
}
