using System.Globalization;
using System.Text;
using Komet.Core;

namespace Komet.Hud;

// Protocol text in one language regardless of the game's (Lang.GetL); labels pad to a column, numbers are invariant.
internal sealed class DebugText(string code)
{
    public const int LabelWidth = 30, MaxWidth = 100;
    private const int MaxKeys = 16;
    private const char KeyMark = '\u0001', ArgMark = '\u0002';
    private readonly StringBuilder _text = new();

    public string T(string key, params object[] args)
    {
        var full = "komet:" + key;
        var text = Lang.GetL(code, full, args);
        return Assert(text != full) ? text : key;
    }

    public DebugText Head(string line)
    {
        _ = _text.Append(line).Append('\n');
        return this;
    }

    public DebugText Section(int number, string key)
    {
        var title = $"[{number}] {T(key).ToUpperInvariant()} ";
        _ = _text.Append('\n').Append(title).Append('=', Math.Max(4, MaxWidth - title.Length)).Append('\n');
        return this;
    }

    public DebugText Row(string key, string value)
    {
        if (!NotNull(value) || value.Length == 0) return this;
        var label = T(key);
        _ = _text.Append("  ").Append(label).Append(' ', Math.Max(1, LabelWidth - label.Length)).Append(Keys(value)).Append('\n');
        return this;
    }

    public DebugText Row(string key, double value, string format, string unit = "")
    {
        if (!double.IsFinite(value)) return this;
        return Row(key, unit.Length > 0 ? Num(value, format) + " " + unit : Num(value, format));
    }

    // Row translates it, so a language switch redoes it
    public static string Key(string key, params object[] args)
    {
        return args.Length == 0 ? $"{KeyMark}{key}{KeyMark}" : $"{KeyMark}{key}{ArgMark}{string.Join(ArgMark, args)}{KeyMark}";
    }

    private string Keys(string value)
    {
        if (!value.Contains(KeyMark, StringComparison.Ordinal)) return value;
        var parts = value.Split(KeyMark); // odd ones are keys
        for (var i = 1; i < Math.Min(parts.Length, 2 * MaxKeys); i += 2)
            if (parts[i].Split(ArgMark) is [var key, .. var args] && key.Length > 0) parts[i] = T(key, args);
        return string.Concat(parts);
    }

    public DebugText Pair(string label, string value, int indent = 2)
    {
        if (!NotNull(label)) return this;
        _ = _text.Append(' ', indent).Append(label).Append(' ', Math.Max(1, LabelWidth + 2 - indent - label.Length))
            .Append(value).Append('\n');
        return this;
    }

    public DebugText Line(string text, int indent = 2)
    {
        if (!NotNull(text)) return this;
        _ = _text.Append(' ', indent).Append(text).Append('\n');
        return this;
    }

    public DebugText Blank()
    {
        _ = _text.Append('\n');
        return this;
    }

    public static string Num(double value, string format) => KometText.Format(value, format, "-");

    public static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => _text.ToString();
}
