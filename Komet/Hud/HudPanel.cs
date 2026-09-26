using System.Text;

namespace Komet.Hud;

internal sealed class HudPanel(
    ICoreClientAPI capi,
    HudSettings settings,
    HudFonts fonts,
    int index,
    int column,
    int refreshEvery,
    int phase,
    Func<bool>? enabled,
    LogStats? log) : IDisposable
{
    public const double Padding = 6, Gap = 14;
    private const int MaxRows = 64, MaxLines = 4 * MaxRows;

    private readonly HudCanvas _canvas = new(capi);
    private readonly List<HudLine> _lines = [];
    private int _epoch; // HudFonts.Epoch the widths were measured at
    private double _labelW, _percentW, _valueW, _unitW, _textW; // grow only, otherwise the layout jumps
    private bool _lastDetail;
    private double _linesHeight, _barBlock, _valueBlock; // from the last Measure()
    private Action? _peaks; // the features' maxima, reset at the end of an interval in which a row read them
    private bool _peaksRead;

    public int Column => column;
    public int RefreshEvery => refreshEvery;
    public LogStats? Log => log;
    public bool Visible => enabled?.Invoke() != false;
    public double Width => _canvas.Width;
    public double Height => _canvas.Height;
    public double NaturalWidth { get; private set; }
    public bool Ready => _canvas.Ready; // drawn and uploaded at least once; before that there is nothing to show
    public (double X, double Y)? Pinned => settings.Pinned.GetValueOrDefault(index) is [var x, var y] ? (x, y) : null;

    public void Dispose()
    {
        _canvas.Dispose();
    }

    public void Draw(double x, double y)
    {
        if (Ready) _canvas.Draw(x, y);
    }

    public void Pin(double x, double y)
    {
        if (!Finite(x) || !Finite(y)) return;
        settings.Pinned[index] = [x, y];
    }

    // Keys are lang keys without "hud-"; their text is translated once per language
    public HudPanel Title(string key, (string Text, Rgba Color)[] badges)
    {
        return Assert(key.Length > 0) && Assert(badges.Length <= HudLine.MaxBadges)
            ? Add(new HudLine { Kind = HudLineKind.Title, Label = HudText.Once("hud-" + key), Badges = badges })
            : this;
    }

    public HudPanel Graph(FrameStats frames)
    {
        return Assert(frames.HistoryLength >= HudCanvas.GraphFrames)
            ? Add(new HudLine { Kind = HudLineKind.Graph, Graph = frames })
            : this;
    }

    public HudPanel Bar(string key, Func<double> percent, Func<double>? value = null, string unit = "",
        bool good = false, bool detail = false)
    {
        return Assert(key.Length > 0)
            ? Line(HudText.Once("hud-" + key), value, unit, percent, detail: detail, good: good)
            : this;
    }

    public HudPanel Value(string key, Func<double> value, string unit = "", bool sub = false, bool detail = false,
        bool final = false)
    {
        return Assert(key.Length > 0)
            ? Line(HudText.Once("hud-" + key), value, unit, sub: sub, detail: detail, final: final)
            : this;
    }

    public HudPanel Line(Func<string> label, Func<double>? value = null, string unit = "", Func<double>? percent = null,
        Func<double>? marker = null, bool sub = false, bool detail = false, bool final = false,
        Func<Rgba?>? color = null, bool good = false)
    {
        return Assert(unit.Length <= HudLine.MaxUnit) && Assert(marker == null || percent != null)
            ? Add(new HudLine
            {
                Label = label, Value = value, Unit = unit, Percent = percent, Marker = marker, Sub = sub,
                Detail = detail, Final = final, Color = color, Good = good
            })
            : this;
    }

    public HudPanel Section(string key, params object[] args)
    {
        if (!Assert(key.Length > 0)) return this;
        if (_lines.Count > 0) _ = Add(new HudLine { Kind = HudLineKind.Rule });
        return Add(new HudLine { Kind = HudLineKind.Header, Label = HudText.Once("hud-" + key, args) });
    }

    // A status line under a section while the condition holds, blank otherwise
    public HudPanel Warn(string key, Func<bool> when)
    {
        var text = HudText.Once("hud-" + key);
        return Assert(key.Length > 0) && NotNull(when)
            ? Line(() => when() ? text() : "", sub: true, color: static () => HudCanvas.Warning)
            : this;
    }

    public HudPanel Peaks(Action reset)
    {
        if (NotNull(reset) && Assert(_lines.Count > 0)) _peaks += reset;
        return this;
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

    public void Accumulate()
    {
        foreach (var line in _lines.Bounded(MaxLines)) line.Accumulate();
        _peaksRead = true;
    }

    // After both readers of an interval, the panel and the bench, so both see the same maxima
    public void ResetPeaks()
    {
        if (!_peaksRead) return;
        _peaksRead = false;
        _peaks?.Invoke();
    }

    public void ResetBench()
    {
        foreach (var line in _lines.Bounded(MaxLines)) line.ResetBench();
    }

    public void Prime()
    {
        foreach (var line in _lines.Bounded(MaxLines)) line.Prime();
    }

    public void AppendMeans(StringBuilder text)
    {
        if (!Assert(_lines.Count > 0)) return;
        var first = true;
        foreach (var line in _lines.Bounded(MaxLines))
        {
            var row = line.MeanText();
            if (row.Length == 0) continue;
            if (!first) _ = text.Append('\n');
            _ = text.Append(row);
            first = false;
        }
    }

    // The rows as the last Measure() left them, the ones Render() draws: nothing for a hidden panel or one not drawn yet, which is not
    // on screen either
    public void AppendShown(StringBuilder text)
    {
        if (!Assert(_lines.Count > 0) || !NotNull(text) || !Visible || !Ready) return;
        _ = Assert(NaturalWidth > 0); // drawn means measured
        AppendShown(text, _lines, _lastDetail);
    }

    // The rows Render() draws with detail as last measured, after a blank line when text already holds a panel. A detail row that
    // was not measured holds the text of an older interval, so it is skipped here as it is there.
    internal static void AppendShown(StringBuilder text, List<HudLine> lines, bool detail)
    {
        if (!NotNull(text) || !Assert(lines.Count <= MaxLines)) return;
        var first = true;
        foreach (var line in lines.Bounded(MaxLines))
        {
            if ((line.Detail && !detail) || line.Empty) continue;
            var row = line.ShownText();
            if (row.Length == 0) continue;
            if (!first) _ = text.Append('\n');
            else if (text.Length > 0) _ = text.Append("\n\n");
            _ = text.Append(row);
            first = false;
        }
    }

    // Measures the lines when the panel is due this interval; true when it then needs Render(). The phase spreads a cadence over its
    // own period, so the slow panels do not all come due on one frame. force = due now regardless, for a direct refresh.
    public bool Measure(int interval, bool detail, bool force = false)
    {
        if (!Assert(interval >= 0) || !Assert(refreshEvery > 0) || !Assert(_lines.Count > 0)) return false;
        if (!Visible || (!force && (interval + phase) % refreshEvery != 0)) return false;
        if (detail != _lastDetail || _epoch != fonts.Epoch) _labelW = _percentW = _valueW = _unitW = _textW = 0;
        (_lastDetail, _epoch) = (detail, fonts.Epoch);
        var font = fonts.Text;
        _linesHeight = 0;
        _canvas.BeginMeasure();
        foreach (var line in _lines.Bounded(MaxLines))
        {
            if (line.Detail && !detail)
            {
                line.Read(); // so the first showing covers one refresh, not the time since detail was last on
                continue;
            }

            line.Measure(fonts, _canvas);
            if (line.Empty) continue;
            _linesHeight += line.Height;
            if (line.Columned) _labelW = Math.Max(_labelW, line.LabelWidth);
            else _textW = Math.Max(_textW, line.LabelWidth);
            _percentW = Math.Max(_percentW, line.PercentWidth);
            _valueW = Math.Max(_valueW, line.ValueWidth);
            _unitW = Math.Max(_unitW, _canvas.TextWidth(font, line.Unit));
        }

        _peaksRead = true;
        double gap = scaled(Gap), unitGap = scaled(HudLine.UnitGap);
        _barBlock = _percentW > 0
            ? gap + HudCanvas.BarWidth + gap + _percentW + unitGap + _canvas.TextWidth(font, "%")
            : 0;
        _valueBlock = _valueW > 0 ? gap + _valueW + unitGap + _unitW : 0;
        // A line of text alone (headers, titles, log rows, a status sentence) only needs the row wide enough, not the label column
        NaturalWidth = Math.Max(_labelW + _barBlock + _valueBlock, _textW) + 2 * scaled(Padding);
        return Assert(_linesHeight > 0) &&
               Assert(_labelW + _textW > 0); // every panel starts with a title or a section header
    }

    // Draws the lines as last measured, at least minWidth wide: the column's width, so the panels of a column line up
    public void Render(double minWidth)
    {
        double gap = scaled(Gap), pad = scaled(Padding);
        if (!Assert(minWidth >= 0) || !Assert(_linesHeight > 0) || !Assert(NaturalWidth > 0)) return; // Measure() first
        var row = Math.Max(minWidth, NaturalWidth) -
                  2 * pad; // the values sit at the right edge, where the column lines them up
        var start = row - _barBlock - _valueBlock;
        if (!Assert(start >= _labelW - 1e-6)) return;
        var columns = new HudColumns(row, start + gap, start + gap + HudCanvas.BarWidth + gap + _percentW,
            start + _barBlock + gap + _valueW);
        _canvas.Begin(columns.RowWidth + 2 * pad, _linesHeight + 2 * pad);
        if (!Assert(_canvas.Width >= NaturalWidth)) return; // Begin() refused the size
        _canvas.Fill(0, 0, _canvas.Width, _canvas.Height, HudCanvas.PanelBackground with { A = settings.Opacity },
            scaled(4));
        var y = pad;
        foreach (var line in _lines.Bounded(MaxLines))
        {
            if ((line.Detail && !_lastDetail) || line.Empty) continue;
            line.Draw(_canvas, fonts, pad, y, columns);
            y += line.Height;
        }

        _canvas.End();
    }
}
