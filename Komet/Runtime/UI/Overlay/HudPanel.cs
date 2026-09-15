using System.Text;
using Komet.Runtime.Diagnostics;

namespace Komet.Runtime.UI.Overlay;

internal sealed class HudPanel(ICoreClientAPI capi, HudSettings settings, int index, int column, int refreshEvery, Func<bool>? enabled, LogStats? log) : IDisposable
{
    public const double Padding = 6, Gap = 14;
    private const int MaxRows = 64, MaxLines = 4 * MaxRows;

    private readonly HudCanvas _canvas = new(capi);
    private readonly List<HudLine> _lines = [];
    private double _labelW, _percentW, _valueW, _unitW;   // grow only, otherwise the layout jumps
    private double _linesHeight, _barBlock, _valueBlock;       // from the last Measure()
    private bool _lastDetail;

    public int Column => column;
    public LogStats? Log => log;
    public bool Visible => enabled?.Invoke() != false;
    public double Width => _canvas.Width;
    public double Height => _canvas.Height;
    public double X => _canvas.X;
    public double Y => _canvas.Y;
    public double NaturalWidth { get; private set; }
    public (double X, double Y)? Pinned => settings.Pinned.GetValueOrDefault(index) is [var x, var y] ? (x, y) : null;
    public bool Contains(double px, double py) => _canvas.Contains(px, py);
    public void Dispose() => _canvas.Dispose();

    public void Draw(double x, double y)
    {
        if (!Assert(Width > 0)) return;   // Refresh() has not run yet
        _canvas.Draw(x, y);
    }

    public void Pin(double x, double y)
    {
        if (!Finite(x) || !Finite(y)) return;
        settings.Pinned[index] = [x, y];
    }

    // key = lang key without "hud-", label = final text
    public HudPanel Title(string key, params (string Text, Rgba Color)[] badges)
        => Assert(key.Length > 0) && Assert(badges.Length <= HudLine.MaxBadges) ? Add(new() { Kind = HudLineKind.Title, Label = () => HudSettings.Translate("hud-" + key), Badges = badges }) : this;
    public HudPanel Graph(FrameStats frames) => Assert(frames.HistoryLength >= HudCanvas.GraphFrames) ? Add(new HudLine { Kind = HudLineKind.Graph, Graph = frames }) : this;
    public HudPanel Bar(string key, Func<double> percent, Func<double>? value = null, string unit = "") => Assert(key.Length > 0) ? Line(() => HudSettings.Translate("hud-" + key), value, unit, percent) : this;
    public HudPanel Value(string key, Func<double> value, string unit = "", bool sub = false, bool detail = false) => Assert(key.Length > 0) ? Line(() => HudSettings.Translate("hud-" + key), value, unit, sub: sub, detail: detail) : this;
    public HudPanel Line(Func<string> label, Func<double>? value = null, string unit = "", Func<double>? percent = null, Func<double>? marker = null, bool sub = false, bool detail = false, Func<Rgba?>? color = null)
        => Assert(unit.Length <= 2) && Assert(marker == null || percent != null) ? Add(new() { Label = label, Value = value, Unit = unit, Percent = percent, Marker = marker, Sub = sub, Detail = detail, Color = color }) : this;

    public HudPanel Section(string key, params object[] args)
    {
        if (!Assert(key.Length > 0)) return this;
        if (_lines.Count > 0) _ = Add(new() { Kind = HudLineKind.Rule });
        return Add(new() { Kind = HudLineKind.Header, Label = () => HudSettings.Translate("hud-" + key, args) });
    }

    public HudPanel Rows(int count, Action<int> add)
    {
        if (!Assert(count > 0) || !Assert(count <= MaxRows)) return this;
        for (var i = 0; i < Math.Min(count, MaxRows); i++) add(i);
        return this;
    }

    private HudPanel Add(HudLine line)
    {
        if (Assert(_lines.Count < MaxLines)) _lines.Add(line);
        return this;
    }

    public void Accumulate() { foreach (var line in _lines.Bounded(MaxLines)) line.Accumulate(); }
    public void ResetBench() { foreach (var line in _lines.Bounded(MaxLines)) line.ResetBench(); }
    public void AppendText(StringBuilder text, bool mean)
    {
        if (!Assert(_lines.Count > 0)) return;
        var first = true;
        foreach (var line in _lines.Bounded(MaxLines))
        {
            var row = line.ToText(mean);
            if (row.Length == 0) continue;
            if (!first) _ = text.Append('\n');
            _ = text.Append(row);
            first = false;
        }
    }

    // Measures the lines when the panel is due this interval; true when it then needs Render()
    public bool Measure(int interval, bool detail)
    {
        if (!Assert(interval >= 0) || !Assert(refreshEvery > 0) || !Assert(_lines.Count > 0)) return false;
        if (interval % refreshEvery != 0 || !Visible) return false;
        if (detail != _lastDetail) _labelW = _percentW = _valueW = _unitW = 0;
        _lastDetail = detail;
        var font = settings.Text;
        _linesHeight = 0;
        foreach (var line in _lines.Bounded(MaxLines))
        {
            if (line.Detail && !detail) continue;
            line.Measure(settings);
            if (line.Empty) continue;
            _linesHeight += line.Height;
            _labelW = Math.Max(_labelW, line.LabelWidth);
            _percentW = Math.Max(_percentW, line.PercentWidth);
            _valueW = Math.Max(_valueW, line.ValueWidth);
            _unitW = Math.Max(_unitW, HudCanvas.TextWidth(font, line.Unit));
        }
        double gap = scaled(Gap), unitGap = scaled(HudLine.UnitGap);
        _barBlock = _percentW > 0 ? gap + HudCanvas.BarWidth + gap + _percentW + unitGap + HudCanvas.TextWidth(font, "%") : 0;
        _valueBlock = _valueW > 0 ? gap + _valueW + unitGap + _unitW : 0;
        NaturalWidth = _labelW + _barBlock + _valueBlock + (2 * scaled(Padding));
        return Assert(_linesHeight > 0) && Assert(_labelW > 0);   // every panel starts with a title or a section header
    }

    // Draws the lines as last measured, at least minWidth wide: the column's width, so the panels of a column line up
    public void Render(double minWidth)
    {
        double gap = scaled(Gap), pad = scaled(Padding);
        if (!Assert(minWidth >= 0) || !Assert(_linesHeight > 0) || !Assert(NaturalWidth > 0)) return;   // Measure() first
        var start = _labelW + Math.Max(0, minWidth - NaturalWidth);
        var columns = new HudColumns(start + _barBlock + _valueBlock, start + gap, start + gap + HudCanvas.BarWidth + gap + _percentW, start + _barBlock + gap + _valueW);
        _canvas.Begin(columns.RowWidth + (2 * pad), _linesHeight + (2 * pad));
        if (!Assert(_canvas.Width >= NaturalWidth)) return;   // Begin() refused the size
        _canvas.Fill(0, 0, _canvas.Width, _canvas.Height, HudCanvas.PanelBackground with { A = settings.Opacity }, scaled(4));
        var y = pad;
        foreach (var line in _lines.Bounded(MaxLines))
        {
            if (line.Detail && !_lastDetail || line.Empty) continue;
            line.Draw(_canvas, settings, pad, y, columns);
            y += line.Height;
        }
        _canvas.End();
    }
}
