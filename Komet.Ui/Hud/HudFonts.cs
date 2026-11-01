using Cairo;

namespace Komet.Hud;

// HUD fonts at fixed pixel sizes times the font scale (CairoFont applies the GUI scale)
internal sealed class HudUiFonts
{
    private const double Unit = 1.15; // a pixel size to the game's unscaled font size
    private double _scale = double.NaN;
    private int _gui;

    private static readonly string Face = CairoFont.WhiteDetailText().Fontname;
    private string _face = "", _strong = "";

    // The menu style's families (HudUi.Restyle); the game's font when it names none
    public static string Family { get; set; } = Face;
    public static string? StrongFamily { get; set; }

    public CairoFont Small { get; } = new(11, Face, [1, 1, 1, 1], null);
    public CairoFont Body { get; } = new(12, Face, [1, 1, 1, 1], null);
    public CairoFont Strong { get; } = new(13, Face, [1, 1, 1, 1], null) { FontWeight = FontWeight.Bold };
    public CairoFont Stat { get; } = new(15, Face, [1, 1, 1, 1], null);
    public CairoFont Big { get; } = new(17, Face, [1, 1, 1, 1], null);
    public CairoFont Huge { get; } = new(20, Face, [1, 1, 1, 1], null);
    public CairoFont Mono { get; } = new(11, "monospace", [0.92, 0.92, 0.92, 1], null);
    public double Scale { get; private set; } = 1;
    public int Epoch { get; private set; }
    public double SmallRow { get; private set; }
    public double BodyRow { get; private set; }
    public double StrongRow { get; private set; }
    public double StatRow { get; private set; }
    public double BigRow { get; private set; }
    public double HugeRow { get; private set; }
    public double MonoRow { get; private set; }

    public void Update(double fontScale)
    {
        var gui = BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale);
        var (face, strong) = (Family, StrongFamily ?? Family);
        if (Math.Abs(fontScale - _scale) < 1e-9 && gui == _gui && face == _face && strong == _strong) return;
        (_face, _strong) = (face, strong);
        (Small.Fontname, Body.Fontname, Stat.Fontname, Big.Fontname, Huge.Fontname) = (face, face, strong, strong, strong);
        (Strong.Fontname, Strong.FontWeight) = (strong, StrongFamily is null ? FontWeight.Bold : FontWeight.Normal);
        (_scale, _gui, Scale) = (fontScale, gui, Assert(fontScale is > 0.3 and < 3) ? fontScale : 1);
        foreach (var (font, px) in (ReadOnlySpan<(CairoFont, double)>)[(Small, 11), (Body, 12), (Strong, 13), (Stat, 15), (Big, 17),
                     (Huge, 20), (Mono, 11)])
            font.UnscaledFontsize = px * Unit * Scale;
        (SmallRow, BodyRow, StrongRow, StatRow) = (Row(Small), Row(Body), Row(Strong), Row(Stat));
        (BigRow, HugeRow, MonoRow) = (Row(Big) * 0.95, Row(Huge) * 0.92, Row(Mono) * 1.05);
        Epoch++;
    }

    private static double Row(CairoFont font) => font.GetFontExtents().Height * 1.12;
}

// The options screen's fonts in a style's families, rebuilt on font or GUI scale change (GUIScale has no event). Main
// thread: CairoFont measures on the engine's context.
internal sealed class HudFonts
{
    private const double BaseFontSize = 14, BaseTitleSize = 16, HeaderPad = 2, BadgePad = 3;
    private int _gui;
    private long _scale = BitConverter.DoubleToInt64Bits(double.NaN);

    public CairoFont Text { get; } = CairoFont.WhiteDetailText().WithLineHeightMultiplier(0.9);
    public CairoFont Header { get; } = CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold);
    public CairoFont Title { get; } = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);

    // The options screen's larger and smaller faces: labels, page titles, a value shown big
    public CairoFont Small { get; } = CairoFont.WhiteDetailText();
    public CairoFont Big { get; } = CairoFont.WhiteSmallText();
    public CairoFont Huge { get; } = CairoFont.WhiteSmallText();

    // Code in the in-game docs: monospace whatever the style's families
    public CairoFont Mono { get; } = new(12, "monospace", [0.92, 0.92, 0.92, 1], null);
    public int Epoch { get; private set; }

    // A style's families, null for the game's; a named heading family brings its own weight. Sizes follow at Update.
    public void Families(string? text, string? heading, string? title, string? numbers)
    {
        var standard = GuiStyle.StandardFontName;
        var (t, h, ti) = (text ?? standard, heading ?? text ?? standard, title ?? heading ?? text ?? standard);
        var n = numbers ?? ti;
        if (!NotNull(t) || (Text.Fontname == t && Header.Fontname == h && Title.Fontname == ti && Huge.Fontname == n)) return;
        (Text.Fontname, Small.Fontname, Header.Fontname, Title.Fontname, Big.Fontname, Huge.Fontname) = (t, t, h, ti, ti, n);
        var weight = heading is null ? FontWeight.Bold : FontWeight.Normal;
        (Header.FontWeight, Title.FontWeight, Big.FontWeight, Huge.FontWeight) = (weight, weight, weight, weight);
        _scale = BitConverter.DoubleToInt64Bits(double.NaN); // measured again
    }

    // The colour text takes when it names none
    public void Colored(Rgba color)
    {
        if (!Finite(color.A) || !Assert(color.A is >= 0 and <= 1)) return;
        double[] rgba = [color.R, color.G, color.B, color.A];
        foreach (var font in new[] { Text, Small, Header, Title, Big, Huge, Mono }.Bounded(7)) font.Color = rgba;
    }

    public double TextRow { get; private set; }
    public double HeaderRow { get; private set; }
    public double BadgeRow { get; private set; }
    public double MonoRow { get; private set; }

    public void Update(double fontScale)
    {
        var (scale, gui) = (BitConverter.DoubleToInt64Bits(fontScale),
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (scale == _scale && gui == _gui) return;
        (_scale, _gui) = (scale, gui);
        if (!Assert(HudSettings.ScaleRange.Contains(fontScale))) fontScale = 1;
        Text.UnscaledFontsize = Header.UnscaledFontsize = BaseFontSize * fontScale;
        Title.UnscaledFontsize = BaseTitleSize * fontScale;
        (Small.UnscaledFontsize, Big.UnscaledFontsize, Huge.UnscaledFontsize) = (11.5 * fontScale, 22 * fontScale, 28 * fontScale);
        Mono.UnscaledFontsize = 12.5 * fontScale;
        MonoRow = Height(Mono);
        var header = Height(Header);
        (TextRow, HeaderRow, BadgeRow) = (Height(Text), header + 2 * scaled(HeaderPad), header + 2 * scaled(BadgePad));
        Epoch++;
    }

    private static double Height(CairoFont font) => font.GetFontExtents().Height * font.LineHeightMultiplier;
}
