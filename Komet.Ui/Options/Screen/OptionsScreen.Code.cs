using System.Text.RegularExpressions;
using Komet.Hud;

namespace Komet.Options;

internal sealed partial class OptionsScreen
{
    internal static readonly Regex CodeTokens = new("""
                                                    (?<comment>//.*|<!--.*?-->|/\*.*?\*/)|(?<literal>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|(?<number>\b\d+(?:\.\d+)?[fF]?\b)|(?<keyword>\b(?:public|private|internal|static|sealed|override|void|var|if|else|return|new|using|null|true|false|try|catch|when|out|ref|float|int|bool|string|vec[234]|mat[234]|uniform|sampler2D|in|where)\b)
                                                    """, RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private void DrawCode(DocLine line, double x, double y, CairoFont font)
    {
        x += line.X;
        var start = 0;
        foreach (var token in CodeTokens.Matches(line.Text).Take(512).Bounded(512))
        {
            x = CodeSpan(line.Text[start..token.Index], x, y, line.H, font, null);
            x = CodeSpan(token.Value, x, y, line.H, font, TokenColor(token));
            start = token.Index + token.Length;
        }

        _ = CodeSpan(line.Text[start..], x, y, line.H, font, null);
    }

    internal static Rgba TokenColor(Match token)
    {
        if (token.Groups["comment"].Success) return new Rgba(0.63, 0.77, 0.65, 1);

        if (token.Groups["literal"].Success) return new Rgba(1, 0.77, 0.57, 1);

        if (token.Groups["number"].Success) return new Rgba(0.92, 0.78, 1, 1);

        return new Rgba(0.55, 0.79, 1, 1);
    }

    private double CodeSpan(string text, double x, double y, double height, CairoFont font, Rgba? color)
    {
        if (text.Length == 0) return x;

        _canvas.Text(x, y, height, font, text, color);
        return x + _canvas.TextWidth(font, text);
    }
}
