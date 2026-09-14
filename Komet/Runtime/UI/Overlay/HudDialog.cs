namespace Komet.Runtime.UI.Overlay;

// A window drawn like a HUD panel; GuiDialog only hosts it (free cursor, Escape, mouse events). Build() adds rows and returns the
// width, the base stacks them top to bottom. A click goes to the hit box under the cursor, a drag on empty space moves the window
// with grid snap. Lang keys are "<prefix>-<key>".
internal abstract class HudDialog : GuiDialog
{
    protected const int MaxRows = 32;
    private const double RowGap = 4;

    protected sealed record HitBox(double X, double Y, double W, double H, Action<double> Click, bool Drag = false);   // Click gets the x fraction

    private readonly string _prefix;
    private string[] _labels = [];
    private double _headerRow, _titleRow;
    private HitBox? _drag;
    private HudCanvas? _grid;
    private (double X, double Y)? _pinned;
    private double _grabX, _grabY;

    protected HudSettings Settings { get; }
    protected HudCanvas Canvas { get; }
    protected List<HitBox> Hits { get; } = [];
    protected List<(double Height, Action<double> Draw)> Rows { get; } = [];
    protected double Width { get; private set; }
    protected double Pad { get; private set; }
    protected double Gap { get; private set; }
    protected double RowH { get; private set; }
    protected bool Dirty { get; set; } = true;

    protected HudDialog(ICoreClientAPI capi, HudSettings settings, string prefix) : base(capi)
    {
        (Settings, _prefix) = (settings, prefix);
        Canvas = new HudCanvas(capi);
        if (NotNull(settings) && Assert(prefix.Length > 0)) settings.Changed += () => Dirty = true;
    }

    public override string? ToggleKeyCombinationCode => null;
    public override void OnGuiOpened() => Dirty = true;
    public override void OnGuiClosed() => Release();
    public bool Contains(double px, double py) => IsOpened() && Canvas.Contains(px, py);
    protected string Translate(string key, params object[] args) => HudSettings.Translate(_prefix + "-" + key, args);

    // Adds the rows and returns the window width; the rows are drawn afterwards, so their lambdas may read Width
    protected abstract double Build();

    public override void OnRenderGUI(float deltaTime)
    {
        if (!Finite(deltaTime) || !Assert(capi.Render.FrameWidth > 0) || !Assert(capi.Render.FrameHeight > 0)) return;
        if (Dirty) Render();
        var (x, y) = _pinned ?? ((capi.Render.FrameWidth - Canvas.Width) / 2, (capi.Render.FrameHeight - Canvas.Height) / 2);
        _grid?.Draw(0, 0);
        Canvas.Draw(Math.Round(x), Math.Round(y));
    }

    private void Render()
    {
        Dirty = false;
        Hits.Clear();
        Rows.Clear();
        (Pad, Gap, RowH) = (scaled(HudPanel.Padding), scaled(HudPanel.Gap), HudCanvas.BadgeHeight(Settings.Header) + scaled(RowGap));
        _headerRow = HudCanvas.HeaderHeight(Settings.Header);
        _titleRow = Math.Max(HudCanvas.LineHeight(Settings.Title), HudCanvas.BadgeHeight(Settings.Header)) + scaled(RowGap);
        if (!Assert(RowH > 0) || !Assert(_headerRow > 0) || !Assert(_titleRow > 0)) return;
        Width = Build();
        var height = 2 * Pad;
        foreach (var (rowHeight, _) in Rows.Bounded(MaxRows)) height += rowHeight;
        if (!Assert(Width > 2 * Pad) || !Assert(Rows.Count is > 1 and <= MaxRows) || !Assert(height > 0)) return;
        Canvas.Begin(Width, height);
        if (!Assert(Canvas.Width >= Width)) return;   // Begin() refused the size
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
        Rows.Add((_titleRow, y =>
        {
            var title = HudSettings.Translate("hud-title");
            Canvas.Text(Pad, y, _titleRow, Settings.Title, title);
            _ = Canvas.Badge(Pad + HudCanvas.TextWidth(Settings.Title, title) + scaled(HudLine.BadgeGap), y, _titleRow, Settings.Header, Translate("title"), HudCanvas.Accent);
            Canvas.Text(Width - Pad - HudCanvas.TextWidth(Settings.Title, "×"), y, _titleRow, Settings.Title, "×");
            Hits.Add(new HitBox(Width - Pad - _titleRow, y, _titleRow, _titleRow, _ => TryClose()));
        }));
    }

    protected void Rule()
    {
        if (!Assert(Rows.Count > 0) || !Assert(Rows.Count < MaxRows)) return;
        Rows.Add((HudCanvas.RuleHeight, y => Canvas.Rule(Pad, y, Width - (2 * Pad), HudCanvas.RuleHeight)));
    }

    protected void Header(string key)
    {
        if (!Assert(key.Length > 0) || !Assert(Rows.Count > 0)) return;
        Rule();
        Rows.Add((_headerRow, y => Canvas.Header(Pad, y, Width - (2 * Pad), _headerRow, Settings.Header, Translate(key))));
    }

    // Widest of the labels; Row() accepts only these, so the label column always fits
    protected double LabelColumn(string[] keys)
    {
        _labels = keys;
        double width = 0;
        foreach (var key in keys.Bounded(MaxRows)) width = Math.Max(width, HudCanvas.TextWidth(Settings.Text, Translate(key)));
        return Assert(keys.Length > 0) && Assert(width > 0) ? width : 0;
    }

    // The label in the left column, the control drawn by the caller at the row's y
    protected void Row(string key, Action<double> control)
    {
        if (!Assert(Array.IndexOf(_labels, key) >= 0) || !Assert(Rows.Count > 0)) return;
        Rows.Add((RowH, y =>
        {
            Canvas.Text(Pad, y, RowH, Settings.Text, Translate(key));
            control(y);
        }));
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (args.Button != EnumMouseButton.Left || !Assert(_drag is null) || !Assert(_grid is null)) return;   // a mouse up went missing
        double x = args.X - Canvas.X, y = args.Y - Canvas.Y;
        if (Hits.Find(h => x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) is { } hit)
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
        if (!NotNull(args) || !Assert(step > 0)) return;
        if (_drag != null && Assert(_drag.W > 0)) _drag.Click(Math.Clamp((args.X - Canvas.X - _drag.X) / _drag.W, 0, 1));
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
        Canvas.Dispose();
    }
}
