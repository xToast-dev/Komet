using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Komet.Rules;

// Four ways a translation breaks. A key the code asks for but a language file lacks renders as the bare key; a key only some files
// know leaves the others bare; a placeholder in a key that Value(), Bar(), Title() or Warn() renders stays literal, because those pass
// no arguments; a knob on a settings page needs "settings-" + its lower-case key, "settings-page-" + its page and "settings-" + its
// group. Keys built at runtime ("hud-pass-" + name) carry no literal and stay unchecked.
internal static class Lang
{
    // A lang key spelled out in full: one of the prefixes the mod uses, and no trailing dash, which would be a concatenation
    private static readonly Regex KeyLiteral = new(
        "^(?:hud|settings|verify|hotkey|cmd|time|update)-[a-z0-9-]*[a-z0-9]$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex
        PanelKey = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly string[] Renderers = ["Value", "Bar", "Section", "Title", "Warn"];

    public static void Register(CompilationStartAnalysisContext start, Sources sources)
    {
        var langs = LangFile.ReadAll(start.Options.AdditionalFiles, start.CancellationToken);
        start.RegisterSyntaxNodeAction(sources.Node(c => Literal(c, langs)), SyntaxKind.StringLiteralExpression,
            SyntaxKind.InterpolatedStringExpression);
        start.RegisterSyntaxNodeAction(sources.Node(c => Panel(c, langs)), SyntaxKind.InvocationExpression);
        start.RegisterOperationAction(sources.Operation(c => Knob(c, langs)), OperationKind.ObjectCreation,
            OperationKind.Invocation);
        start.RegisterAdditionalFileAction(c => Mismatch(c, langs));
    }

    private static void Literal(SyntaxNodeAnalysisContext context, LangFile[] langs)
    {
        var text = context.Node switch
        {
            LiteralExpressionSyntax literal => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax { Contents: { Count: 1 } contents } when
                contents[0] is InterpolatedStringTextSyntax only =>
                only.TextToken.ValueText,
            _ => ""
        };
        if (KeyLiteral.IsMatch(text) && Missing(langs, text) is { } missing)
            context.ReportDiagnostic(Diagnostic.Create(Rules.MissingKey, context.Node.GetLocation(), text, missing));
    }

    // panel.Value("fps", ...) renders "hud-fps"
    private static void Panel(SyntaxNodeAnalysisContext context, LangFile[] langs)
    {
        var call = (InvocationExpressionSyntax)context.Node;
        var name = call.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name,
            MemberBindingExpressionSyntax b => b.Name,
            _ => null
        };
        if (name is not IdentifierNameSyntax { Identifier.ValueText: var renderer } ||
            Array.IndexOf(Renderers, renderer) < 0) return;
        if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression) || !PanelKey.IsMatch(literal.Token.ValueText)) return;
        var key = "hud-" + literal.Token.ValueText;
        if (Missing(langs, key) is { } missing)
            context.ReportDiagnostic(Diagnostic.Create(Rules.MissingKey, literal.GetLocation(), key, missing));
        if (renderer == "Section") return;
        foreach (var lang in langs)
            if (lang.TryGet(key, out var text) && text.IndexOf('{') >= 0)
                context.ReportDiagnostic(Diagnostic.Create(Rules.Placeholder, literal.GetLocation(), key, renderer,
                    lang.Name, text));
    }

    // Knob rows: new("Key", "page", "group", ...) or a factory returning a Knob, Switch("Key", "page", "group", ...); a null page is not shown
    private static void Knob(OperationAnalysisContext context, LangFile[] langs)
    {
        var (type, arguments) = context.Operation switch
        {
            IObjectCreationOperation creation => (creation.Type, creation.Arguments),
            IInvocationOperation call => (call.TargetMethod.ReturnType, call.Arguments),
            _ => (null, [])
        };
        if (type is not { Name: "Knob" }) return;
        var key = Argument(arguments, "key");
        if (key?.ConstantValue is not { HasValue: true, Value: string knob } ||
            Argument(arguments, "page")?.ConstantValue is not { HasValue: true, Value: string page })
            return;
        var group = Argument(arguments, "group")?.ConstantValue is { HasValue: true, Value: string name }
            ? "settings-" + name
            : null;
        foreach (var lang in (string?[])["settings-" + knob.ToLowerInvariant(), "settings-page-" + page, group])
            if (lang is not null && Missing(langs, lang) is { } missing)
                context.ReportDiagnostic(Diagnostic.Create(Rules.KnobKey, key.Syntax.GetLocation(), knob, page, lang,
                    missing));
    }

    private static IOperation? Argument(ImmutableArray<IArgumentOperation> arguments, string parameter)
    {
        return arguments
            .FirstOrDefault(a => string.Equals(a.Parameter?.Name, parameter, StringComparison.OrdinalIgnoreCase))
            ?.Value;
    }

    private static void Mismatch(AdditionalFileAnalysisContext context, LangFile[] langs)
    {
        var file = Array.Find(langs, l => l.Path == context.AdditionalFile.Path);
        if (file is null) return;
        foreach (var key in file.Keys)
            if (Missing(langs, key) is { } missing)
                context.ReportDiagnostic(
                    Diagnostic.Create(Rules.LangMismatch, file.Where(key), key, file.Name, missing));
    }

    // The language files that lack the key, or null when all have it
    private static string? Missing(LangFile[] langs, string key)
    {
        if (langs.Length == 0) return "every language file: no assets/**/lang/*.json is passed as AdditionalFiles";
        var missing = langs.Where(l => !l.TryGet(key, out _)).Select(l => l.Name).ToArray();
        return missing.Length == 0 ? null : string.Join(", ", missing);
    }
}

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
