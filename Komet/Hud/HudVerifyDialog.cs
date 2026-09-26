using System.Globalization;

namespace Komet.Hud;

// Checksum window: the sha256 of the installed zip next to the one CI published for the same release, character by character. Drawn
// like a HUD panel; GuiDialog only hosts it (free cursor, Escape, mouse events). Build() adds rows and returns the width of the control
// column; Render() measures the labels, stacks the rows top to bottom and draws them. A click goes to the hit box under the cursor, a
// drag on empty space moves the window with grid snap. Lang keys are "verify-<key>".
internal sealed class HudVerifyDialog : GuiDialog
{
    private const int MaxRows = 48; // past the bound a page is not drawn
    private const double RowGap = 4, ButtonGap = 8;

    private readonly HudSettings _settings;
    private readonly HudFonts _fonts;
    private readonly SnapGrid _snap;
    private readonly Func<UpdateCheck?> _update;
    private readonly Action _recheck;
    private readonly HudCanvas _canvas;
    private readonly List<HitBox> _hits = [];
    private readonly List<(double Height, Action<double> Draw)> _rows = [];
    private UpdateReport? _shown;
    private int _epoch; // HudFonts.Epoch of the last composition
    private double _grabX, _grabY;
    private double _headerRow, _titleRow, _labelW, _width, _pad, _rowH;
    private double _controlX; // right of the widest label; set after Build(), so only row lambdas read it
    private bool _dirty = true, _moving; // _moving: the window follows the cursor
    private (double X, double Y)? _pinned;

    public HudVerifyDialog(ICoreClientAPI capi, HudSettings settings, HudFonts fonts, SnapGrid snap,
        Func<UpdateCheck?> update, Action recheck) : base(capi)
    {
        (_settings, _fonts, _snap, _update, _recheck) = (settings, fonts, snap, update, recheck);
        _canvas = new HudCanvas(capi);
        if (NotNull(settings) && NotNull(update)) settings.Changed += () => _dirty = true;
    }

    public override string? ToggleKeyCombinationCode => null;

    // Raised by every composition (a Cairo pass over every row and a texture upload), which the HUD keeps out of its steady frames
    public event Action? Composing;

    public override void OnGuiClosed()
    {
        _moving = false;
    }

    public bool Contains(double px, double py)
    {
        return IsOpened() && _canvas.Contains(px, py);
    }

    private static string Translate(string key, params object[] args)
    {
        return HudText.Translate("verify-" + key, args);
    }

    // Composed again only when something it shows changed (a new report: a check finished, or "check again" started one; a setting)
    // or the fonts were rebuilt (the font or the GUI scale, which changes without an event), not on every open: the canvas and its
    // texture outlive a close
    public override void OnRenderGUI(float deltaTime)
    {
        var report = _update()?.Report;
        if (!ReferenceEquals(report, _shown)) (_shown, _dirty) = (report, true);
        if (!Finite(deltaTime) || !Assert(capi.Render.FrameWidth > 0) || !Assert(capi.Render.FrameHeight > 0)) return;
        _fonts.Update(_settings.FontScale);
        if (_dirty || _epoch != _fonts.Epoch) Render();
        var (x, y) = _pinned ??
                     ((capi.Render.FrameWidth - _canvas.Width) / 2, (capi.Render.FrameHeight - _canvas.Height) / 2);
        if (_moving) _snap.Draw();
        _canvas.Draw(Math.Round(x), Math.Round(y));
    }

    private void Render()
    {
        (_dirty, _epoch) = (false, _fonts.Epoch);
        Composing?.Invoke();
        _hits.Clear();
        _rows.Clear();
        (_pad, var gap, _rowH) = (scaled(HudPanel.Padding), scaled(HudPanel.Gap), _fonts.BadgeRow + scaled(RowGap));
        (_headerRow, _titleRow) = (_fonts.HeaderRow, _fonts.TitleRow + scaled(RowGap));
        _labelW = 0;
        _canvas.BeginMeasure();
        if (!Assert(_rowH > 0) || !Assert(_headerRow > 0) || !Assert(_titleRow > 0)) return;
        var controlW = Build();
        _controlX = _pad + _labelW + gap;
        _width = _controlX + controlW + _pad;
        var height = 2 * _pad;
        foreach (var (rowHeight, _) in _rows.Bounded(MaxRows)) height += rowHeight;
        if (!Assert(controlW > 0) || !Assert(_rows.Count is > 1 and <= MaxRows) || !Assert(height > 0)) return;
        if (!_canvas.BeginPanel(_width, height, _settings.Opacity)) return;
        var top = _pad;
        foreach (var (rowHeight, draw) in _rows.Bounded(MaxRows))
        {
            draw(top);
            top += rowHeight;
        }

        _canvas.End();
    }

    private double Build()
    {
        var (r, check, none) = (_shown ?? UpdateReport.Pending, _update(), Translate("none"));
        var cell = "0123456789abcdef".Max(digit =>
            _canvas.TextWidth(_fonts.Text, digit.ToString(CultureInfo.InvariantCulture)));
        if (!Assert(cell > 0)) return 0;
        var own = check is null ? none : check.BuildTag; // the build's own tag, also when GitHub has no release of it
        // what a verdict names: "no release {0}" names the one it looked for
        var named = r.Tag.Length > 0 ? r.Tag : own;
        var (verdict, color) = r.Verdict();
        var text = HudText.Translate(verdict, r.State == UpdateState.Failed ? r.Detail : named);
        Title();
        Row("file",
            y => _canvas.Text(_controlX, y, _rowH, _fonts.Text, check?.FileName is { Length: > 0 } file ? file : none));
        Row("build", y => _canvas.Text(_controlX, y, _rowH, _fonts.Text, own));
        Row("release", y => Field(y, r.Tag, none));
        Row("released", y => Field(y, r.Released, none));
        Header("sha256");
        Row("installed", y => Hash(y, cell, r.Installed, r.Published));
        Row("github", y => Hash(y, cell, r.Published, ""));
        Header("result");
        _rows.Add((_rowH, y => _canvas.Text(_pad, y, _rowH, _fonts.Text, text, color)));
        Row("newest", y => Newest(y, r, none));
        Rule();
        Buttons(r);
        return UpdateReport.HashLength * cell;
    }

    // "Komet", the window's name as a badge and × in the corner
    private void Title()
    {
        if (!Assert(_rows.Count == 0) || !Assert(_titleRow > 0)) return;
        Action<double> draw = y =>
        {
            var title = HudText.Translate("hud-title");
            _canvas.Text(_pad, y, _titleRow, _fonts.Title, title);
            _ = _canvas.Badge(_pad + _canvas.TextWidth(_fonts.Title, title) + scaled(HudLine.BadgeGap), y, _titleRow,
                _fonts, Translate("title"), HudCanvas.Accent);
            _canvas.Text(_width - _pad - _canvas.TextWidth(_fonts.Title, "×"), y, _titleRow, _fonts.Title, "×");
            _hits.Add(new HitBox(_width - _pad - _titleRow, y, _titleRow, _titleRow, () => TryClose()));
        };
        _rows.Add((_titleRow, draw));
    }

    private void Rule()
    {
        if (!Assert(_rows.Count > 0) || !Assert(_rows.Count < MaxRows)) return;
        _rows.Add((_fonts.RuleRow, y => _canvas.Rule(_pad, y, _width - 2 * _pad, _fonts.RuleRow)));
    }

    private void Header(string key)
    {
        if (!Assert(key.Length > 0) || !Assert(_rows.Count > 0)) return;
        Rule();
        var text = Translate(key);
        _rows.Add((_headerRow, y => _canvas.Header(_pad, y, _width - 2 * _pad, _headerRow, _fonts, text)));
    }

    // The label in the left column, the control drawn by the caller at the row's y
    private void Row(string key, Action<double> control)
    {
        if (!Assert(key.Length > 0) || !Assert(_rows.Count > 0)) return;
        var label = Translate(key);
        _labelW = Math.Max(_labelW, _canvas.TextWidth(_fonts.Text, label));
        _rows.Add((_rowH, y =>
        {
            _canvas.Text(_pad, y, _rowH, _fonts.Text, label);
            control(y);
        }));
    }

    // The text, or a dimmed none without one
    private void Field(double y, string text, string none)
    {
        var shown = text.Length > 0;
        if (!Finite(y)) return;
        _canvas.Text(_controlX, y, _rowH, _fonts.Text, shown ? text : none, shown ? null : HudCanvas.Dim);
    }

    private void Newest(double y, UpdateReport r, string none)
    {
        Field(y, r.Newest, none);
        if (r.State is UpdateState.Checking or UpdateState.Failed || r.Newest.Length == 0) return;
        var (key, color) = r.State == UpdateState.Outdated
            ? ("update", HudCanvas.Warning)
            : ("current", HudCanvas.Good);
        _ = _canvas.Badge(_controlX + _canvas.TextWidth(_fonts.Text, r.Newest) + scaled(HudLine.BadgeGap), y, _rowH,
            _fonts, Translate(key), color);
    }

    // Close at the right edge, check again beside it
    private void Buttons(UpdateReport r)
    {
        string[] buttons = [Translate("recheck"), Translate("close")];
        var width = buttons.Max(text => _canvas.BadgeWidth(_fonts, text));
        if (!Assert(width > 0) || !Assert(_rows.Count > 0)) return;
        Action<double> draw = y =>
        {
            var x = _width - _pad - width;
            Button(x, y, width, buttons[1], HudCanvas.Neutral, () => TryClose());
            Button(x - width - scaled(ButtonGap), y, width, buttons[0],
                r.State == UpdateState.Checking ? HudCanvas.Disabled : HudCanvas.Accent, _recheck);
        };
        _rows.Add((_rowH, draw));
    }

    // A badge that runs click
    private void Button(double x, double y, double w, string text, Rgba color, Action click)
    {
        if (!Assert(w > 0) || !Assert(text.Length > 0) || !NotNull(click)) return;
        _hits.Add(new HitBox(x, y, w, _rowH, click));
        _ = _canvas.Badge(x, y, _rowH, _fonts, text, color, w);
    }

    // One cell per hex digit so both hashes line up; with a reference to compare with, each digit is green or red by whether it matches
    private void Hash(double y, double cell, string hash, string reference)
    {
        if (hash.Length == 0)
        {
            _canvas.Text(_controlX, y, _rowH, _fonts.Text, Translate("none"), HudCanvas.Dim);
            return;
        }

        if (!Assert(cell > 0) || !Assert(hash.Length == UpdateReport.HashLength) ||
            !Assert(reference.Length is 0 or UpdateReport.HashLength)) return;
        for (var i = 0; i < Math.Min(hash.Length, UpdateReport.HashLength); i++)
        {
            Rgba? color = null;
            if (reference.Length > 0) color = hash[i] == reference[i] ? HudCanvas.Good : HudCanvas.Error;
            _canvas.Text(_controlX + i * cell, y, _rowH, _fonts.Text, hash[i..(i + 1)], color);
        }
    }

    public override void OnMouseDown(MouseEvent args)
    {
        if (!Contains(args.X, args.Y)) return;
        args.Handled = true;
        if (args.Button != EnumMouseButton.Left) return;
        double x = args.X - _canvas.X, y = args.Y - _canvas.Y;
        if (_hits.Find(h => x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) is { } hit)
        {
            hit.Click();
            return;
        }

        (_grabX, _grabY, _moving) = (x, y, true);
    }

    public override void OnMouseMove(MouseEvent args)
    {
        var step = scaled(HudSettings.SnapStep);
        if (!_moving || !NotNull(args) || !Assert(step > 0)) return;
        _pinned = (Math.Round((args.X - _grabX) / step) * step, Math.Round((args.Y - _grabY) / step) * step);
        args.Handled = true;
    }

    public override void OnMouseUp(MouseEvent args)
    {
        _moving = false;
    }

    public override void Dispose()
    {
        base.Dispose();
        _canvas.Dispose();
    }

    private sealed record HitBox(double X, double Y, double W, double H, Action Click);
}
