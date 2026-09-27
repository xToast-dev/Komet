namespace Komet.Hud;

internal enum HudLineKind
{
    Text,
    Header,
    Rule,
    Title,
    Graph
}

internal readonly record struct HudColumns(double RowWidth, double BarX, double PercentRight, double ValueRight);

internal sealed class HudLine
{
    public const double UnitGap = 3, BadgeGap = 6;
    public const int MaxBadges = 4, MaxUnit = 2;

    // TitleGap: extra room between the title and its first badge
    private const double Indent = 12, GraphWidth = 320, GraphHeight = 90, TitleGap = 6;

    private Rgba? _color;
    private double _fraction, _marker, _valueSum, _percentSum;
    private bool _fresh;

    private string _label = "", _value = "", _percent = "";

    // The last Read(); the bench has not taken it yet while _fresh
    private double _read = double.NaN, _readPercent = double.NaN;

    // The windows each mean is over: a share can be missing where its time is not
    private int _samples, _percentSamples;

    public HudLineKind Kind { get; init; } = HudLineKind.Text;
    public bool Detail { get; init; }
    public Func<string> Label { get; init; } = static () => "";
    public bool Sub { get; init; }
    public Func<double>? Value { get; init; } // NaN = show nothing
    public bool Final { get; init; } // already covers the whole bench, so the report takes it as it stands
    public string Unit { get; init; } = "";
    public Func<double>? Percent { get; init; } // 0..100
    public Func<double>? Marker { get; init; }
    public bool Good { get; init; } // a full bar is the good outcome, so the bar turns green as it fills
    public Func<Rgba?>? Color { get; init; } // label color, null = default
    public (string Text, Rgba Color)[] Badges { get; init; } = [];
    public FrameStats? Graph { get; init; }

    public double LabelWidth { get; private set; }
    public double Height { get; private set; }
    public double ValueWidth { get; private set; }
    public double PercentWidth { get; private set; }
    public bool Empty => Kind == HudLineKind.Text && _label.Length == 0; // dynamic lists leave unused rows blank

    // A row whose label lines up with a bar or a value; everything else only needs its own width
    public bool Columned => Kind == HudLineKind.Text && (Value != null || Percent != null);
    private string NumberFormat => Unit switch { "ms" => "F2", "/s" => "F1", _ => "N0" };

    public void Measure(HudFonts fonts, HudCanvas canvas)
    {
        Capture();
        ValueWidth = PercentWidth = 0;
        var font = Kind switch
        {
            HudLineKind.Title => fonts.Title,
            HudLineKind.Header => fonts.Header,
            _ => fonts.Text
        };
        Height = Kind switch
        {
            HudLineKind.Text => fonts.TextRow,
            HudLineKind.Header => fonts.HeaderRow,
            HudLineKind.Rule => fonts.RuleRow,
            HudLineKind.Title => fonts.TitleRow,
            _ => scaled(GraphHeight)
        };
        if (!Assert(Kind != HudLineKind.Graph || Graph != null) || !Assert(Height > 0))
        {
            Height = 0;
            return;
        }

        LabelWidth = Kind switch
        {
            HudLineKind.Graph => scaled(GraphWidth),
            HudLineKind.Title => scaled(TitleGap) + canvas.TextWidth(font, _label),
            _ => (Sub ? scaled(Indent) : 0) + canvas.TextWidth(font, _label)
        };
        foreach (var (text, _) in Badges.Bounded(MaxBadges))
            LabelWidth += scaled(BadgeGap) + canvas.BadgeWidth(fonts, text);
        if (Kind != HudLineKind.Text || !Assert(LabelWidth >= 0)) return;
        if (Value != null) ValueWidth = canvas.TextWidth(font, _value);
        if (Percent != null) PercentWidth = canvas.TextWidth(font, _percent);
    }

    // The row's strings, read from the counters once at the end of the interval that filled them: the panel draws them and the dump
    // quotes them, so both show that interval. A number that is not finite shows as nothing and draws an empty bar.
    public void Capture()
    {
        _label = Label();
        _color = Color?.Invoke();
        (_value, _percent, _fraction, _marker) = ("", "", 0, 0);
        if (!Assert(Badges.Length <= MaxBadges) || !Assert(Marker == null || Percent != null) ||
            Kind != HudLineKind.Text) return;
        Read();
        if (Value != null) _value = HudText.Format(_read, NumberFormat);
        if (Percent == null) return;
        var marker = Marker?.Invoke() ?? 0;
        _fraction = double.IsFinite(_readPercent) ? Math.Clamp(_readPercent / 100, 0, 1) : 0;
        _marker = double.IsFinite(marker) ? Math.Clamp(marker / 100, 0, 1) : 0;
        _percent = HudText.Format(_readPercent, "F1");
    }

    // A counter row's number is a Growth: it covers the span since the row was last read, by whoever read it. So the panel reads it once
    // per refresh, and the bench, which accumulates every interval, takes that reading on an interval that had one instead of reading
    // again (it would get an empty span); on the others the bench's read starts the panel's next span.
    public void Read()
    {
        (_read, _readPercent, _fresh) = (Value?.Invoke() ?? double.NaN, Percent?.Invoke() ?? double.NaN, true);
        _ = Assert(Kind == HudLineKind.Text || (Value == null && Percent == null)); // only text rows carry numbers
    }

    // A new baseline for the row's Growths when counting starts: no reading the panel shows or the bench takes
    public void Prime()
    {
        Read();
        _fresh = false;
    }

    // The row as the last Capture() left it: what the panel shows, never read again. "" for a row the panel leaves blank.
    public string ShownText()
    {
        if (!Assert(_value.Length == 0 || Value != null) || !Assert(_percent.Length == 0 || Percent != null)) return "";
        return Text(_label, Value == null ? null : _value, Percent == null ? null : _percent);
    }

    // A statistic without a window yet is not a sample, nor is a share of an empty one. Value and share are averaged over their own
    // windows: a mod's time is known in a window whose share is not (no frame time to divide by), and the panel shows that time, so
    // a missing share must not drop it from the mean as well.
    public void Accumulate()
    {
        if (!Assert(_samples >= 0) || !Assert(_percentSamples >= 0)) return;
        if (!_fresh) Read();
        _fresh = false;
        if (double.IsFinite(_read)) (_valueSum, _samples) = (_valueSum + _read, _samples + 1);
        if (double.IsFinite(_readPercent))
            (_percentSum, _percentSamples) = (_percentSum + _readPercent, _percentSamples + 1);
    }

    // A reading from before the bench is no sample of it
    public void ResetBench() => (_valueSum, _percentSum, _samples, _percentSamples, _fresh) = (0, 0, 0, 0, false);

    // The bench's mean of the row, NaN (nothing) without one sample; a final row as it stands, it covers the whole bench already. The
    // label is read once: a log row changes on a pool thread.
    public string MeanText()
    {
        if (!Assert(_samples >= 0) || !Assert(_percentSamples >= 0)) return "";
        var label = Label();
        if (Kind != HudLineKind.Text) return Text(label, null, null);
        double mean = _samples > 0 ? _valueSum / _samples : double.NaN,
            share = _percentSamples > 0 ? _percentSum / _percentSamples : double.NaN;
        if (Final) (mean, share) = (Value?.Invoke() ?? double.NaN, Percent?.Invoke() ?? double.NaN);
        return Text(label, Value is null ? null : HudText.Format(mean, NumberFormat),
            Percent is null ? null : HudText.Format(share, "F1"));
    }

    private string Text(string label, string? value, string? percent) => Kind switch
    {
        HudLineKind.Header => $"[{label}]",
        HudLineKind.Title => $"{label} – {string.Join(", ", Badges.Select(b => b.Text))}",
        HudLineKind.Text when label.Length > 0 => RowText(label, Sub, value, percent, Unit),
        _ => ""
    };

    // One row of a dump or a bench report: "label: 12.5 % 3.20 ms". value and percent are the numbers as formatted, null for a row
    // without that column and "" for one that measured nothing. The panel draws what it has and leaves the rest blank, so the row
    // says what it has too; one with no number at all is left out instead of printing a bare "label:" or " %".
    internal static string RowText(string label, bool sub, string? value, string? percent, string unit)
    {
        if (!Assert(label.Length > 0) || !Assert(unit.Length <= MaxUnit)) return "";
        var text = (sub ? "  " : "") + label;
        if (value == null && percent == null) return text;
        bool hasValue = value is { Length: > 0 }, hasPercent = percent is { Length: > 0 };
        if (!hasValue && !hasPercent) return "";
        text += ":";
        if (hasPercent) text += $" {percent} %";
        if (hasValue) text += $" {value} {unit}";
        return text.TrimEnd();
    }

    public void Draw(HudCanvas canvas, HudFonts fonts, double x, double y, HudColumns columns)
    {
        if (!Assert(Height > 0) || !Assert(columns.RowWidth > 0) || !Assert(canvas.Width >= columns.RowWidth)) return;
        var h = Height;
        switch (Kind)
        {
            case HudLineKind.Header:
                canvas.Header(x, y, columns.RowWidth, h, fonts, _label);
                return;
            case HudLineKind.Rule:
                canvas.Rule(x, y, columns.RowWidth, h);
                return;
            case HudLineKind.Graph:
                if (NotNull(Graph)) canvas.Graph(x, y, columns.RowWidth, h, fonts, Graph);
                return;
            case HudLineKind.Title:
                canvas.Text(x, y, h, fonts.Title, _label);
                x += canvas.TextWidth(fonts.Title, _label) + scaled(TitleGap);
                foreach (var (text, color) in Badges.Bounded(MaxBadges))
                    x += scaled(BadgeGap) + canvas.Badge(x, y, h, fonts, text, color);
                return;
            case HudLineKind.Text:
                break;
            default:
                return;
        }

        var unitGap = scaled(UnitGap);
        canvas.Text(x + (Sub ? scaled(Indent) : 0), y, h, fonts.Text, _label, _color);
        if (Percent != null)
        {
            canvas.Bar(x + columns.BarX, y, h, HudCanvas.BarWidth, _fraction, _marker, good: Good);
            canvas.Text(x + columns.PercentRight - PercentWidth, y, h, fonts.Text, _percent);
            if (_percent.Length > 0) canvas.Text(x + columns.PercentRight + unitGap, y, h, fonts.Text, "%");
        }

        canvas.Text(x + columns.ValueRight - ValueWidth, y, h, fonts.Text, _value);
        if (_value.Length > 0) canvas.Text(x + columns.ValueRight + unitGap, y, h, fonts.Text, Unit);
    }
}

// A total's growth since this instance last read it. Each HUD row owns its instances, so a row covers the span since its own last
// read. NaN on the first read, across a pause of counting (Counting.Epoch) and when the total fell (a new world started from zero).
internal sealed class Growth(Func<double> total)
{
    private int _epoch = -1;
    private double _last = double.NaN;

    public double Next()
    {
        var now = total();
        var grown = _epoch == Counting.Epoch && now >= _last ? now - _last : double.NaN;
        (_last, _epoch) = (now, Counting.Epoch);
        return Finite(now) && Assert(_epoch >= 0) ? grown : double.NaN;
    }
}
