using Cairo;

namespace Komet.Hud;

// The HUD's three fonts at the settings' font scale and the row heights measured from them. Rebuilt when the font scale or the game's
// GUI scale changed (RuntimeEnv.GUIScale changes without an event, and CairoFont scales by it); Epoch counts the rebuilds, so panels
// and dialogs know their measurements are stale. Main thread: CairoFont measures on the engine's shared context.
internal sealed class HudFonts
{
    private const double BaseFontSize = 14, BaseTitleSize = 16, HeaderPad = 2, BadgePad = 3, RuleH = 9;
    private int _gui;
    private long _scale = BitConverter.DoubleToInt64Bits(double.NaN);

    public CairoFont Text { get; } = CairoFont.WhiteDetailText().WithLineHeightMultiplier(0.9);
    public CairoFont Header { get; } = CairoFont.WhiteDetailText().WithWeight(FontWeight.Bold);
    public CairoFont Title { get; } = CairoFont.WhiteSmallText().WithWeight(FontWeight.Bold);
    public int Epoch { get; private set; }
    public double TextRow { get; private set; }
    public double HeaderRow { get; private set; } // a section header: the Header font on its band
    public double BadgeRow { get; private set; } // a badge: the Header font in its box
    public double TitleRow { get; private set; } // the Title font beside its badges
    public double RuleRow { get; private set; }
    public static double BadgePadding => 2 * scaled(BadgePad);
    public static double HeaderPadding => scaled(HeaderPad);

    // True when it rebuilt; checked once per measure pass and per dialog frame
    public bool Update(double fontScale)
    {
        var (scale, gui) = (BitConverter.DoubleToInt64Bits(fontScale),
            BitConverter.SingleToInt32Bits(RuntimeEnv.GUIScale));
        if (scale == _scale && gui == _gui) return false;
        (_scale, _gui) = (scale, gui);
        if (!Assert(HudSettings.ScaleRange.Contains(fontScale))) fontScale = 1;
        Text.UnscaledFontsize = Header.UnscaledFontsize = BaseFontSize * fontScale;
        Title.UnscaledFontsize = BaseTitleSize * fontScale;
        var header = Height(Header);
        (TextRow, HeaderRow, BadgeRow, RuleRow) =
            (Height(Text), header + 2 * HeaderPadding, header + BadgePadding, scaled(RuleH));
        TitleRow = Math.Max(Height(Title), BadgeRow);
        Epoch++;
        return Assert(TextRow > 0 && TitleRow > 0);
    }

    private static double Height(CairoFont font)
    {
        return Assert(font.UnscaledFontsize > 0) ? font.GetFontExtents().Height * font.LineHeightMultiplier : 0;
    }
}
