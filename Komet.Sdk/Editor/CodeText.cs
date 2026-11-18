using System.Globalization;
using Komet.Hud;
using Komet.Options;

namespace Komet.Sdk;

// Code drawn as the menu draws it: monospace, tabs as four spaces, tokens in the options screen's colours
internal static class CodeText
{
    private const int MaxGutters = 4096;
    private static readonly string?[] Gutters = new string?[MaxGutters];

    public static string Display(string text) =>
        text.Contains('\t', StringComparison.Ordinal) ? text.Replace("\t", "    ", StringComparison.Ordinal) : text;

    // A line number padded for the editor's gutter, kept so a redraw takes no new string
    public static string Gutter(int line)
    {
        if (line is < 0 or >= MaxGutters) return (line + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5) + "  ";

        return Gutters[line] ??= (line + 1).ToString(CultureInfo.InvariantCulture).PadLeft(5) + "  ";
    }

    public static double Width(HudUi ui, string text) => ui.TextW(ui.F.Mono, Display(text));

    // One line of code at the cursor, its gutter dim, the rest coloured by token; the cursor moves a row down
    public static void Line(HudUi ui, string text, string gutter = "")
    {
        var x = ui.X;
        var font = ui.F.Mono;
        var height = ui.F.BodyRow;
        Span(gutter, HudUi.Dim);
        var visible = ui.Fit(font, Display(text), Math.Max(1, ui.X + ui.W - x));
        var start = 0;
        foreach (var token in OptionsScreen.CodeTokens.Matches(visible).Bounded(512))
        {
            Span(visible[start..token.Index], null);
            Span(token.Value, OptionsScreen.TokenColor(token));
            start = token.Index + token.Length;
        }

        Span(visible[start..], null);
        ui.Y += height;

        void Span(string part, Rgba? color)
        {
            if (part.Length == 0) return;

            ui.Text(x, ui.Y, height, font, part, color);
            x += ui.TextW(font, part);
        }
    }
}
