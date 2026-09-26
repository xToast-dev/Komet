using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Komet.Rules;

// A language file: a flat JSON object {"key": "text", ...}. The reader is tolerant, not a validator (a test parses the assets with
// Newtonsoft): it collects every string key with its string value and skips anything else up to the next separator. Like Newtonsoft,
// which the game reads the files with, it accepts // and /* */ comments and single-quoted strings.
internal sealed class LangFile
{
    private readonly Dictionary<string, (string Text, TextSpan Span)> _entries = new(StringComparer.Ordinal);

    private LangFile(string path, SourceText text)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Text = text;
    }

    public string Path { get; }
    public string Name { get; }
    public SourceText Text { get; }
    public IEnumerable<string> Keys => _entries.Keys;

    public bool TryGet(string key, out string text)
    {
        var found = _entries.TryGetValue(key, out var entry);
        text = entry.Text;
        return found;
    }

    public Location Where(string key)
    {
        var span = _entries[key].Span;
        return Location.Create(Path, span, Text.Lines.GetLinePositionSpan(span));
    }

    // The language files among the additional files, in ordinal path order
    public static LangFile[] ReadAll(ImmutableArray<AdditionalText> files, CancellationToken token)
    {
        var langs = new List<LangFile>();
        foreach (var file in files)
        {
            var path = file.Path.Replace('\\', '/');
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !path.Contains("/lang/") ||
                file.GetText(token) is not { } text) continue;
            var lang = new LangFile(file.Path, text);
            lang.Read(text.ToString());
            langs.Add(lang);
        }

        langs.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return [.. langs];
    }

    private void Read(string json)
    {
        var at = json.IndexOf('{') + 1;
        if (at == 0) return;
        var value = new StringBuilder();
        while (at < json.Length)
        {
            at = Skip(json, at);
            if (at >= json.Length || json[at] == '}') return;
            if (json[at] is not ('"' or '\''))
            {
                at = Next(json, at);
                continue;
            }

            var start = at;
            at = String(json, at, value);
            var key = value.ToString();
            var span = TextSpan.FromBounds(start, at);
            at = Skip(json, at);
            if (at >= json.Length || json[at] != ':') continue;
            at = Skip(json, at + 1);
            if (at >= json.Length || json[at] is not ('"' or '\''))
            {
                at = Next(json, at);
                continue;
            }

            at = String(json, at, value);
            if (!_entries.ContainsKey(key)) _entries[key] = (value.ToString(), span);
        }
    }

    // Past whitespace, separators and comments
    private static int Skip(string json, int at)
    {
        while (at < json.Length)
            if (char.IsWhiteSpace(json[at]) || json[at] == ',') at++;
            else if (Comment(json, at) is var end && end > at) at = end;
            else return at;

        return at;
    }

    // Past the comment starting at `at`, or `at` when none starts there; an unterminated block comment runs to the end
    private static int Comment(string json, int at)
    {
        if (at + 1 >= json.Length || json[at] != '/') return at;
        if (json[at + 1] == '/')
        {
            var line = json.IndexOf('\n', at + 2);
            return line < 0 ? json.Length : line + 1;
        }

        if (json[at + 1] != '*') return at;
        var close = json.IndexOf("*/", at + 2, StringComparison.Ordinal);
        return close < 0 ? json.Length : close + 2;
    }

    // Past the next ',' of this object outside strings, comments and nested values, or at its closing '}'
    private static int Next(string json, int at)
    {
        var (quote, depth) = ('\0', 0);
        while (at < json.Length)
        {
            if (quote == '\0' && Comment(json, at) is var end && end > at)
            {
                at = end;
                continue;
            }

            var c = json[at++];
            if (quote != '\0')
            {
                if (c == '\\') at++;
                else if (c == quote) quote = '\0';
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c is '{' or '[')
            {
                depth++;
            }
            else if (c is ']' || (c == '}' && depth > 0))
            {
                depth--;
            }
            else if (c == '}')
            {
                return at - 1;
            }
            else if (c == ',' && depth == 0)
            {
                return at;
            }
        }

        return at;
    }

    // Decodes the string starting at the quote at `at` into `value`; returns the index after the closing quote
    private static int String(string json, int at, StringBuilder value)
    {
        _ = value.Clear();
        var quote = json[at++];
        while (at < json.Length && json[at] != quote)
        {
            var c = json[at++];
            if (c != '\\' || at >= json.Length)
            {
                _ = value.Append(c);
                continue;
            }

            c = json[at++];
            if (c == 'u' && at + 4 <= json.Length &&
                int.TryParse(json.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                _ = value.Append((char)code);
                at += 4;
                continue;
            }

            _ = value.Append(c switch { 'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', _ => c });
        }

        return Math.Min(at + 1, json.Length);
    }
}
