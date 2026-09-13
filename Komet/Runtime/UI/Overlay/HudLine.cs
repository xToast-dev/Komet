using Komet.Runtime.Diagnostics;

namespace Komet.Runtime.UI.Overlay;

internal enum HudLineKind { Text, Header, Rule, Title, Graph }

internal readonly record struct HudColumns(double RowWidth, double BarX, double PercentRight, double ValueRight);

internal sealed class HudLine
{
    public const double UnitGap = 3, BadgeGap = 6;
    public const int MaxBadges = 4;
    private const double Indent = 12, GraphWidth = 320, GraphHeight = 90;

    public HudLineKind Kind { get; init; } = HudLineKind.Text;
    public bool Detail { get; init; }
    public Func<string> Label { get; init; } = static () => "";
    public bool Sub { get; init; }
    public Func<double>? Value { get; init; }     // NaN = show nothing
    public string Unit { get; init; } = "";
    public Func<double>? Percent { get; init; }   // 0..100
    public Func<double>? Marker { get; init; }
    public Func<Rgba?>? Color { get; init; }   // label color, null = default
    public IReadOnlyList<(string Text, Rgba Color)> Badges { get; init; } = [];
    public FrameStats? Graph { get; init; }

    public double LabelWidth { get; private set; }
    public double Height { get; private set; }
    public double ValueWidth { get; private set; }
    public double PercentWidth { get; private set; }
    public bool Empty => Kind == HudLineKind.Text && _label.Length == 0;   // dynamic lists leave unused rows blank

    private string _label = "", _value = "", _percent = "";
    private double _fraction, _marker, _valueSum, _percentSum;
    private Rgba? _color;
    private int _samples;
    private string NumberFormat => Unit switch { "ms" => "F2", "/s" => "F1", _ => "N0" };

    public void Measure(HudSettings settings)
    {
        _label = Label();
        _color = Color?.Invoke();
        _value = _percent = "";
        ValueWidth = PercentWidth = 0;
        if (!Assert(Kind != HudLineKind.Graph || Graph != null)) { Height = 0; return; }
        var font = Kind switch { HudLineKind.Title => settings.Title, HudLineKind.Header => settings.Header, _ => settings.Text };
        Height = Kind switch
        {
            HudLineKind.Text   => HudCanvas.LineHeight(font),
            HudLineKind.Header => HudCanvas.HeaderHeight(font),
            HudLineKind.Rule   => HudCanvas.RuleHeight,
            HudLineKind.Title  => Math.Max(HudCanvas.LineHeight(font), HudCanvas.BadgeHeight(settings.Header)),
            _                  => scaled(GraphHeight),
        };
        if (!Assert(Height > 0)) { Height = 0; return; }
        var indent = Sub ? scaled(Indent) : 0;
        LabelWidth = Kind == HudLineKind.Graph ? scaled(GraphWidth) : indent + HudCanvas.TextWidth(font, _label);
        foreach (var (text, _) in Badges.Bounded(MaxBadges)) LabelWidth += scaled(BadgeGap) + HudCanvas.BadgeWidth(settings.Header, text);
        if (Kind != HudLineKind.Text || !Assert(LabelWidth >= 0)) return;

        if (Value != null)
        {
            _value = HudSettings.Format(Value(), NumberFormat);
            ValueWidth = HudCanvas.TextWidth(font, _value);
        }
        if (Percent == null) return;
        var percent = Percent();
        _fraction = Math.Clamp(percent / 100, 0, 1);
        _marker = Marker == null ? 0 : Math.Clamp(Marker() / 100, 0, 1);
        _percent = HudSettings.Format(percent, "F1");
        PercentWidth = HudCanvas.TextWidth(font, _percent);
    }

    public void Accumulate()
    {
        if (Value != null) _valueSum += Value();
        if (Percent != null) _percentSum += Percent();
        _samples++;
    }

    public void ResetBench() => (_valueSum, _percentSum, _samples) = (0, 0, 0);

    public string ToText(bool mean) => Kind switch
    {
        HudLineKind.Header => $"[{Label()}]",
        HudLineKind.Title => $"{Label()} – {string.Join(", ", Badges.Select(b => b.Text))}",
        HudLineKind.Text when Label().Length > 0 => Row(mean),
        _ => "",
    };

    private string Row(bool mean)
    {
        var text = (Sub ? "  " : "") + Label();
        if (Value == null && Percent == null) return text;
        if (!Assert(!mean || _samples > 0)) return "";
        var value = 0.0;
        if (Value != null) value = mean ? _valueSum / _samples : Value();
        if (double.IsNaN(value)) return "";
        text += ":";
        if (Percent != null) text += $" {HudSettings.Format(mean ? _percentSum / _samples : Percent(), "F1")} %";
        if (Value != null) text += $" {HudSettings.Format(value, NumberFormat)} {Unit}";
        return text.TrimEnd();
    }

    public void Draw(HudCanvas canvas, HudSettings settings, double x, double y, HudColumns columns)
    {
        if (!Assert(Height > 0) || !Assert(columns.RowWidth > 0) || !Assert(canvas.Width >= columns.RowWidth)) return;
        var h = Height;
        switch (Kind)
        {
            case HudLineKind.Text:
                var unitGap = scaled(UnitGap);
                canvas.Text(x + (Sub ? scaled(Indent) : 0), y, h, settings.Text, _label, _color);
                if (Percent != null)
                {
                    canvas.Bar(x + columns.BarX, y, h, HudCanvas.BarWidth, _fraction, _marker);
                    canvas.Text(x + columns.PercentRight - PercentWidth, y, h, settings.Text, _percent);
                    canvas.Text(x + columns.PercentRight + unitGap, y, h, settings.Text, "%");
                }
                canvas.Text(x + columns.ValueRight - ValueWidth, y, h, settings.Text, _value);
                if (_value.Length > 0) canvas.Text(x + columns.ValueRight + unitGap, y, h, settings.Text, Unit);
                break;
            case HudLineKind.Header:
                canvas.Header(x, y, columns.RowWidth, h, settings.Header, _label);
                break;
            case HudLineKind.Rule:
                canvas.Rule(x, y, columns.RowWidth, h);
                break;
            case HudLineKind.Title:
                canvas.Text(x, y, h, settings.Title, _label);
                x += HudCanvas.TextWidth(settings.Title, _label);
                foreach (var (text, color) in Badges.Bounded(MaxBadges)) x += scaled(BadgeGap) + canvas.Badge(x, y, h, settings.Header, text, color);
                break;
            case HudLineKind.Graph:
                if (NotNull(Graph)) canvas.Graph(x, y, columns.RowWidth, h, settings.Text, Graph);
                break;
        }
    }
}
