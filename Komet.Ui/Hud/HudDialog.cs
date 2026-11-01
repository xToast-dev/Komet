namespace Komet.Hud;

// Shared by window and debug window: one canvas faded by HudMotion with hit boxes, sized to the game window and
// recomposed only on change, a scroll bar and the wheel, snap-grid drag, close after the fade
internal abstract class HudDialog : GuiDialog
{
    private const double Margin = 24;

    private readonly HudSettings _settings;
    private readonly SnapGrid _snap;
    private readonly float _depth;
    private readonly Action _finish;
    private (double X, double Y) _grab;
    private bool _dirty = true, _urgent = true;
    private long _composed;
    private int _epoch;

    // Clicks use the hit map painted with the shown picture (Canvas.Shown lags a background canvas): shown, waiting,
    // painted, free.
    private readonly List<HudUi.Hit>[] _hitMaps = [[], [], [], []];

    protected HudDialog(ICoreClientAPI capi, HudSettings settings, HudUiFonts fonts, SnapGrid snap, float depth) : base(capi)
    {
        Canvas = new HudCanvas(capi, background: true);
        Motion = new HudMotion(capi);
        (_settings, Fonts, _snap, _depth) = (settings, fonts, snap, depth);
        _finish = Finish;
    }

    protected HudCanvas Canvas { get; }
    protected HudMotion Motion { get; }
    protected HudUiFonts Fonts { get; } // shared with the overlay
    protected (double W, double H) Size { get; private set; } // of the page last composed
    protected List<HudUi.Hit> Hits { get; private set; } = [];
    protected bool Moving { get; private set; }
    private List<HudUi.Hit> ShownHits => Canvas.Shown as List<HudUi.Hit> ?? Hits;

    public override string? ToggleKeyCombinationCode => null;
    public override bool PrefersUngrabbedMouse => true;

    public event Action? Composing;

    private void NewHits()
    {
        foreach (var map in _hitMaps.Bounded(4))
            if (!Canvas.Holds(map))
            {
                map.Clear();
                Hits = map;
                return;
            }
    }

    // Urgent: at the next frame (input), otherwise at most every refresh interval (new numbers)
    public void Invalidate(bool urgent = false) => (_dirty, _urgent) = (true, _urgent || urgent);

    // The dialog's size this frame: max at font scale 1, scaled, within a margin of the game window
    protected (double W, double H) Fitted(double maxW, double maxH)
    {
        Fonts.Update(_settings.FontScale);
        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        return (Math.Min(w - 2 * scaled(Margin), scaled(maxW * Fonts.Scale)), Math.Min(h - 2 * scaled(Margin), scaled(maxH * Fonts.Scale)));
    }

    // True when a new page is due (invalidated and the interval passed, new fonts, new size): the canvas is blanked at size
    // and the caller draws the page and ends it with Canvas.End(Hits)
    protected bool Recompose((double W, double H) size, double refreshMs)
    {
        var due = _urgent || Environment.TickCount64 - _composed >= refreshMs;
        if (!(_dirty && due) && _epoch == Fonts.Epoch && size == Size) return false;
        if (!Assert(size.W > 0) || !Assert(size.H > 0)) return false;
        (_dirty, _urgent, _epoch, Size, _composed) = (false, false, Fonts.Epoch, size, Environment.TickCount64);
        Composing?.Invoke();
        NewHits();
        return Canvas.Blank(size.W, size.H);
    }

    // Track and thumb for a pane of height at top showing content from scroll on; nothing when the content fits
    protected void ScrollBar(double x, double width, double top, double height, double content, double scroll)
    {
        if (content <= height || !Assert(height > 0) || !Assert(width > 0)) return;
        var thumb = Math.Min(height, Math.Max(scaled(24), height * height / content));
        var at = top + (height - thumb) * Math.Clamp(scroll / (content - height), 0, 1);
        Canvas.Fill(x, top, width, height, Rgba.White(0.05), width / 2);
        Canvas.Fill(x, at, width, thumb, Rgba.White(0.28), width / 2);
    }

    // The wheel's notches, positive downwards
    protected abstract void Scrolled(double notches);

    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        if (!NotNull(args) || !Contains(capi.Input.MouseX, capi.Input.MouseY) || !Finite(args.deltaPrecise)) return;
        Scrolled(-args.deltaPrecise);
        Invalidate(true);
        args.SetHandled();
    }

    protected void Show(bool close)
    {
        if (!IsOpened()) _ = TryOpen();
        else if (Motion.Closing) Motion.Open();
        else if (close) _ = TryClose();
    }

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Motion.Open();
    }

    public override bool TryClose()
    {
        if (!IsOpened()) return false;
        Motion.Close();
        return true;
    }

    // Returns false: GuiManager treats true as "removed" and asks the same slot again, which looped forever.
    public override bool OnEscapePressed()
    {
        if (IsOpened()) Motion.Close();
        return false;
    }

    private void Finish()
    {
        if (Motion.Closing && IsOpened()) _ = base.TryClose();
    }

    public bool Contains(double px, double py) => IsOpened() && Canvas.Ready && Canvas.Contains(px, py);

    // At the saved place, NaN: centred. A place saved at a larger resolution would put the title bar, the only grip,
    // off-screen.
    protected void Present(double x, double y)
    {
        int w = capi.Render.FrameWidth, h = capi.Render.FrameHeight;
        if (!Assert(w > 0) || !Assert(h > 0)) return;
        (x, y) = (double.IsFinite(x) ? x : (w - Size.W) / 2, double.IsFinite(y) ? y : (h - Size.H) / 2);
        (x, y) = (Math.Clamp(x, 0, Math.Max(0, w - Canvas.Width)), Math.Clamp(y, 0, Math.Max(0, h - Canvas.Height)));
        if (Moving) _snap.Draw();
        Motion.Draw(Canvas, Math.Round(x), Math.Round(y), _depth);
    }

    protected void Hover()
    {
        Motion.Hover(Canvas, ShownHits, _depth);
        if (Motion.Closed) capi.Event.EnqueueMainThreadTask(_finish, "komet-hud-close");
    }

    protected abstract bool Grabs(double x, double y, EnumMouseButton button);

    protected abstract void Moved(double x, double y);

    public override void OnMouseDown(MouseEvent args)
    {
        if (!NotNull(args) || !Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (Motion.Closing) return;
        var (x, y) = (args.X - Canvas.X, args.Y - Canvas.Y);
        if (HudUi.HitAt(ShownHits, x, y) is { } hit)
        {
            Motion.Press(hit);
            hit.Click();
            Invalidate(true);
        }
        else if (Grabs(x, y, args.Button)) (_grab, Moving) = ((x, y), true);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        if (!Moving || !NotNull(args)) return;
        Moved(HudMotion.Snap(args.X - _grab.X, Canvas.Width, capi.Render.FrameWidth),
            HudMotion.Snap(args.Y - _grab.Y, Canvas.Height, capi.Render.FrameHeight));
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args) => Drop();

    public override void OnGuiClosed() => Drop();

    // The grid's full-screen texture is kept only while dragging and a few seconds after
    private void Drop()
    {
        if (!Moving) return;
        _snap.Release();
        Moving = false;
    }

    public override void Dispose()
    {
        base.Dispose();
        Canvas.Dispose();
        Motion.Dispose();
    }
}

// The grid a dragged dialog snaps to: a full-screen texture (2560x1440: 14.7 MB), built when a drag starts and freed
// KeepMs after it ends, so quick repeated drags reuse it instead of repainting it on the main thread each time;
// rebuilt within a drag only on a frame size or GUI scale change
internal sealed class SnapGrid(ICoreClientAPI capi, Action built) : IDisposable
{
    private const int KeepMs = 5000;

    private HudCanvas? _canvas;
    private (int Width, int Height, int Scale) _key;
    private long _release;

    public void Dispose()
    {
        Cancel();
        _canvas?.Dispose();
        _canvas = null;
    }

    // A drag ended: the texture goes unless another drag starts within KeepMs
    public void Release()
    {
        if (_canvas is null || !NotNull(capi.Event)) return;
        Cancel();
        _release = capi.Event.RegisterCallback(_ =>
        {
            _release = 0;
            Dispose();
        }, KeepMs, permittedWhilePaused: true);
    }

    public void Draw()
    {
        var key = (capi.Render.FrameWidth, capi.Render.FrameHeight,
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (!Assert(key.FrameWidth > 0 && key.FrameHeight > 0)) return;
        Cancel();
        if (_canvas is null || key != _key)
        {
            _canvas?.Dispose();
            _canvas = HudCanvas.Grid(capi, scaled(HudSettings.SnapStep));
            _key = key;
            built();
        }

        _canvas.Draw(0, 0);
    }

    private void Cancel()
    {
        if (_release == 0 || !NotNull(capi.Event)) return;
        capi.Event.UnregisterCallback(_release);
        _release = 0;
    }
}
