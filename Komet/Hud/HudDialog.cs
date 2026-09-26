namespace Komet.Hud;

// A window drawn like a HUD panel; GuiDialog only hosts it (free cursor, Escape, mouse events). Build() adds rows and returns the
// width of the control column; the base measures the labels, stacks the rows top to bottom and draws them. A click goes to the hit
// box under the cursor, a drag on empty space moves the window with grid snap. Lang keys are "<prefix>-<key>".
internal abstract class HudDialog : GuiDialog
{
    private const int MaxRows = 48; // past the bound a page is not drawn; the HUD page holds 33
    private const double RowGap = 4;

    private readonly string _prefix;
    private readonly SnapGrid _snap;
    private HitBox? _drag;
    private int _epoch; // HudFonts.Epoch of the last composition
    private double _grabX, _grabY;
    private double _headerRow, _titleRow, _labelW;
    private bool _moving; // the window follows the cursor
    private (double X, double Y)? _pinned;

    protected HudDialog(ICoreClientAPI capi, HudSettings settings, HudFonts fonts, SnapGrid snap, string prefix) :
        base(capi)
    {
        (Settings, Fonts, _snap, _prefix) = (settings, fonts, snap, prefix);
        Canvas = new HudCanvas(capi);
        if (NotNull(settings) && Assert(prefix.Length > 0)) settings.Changed += () => Dirty = true;
    }

    protected HudSettings Settings { get; }
    protected HudFonts Fonts { get; }
    protected HudCanvas Canvas { get; }
    protected List<HitBox> Hits { get; } = [];
    protected List<(double Height, Action<double> Draw)> Rows { get; } = [];
    protected double Width { get; private set; }

    // Right of the widest label; set after Build(), so only row lambdas read it
    protected double ControlX { get; private set; }

    protected double ControlW { get; private set; }
    protected double Pad { get; private set; }
    protected double Gap { get; private set; }
    protected double RowH { get; private set; }
    protected bool Dirty { get; set; } = true;

    public override string? ToggleKeyCombinationCode => null;

    // Raised by every composition (a Cairo pass over every row and a texture upload), which the HUD keeps out of its steady frames
    public event Action? Composing;

    public override void OnGuiClosed()
    {
        Release();
    }

    public bool Contains(double px, double py)
    {
        return IsOpened() && Canvas.Contains(px, py);
    }

    protected string Translate(string key, params object[] args)
    {
        return HudText.Translate(_prefix + "-" + key, args);
    }

    protected abstract double Build();

    // Composed again only when something it shows changed (Dirty, set by a setting) or the fonts were rebuilt (the font or the GUI
    // scale, which changes without an event), not on every open: the canvas and its texture outlive a close
    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !Assert(capi.Render.FrameWidth > 0) || !Assert(capi.Render.FrameHeight > 0)) return;
        _ = Fonts.Update(Settings.FontScale);
        if (Dirty || _epoch != Fonts.Epoch) Render();
        var (x, y) = _pinned ?? ((capi.Render.FrameWidth - Canvas.Width) / 2,
            (capi.Render.FrameHeight - Canvas.Height) / 2);
        if (_moving) _snap.Draw();
        Canvas.Draw(Math.Round(x), Math.Round(y));
    }

    private void Render()
    {
        (Dirty, _epoch) = (false, Fonts.Epoch);
        Composing?.Invoke();
        Hits.Clear();
        Rows.Clear();
        (Pad, Gap, RowH) = (scaled(HudPanel.Padding), scaled(HudPanel.Gap), Fonts.BadgeRow + scaled(RowGap));
        (_headerRow, _titleRow) = (Fonts.HeaderRow, Fonts.TitleRow + scaled(RowGap));
        _labelW = 0;
        Canvas.BeginMeasure();
        if (!Assert(RowH > 0) || !Assert(_headerRow > 0) || !Assert(_titleRow > 0)) return;
        ControlW = Build();
        ControlX = Pad + _labelW + Gap;
        Width = ControlX + ControlW + Pad;
        var height = 2 * Pad;
        foreach (var (rowHeight, _) in Rows.Bounded(MaxRows)) height += rowHeight;
        if (!Assert(ControlW > 0) || !Assert(Rows.Count is > 1 and <= MaxRows) || !Assert(height > 0)) return;
        Canvas.Begin(Width, height);
        if (!Assert(Canvas.Width >= Width)) return; // Begin() refused the size
        Canvas.Fill(0, 0, Width, height, HudCanvas.PanelBackground with { A = Settings.Opacity }, scaled(4));
        var top = Pad;
        foreach (var (rowHeight, draw) in Rows.Bounded(MaxRows))
        {
            draw(top);
            top += rowHeight;
        }

        Canvas.End();
    }

    // "Komet", the window's name as a badge and × in the corner
    protected void Title()
    {
        if (!Assert(Rows.Count == 0) || !Assert(_titleRow > 0)) return;
        Action<double> draw = y =>
        {
            var title = HudText.Translate("hud-title");
            Canvas.Text(Pad, y, _titleRow, Fonts.Title, title);
            _ = Canvas.Badge(Pad + Canvas.TextWidth(Fonts.Title, title) + scaled(HudLine.BadgeGap), y, _titleRow, Fonts,
                Translate("title"), HudCanvas.Accent);
            Canvas.Text(Width - Pad - Canvas.TextWidth(Fonts.Title, "×"), y, _titleRow, Fonts.Title, "×");
            Hits.Add(new HitBox(Width - Pad - _titleRow, y, _titleRow, _titleRow, _ => TryClose()));
        };
        Rows.Add((_titleRow, draw));
    }

    protected void Rule()
    {
        if (!Assert(Rows.Count > 0) || !Assert(Rows.Count < MaxRows)) return;
        Rows.Add((Fonts.RuleRow, y => Canvas.Rule(Pad, y, Width - 2 * Pad, Fonts.RuleRow)));
    }

    protected void Header(string key)
    {
        if (!Assert(key.Length > 0) || !Assert(Rows.Count > 0)) return;
        Rule();
        Rows.Add((_headerRow,
            y => Canvas.Header(Pad, y, Width - 2 * Pad, _headerRow, Fonts, Translate(key))));
    }

    // The label in the left column, the control drawn by the caller at the row's y
    protected void Row(string key, Action<double> control)
    {
        if (!Assert(key.Length > 0) || !Assert(Rows.Count > 0)) return;
        var label = Translate(key);
        _labelW = Math.Max(_labelW, Canvas.TextWidth(Fonts.Text, label));
        Action<double> draw = y =>
        {
            Canvas.Text(Pad, y, RowH, Fonts.Text, label);
            control(y);
        };
        Rows.Add((RowH, draw));
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (args.Button != EnumMouseButton.Left) return;
        Release(); // a mouse up that went to another handler left a drag behind
        double x = args.X - Canvas.X, y = args.Y - Canvas.Y;
        if (Hits.Find(h => x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) is { } hit)
        {
            if (!Assert(hit.W > 0) || !Assert(hit.H > 0)) return;
            hit.Click((x - hit.X) / hit.W);
            _drag = hit.Drag ? hit : null;
            return;
        }

        (_grabX, _grabY, _moving) = (x, y, true);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        var step = scaled(HudSettings.SnapGrid);
        if (!NotNull(args) || !Assert(step > 0)) return;
        if (_drag != null && Assert(_drag.W > 0))
            _drag.Click(Math.Clamp((args.X - Canvas.X - _drag.X) / _drag.W, 0, 1));
        else if (_moving)
            _pinned = (Math.Round((args.X - _grabX) / step) * step, Math.Round((args.Y - _grabY) / step) * step);
        else return;
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        Release();
    }

    private void Release()
    {
        (_drag, _moving) = (null, false);
    }

    public override void Dispose()
    {
        base.Dispose();
        Canvas.Dispose();
    }

    protected sealed record HitBox(
        double X,
        double Y,
        double W,
        double H,
        Action<double> Click,
        bool Drag = false); // Click gets the x fraction
}
