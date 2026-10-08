namespace Komet.Hud;

internal enum HudLineKind
{
    Text,
    Header,
    Title
}

// One row of the HUD's report: the bench's means and the debug protocol's HUD section read every row at the end of each interval
internal sealed class HudLine
{
    public const int MaxUnit = 2;

    private double _valueSum, _percentSum;
    private bool _fresh;

    // The last Read(); the bench has not taken it yet while _fresh
    private double _read = double.NaN, _readPercent = double.NaN;

    // The windows each mean is over: a share can be missing where its time is not
    private int _samples, _percentSamples;

    public HudLineKind Kind { get; init; } = HudLineKind.Text;
    public Func<string> Label { get; init; } = static () => "";
    public bool Sub { get; init; }
    public Func<double>? Value { get; init; } // NaN = nothing measured
    public bool Final { get; init; } // already covers the whole bench, so the report takes it as it stands
    public string Unit { get; init; } = "";
    public Func<double>? Percent { get; init; } // 0..100
    public Func<string>? Badge { get; init; } // a title's

    private string NumberFormat => Unit switch { "ms" => "F2", "/s" => "F1", _ => "N0" };

    // A counter row's number is a Growth: it covers the span since the row was last read
    public void Read()
    {
        (_read, _readPercent, _fresh) = (Value?.Invoke() ?? double.NaN, Percent?.Invoke() ?? double.NaN, true);
        _ = Assert(Kind == HudLineKind.Text || (Value == null && Percent == null)); // only text rows carry numbers
    }

    // A new baseline for the row's Growths when counting starts
    public void Prime()
    {
        Read();
        _fresh = false;
        _ = Assert(!_fresh);
    }

    // A statistic without a window yet is not a sample, nor is a share of an empty one. Value and share are averaged over their own
    // windows: a mod's time is known in a window whose share is not (no frame time to divide by).
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
    public void ResetBench()
    {
        _ = Assert(_samples >= 0) && Assert(_percentSamples >= 0);
        (_valueSum, _percentSum, _samples, _percentSamples, _fresh) = (0, 0, 0, 0, false);
    }

    // The label is read once: a log row changes on a pool thread.
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
        HudLineKind.Title => Titled(label),
        HudLineKind.Text when label.Length > 0 => RowText(label, Sub, value, percent, Unit),
        _ => ""
    };

    private string Titled(string label) => Badge is { } badge && NotNull(label) ? $"{label} – {badge()}" : label;

    // One row of a bench report: "label: 12.5 % 3.20 ms". value and percent are the numbers as formatted, null for a row without that
    // column and "" for one that measured nothing; one with no number at all is left out instead of printing a bare "label:" or " %".
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
