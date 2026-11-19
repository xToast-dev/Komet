using System.Text.Json;
using System.Text.RegularExpressions;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// A problem on a line (0-based) at a column (1-based). Renderer names the one renderer it fails under ("" both).
// Inherited: a copy's warning the game's own file carries already (grey in the editor, not under PROBLEMS)
internal readonly record struct Diagnostic(
    int Line,
    int Column,
    string Message,
    bool Error,
    string Renderer = "",
    bool Inherited = false);

// The problems of the studio's files: the quick checks of the draft (a missing ';', braces, unused defines and uniforms,
// old GLSL names Vulkan does not take, broken JSON) plus what the last build reported. A file's checks run again only
// when its text changes; the shown file's lines map to its first problem for the editor.
internal sealed class ShaderDiagnostics
{
    private const int MaxIssues = 128, MaxFiles = 256;

    private static readonly Regex DefineRx =
        new(@"#define\s+(\w+)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex UniformRx =
        new(@"uniform\s+\w+\s+(\w+)\s*;",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex VulkanRx =
        new(@"\b(texture2D|gl_FragColor|gl_FragData)\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex CommentRx =
        new(@"//[^\n]*",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private readonly Dictionary<string, Diagnostic[]> _built = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (int Start, int Count, int First)> _byLine = []; // First: its own issue, -1 none

    private readonly Dictionary<string, (string Text, Diagnostic[] Issues)> _checked = new(StringComparer.Ordinal);

    private readonly Dictionary<string, (HashSet<(string, string)> Theirs, HashSet<string> Unchanged)> _vanilla =
        new(StringComparer.Ordinal);

    private (string Path, string Text, Diagnostic[]? Built, Diagnostic[] Issues)? _classified;
    private Diagnostic[] _shown = [];
    private string? _shownPath, _shownText;

    // Says whether something sets a uniform a file declares (the studio: game, Komet, pack, a mod); null: no checks
    public static System.Func<string, string, bool>? IsSet { get; set; }

    // The problems of a file's text, its build's after the checks
    private Diagnostic[] Of(string path, string text)
    {
        if (!_checked.TryGetValue(path, out var known) || (!ReferenceEquals(known.Text, text) && known.Text != text))
        {
            if (_checked.Count >= MaxFiles) _checked.Clear();

            known = (text, Inspect(path, text));
            _checked[path] = known;
        }

        // after a build its warnings stand for the local unused checks
        return _built.TryGetValue(path, out var built) && built.Length > 0
            ? [.. built, .. known.Issues.Where(d => d.Error || d.Renderer.Length > 0)]
            : known.Issues;
    }

    public static int Errors(Diagnostic[] issues)
    {
        var errors = 0;
        foreach (var issue in issues.Bounded(MaxIssues * 2))
        {
            errors += issue.Error ? 1 : 0;
        }

        return errors;
    }

    // The build's problems of the files it built (none: cleared)
    public void Built(IReadOnlyList<string> files, BuildIssue[] issues)
    {
        for (var i = 0; i < Math.Min(files.Count, MaxFiles); i++)
        {
            _ = _built.Remove(files[i]);
        }

        foreach (var issue in issues.Bounded(MaxIssues * 4))
        {
            var list = _built.TryGetValue(issue.File, out var known) ? known : [];
            _built[issue.File] =
            [
                .. list, new Diagnostic(issue.Line, issue.Column, issue.Message, issue.Error,
                    issue.Renderer)
            ];
        }

        _shownText = null;
    }

    // The file the editor shows: its lines to their problems (sorted by line) and their first own one, an error before
    // a warning. The same classified list as PROBLEMS: what the editor marks yellow or red is what the panel counts.
    public void Show(ShaderDocument? document)
    {
        if (document?.Path == _shownPath && ReferenceEquals(document?.Text, _shownText)) return;

        (_shownPath, _shownText) = (document?.Path, document?.Text);
        _shown = document is null || document.ReadOnly ? [] : Classified(document.Path, document.Text);
        _shown = [.. _shown.Take(MaxIssues).OrderBy(d => d.Line)];
        _byLine.Clear();
        for (var i = 0; i < Math.Min(_shown.Length, MaxIssues); i++)
        {
            var issue = _shown[i];
            var (start, count, first) = _byLine.TryGetValue(issue.Line, out var known) ? known : (i, 0, -1);
            var better = !issue.Inherited && (first < 0 || (issue.Error && !_shown[first].Error));
            _byLine[issue.Line] = (start, count + 1, better ? i : first);
        }
    }

    // The line's first own problem; false when it has none of its own (inherited ones only, or none)
    public bool At(int line, out Diagnostic issue)
    {
        var found = _byLine.TryGetValue(line, out var at) && at.First >= 0;
        issue = found ? _shown[at.First] : default;
        return found;
    }

    // Every problem of a line, inherited ones too
    public ReadOnlySpan<Diagnostic> On(int line) =>
        _byLine.TryGetValue(line, out var at) ? _shown.AsSpan(at.Start, at.Count) : [];

    // A file's problems with the copy's inherited warnings marked: a warning the unchanged vanilla line carries already
    // (the same message on the same text), or a build warning on a line vanilla has unchanged (the game's file is never
    // built here, so its build warnings have nothing to match). Errors are always the author's.
    public Diagnostic[] Classified(string path, string text)
    {
        // the editor and PROBLEMS ask for the same text on every keystroke: answered once
        if (_classified is { } last && last.Path == path && ReferenceEquals(last.Text, text) &&
            ReferenceEquals(last.Built, _built.GetValueOrDefault(path)))
        {
            return last.Issues;
        }

        var issues = Of(path, text);
        if (issues.Length == 0 || StudioLink.GameFileOf(path) is not { } game || Vanilla(game) is not { } vanilla)
        {
            return issues;
        }

        var (theirs, unchanged) = (vanilla.Theirs, vanilla.Unchanged);
        var built = _built.GetValueOrDefault(path) ?? [];
        var lines = text.Split('\n');
        var marked = new Diagnostic[Math.Min(issues.Length, MaxIssues * 4)];
        for (var i = 0; i < Math.Min(issues.Length, MaxIssues * 4); i++)
        {
            var issue = issues[i];
            var line = (uint)issue.Line < (uint)lines.Length ? lines[issue.Line].Trim() : null;
            var inherited = !issue.Error && line is not null && (theirs.Contains((issue.Message, line)) ||
                                                                 (Array.IndexOf(built, issue) >= 0 &&
                                                                  unchanged.Contains(line)));
            marked[i] = issue with { Inherited = inherited };
        }

        _classified = (path, text, _built.GetValueOrDefault(path), marked);
        return marked;
    }

    // The game's file's own warnings and code lines, read and inspected once per file (it never changes in a session)
    private (HashSet<(string, string)> Theirs, HashSet<string> Unchanged)? Vanilla(string game)
    {
        if (_vanilla.TryGetValue(game, out var known)) return known;

        if (!StudioLink.Read(game, out var vanilla, out _)) return null;

        var vanillaLines = vanilla.Split('\n');
        HashSet<(string, string)> theirs = [];
        foreach (var issue in Of(game, vanilla).Bounded(MaxIssues * 2))
        {
            if (!issue.Error && (uint)issue.Line < (uint)vanillaLines.Length)
            {
                _ = theirs.Add((issue.Message, vanillaLines[issue.Line].Trim()));
            }
        }

        // lines of real code only: a blank line or a lone brace says nothing about where a warning came from
        var unchanged = new HashSet<string>(vanillaLines.Select(l => l.Trim()).Where(l => l.Any(char.IsLetter)),
            StringComparer.Ordinal);
        if (_vanilla.Count >= MaxFiles) _vanilla.Clear();

        return _vanilla[game] = (theirs, unchanged);
    }

    private static Diagnostic[] Inspect(string path, string text)
    {
        if (ShaderPaths.IsJson(path)) return InspectJson(text);

        if (!ShaderPaths.IsStage(path)) return [];

        List<Diagnostic> issues = [];
        var lines = text.Split('\n');
        InspectLines(issues, lines);
        Unused(issues, text, DefineRx, "unused-define");
        Unused(issues, text, UniformRx, "unused-uniform");
        NotSet(issues, path, text);
        issues.Sort((a, b) => a.Line.CompareTo(b.Line));
        return [.. issues.Take(MaxIssues)];
    }

    private static void NotSet(List<Diagnostic> issues, string path, string text)
    {
        if (IsSet is not { } isSet) return;

        foreach (var match in UniformRx.Matches(text).Bounded(64))
        {
            var name = match.Groups[1].Value;
            if (!isSet(path, name))
            {
                var line = text.AsSpan(0, match.Index).Count('\n');
                var column = match.Groups[1].Index - text.LastIndexOf('\n', match.Index);
                issues.Add(new Diagnostic(line, column, L("uniform-not-set", name), false));
            }
        }
    }

    private static Diagnostic[] InspectJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return [];
        }
        catch (JsonException e)
        {
            var message = e.Message;
            var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
            return
            [
                new Diagnostic((int)(e.LineNumber ?? 0), (int)(e.BytePositionInLine ?? 0) + 1,
                    L("json-invalid") + ": " + (cut > 0 ? message[..cut] : message), true)
            ];
        }
    }

    // ';' missing at a line's end (not before a continuation, after an operator or else, in a block comment), braces
    // that do not pair, names Vulkan does not take
    private static void InspectLines(List<Diagnostic> issues, string[] lines)
    {
        var codes = Code(lines);
        var depth = 0;
        for (var i = 0; i < Math.Min(codes.Length, ShaderPaths.MaxSource) && issues.Count < MaxIssues; i++)
        {
            var code = codes[i];
            var trimmed = code.Trim();
            depth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');
            if (Unterminated(codes, i, trimmed))
            {
                issues.Add(new Diagnostic(i, code.Length - code.TrimStart().Length + 1, L("semicolon-expected"), true));
            }

            foreach (var m in VulkanRx.Matches(code).Bounded(8))
            {
                var hint = m.Value == "texture2D" ? L("vulkan-use-texture") : L("vulkan-use-out");
                issues.Add(new Diagnostic(i, m.Index + 1, L("vulkan-missing", m.Value, hint), false, "Vulkan"));
            }
        }

        if (depth != 0) issues.Add(new Diagnostic(Math.Max(0, lines.Length - 1), 1, L("braces-unmatched"), true));
    }

    // The lines without comments (line and block)
    private static string[] Code(string[] lines)
    {
        var codes = new string[lines.Length];
        var block = false;
        for (var i = 0; i < Math.Min(lines.Length, ShaderPaths.MaxSource); i++)
        {
            var code = lines[i];
            if (block)
            {
                var end = code.IndexOf("*/", StringComparison.Ordinal);
                (code, block) = end < 0 ? ("", true) : (code[(end + 2)..], false);
            }

            var open = code.IndexOf("/*", StringComparison.Ordinal);
            if (open >= 0)
            {
                block = code.IndexOf("*/", open + 2, StringComparison.Ordinal) < 0;
                code = code[..open];
            }

            var cut = code.IndexOf("//", StringComparison.Ordinal);
            codes[i] = cut < 0 ? code : code[..cut];
        }

        return codes;
    }

    private static bool Unterminated(string[] codes, int i, string trimmed)
    {
        if (trimmed.Length == 0 || trimmed[0] is '#' or '*' || trimmed[^1] is ';' or '{' or '}' or ')' or ',' or '+' or
                '-' or '*' or '/' or '=' or '&' or '|' or '?' or ':' or '<' or '>' or '(' or '[' ||
            trimmed.StartsWith("else", StringComparison.Ordinal) || trimmed == "do")
        {
            return false;
        }

        for (var j = 1; j < 64 && i + j < codes.Length; j++)
        {
            var next = codes[i + j].Trim();
            if (next.Length > 0)
            {
                // the statement goes on, or its ';' stands alone on the next line (rift.fsh, galaxy.fsh)
                return next[0] is not ('+' or '-' or '*' or '/' or '=' or '&' or '|' or '?' or ':' or '.' or ')' or '<'
                    or
                    '>' or '{' or ';' or ',' or ']');
            }
        }

        return true;
    }

    // A #define or uniform named once only (its declaration)
    private static void Unused(List<Diagnostic> issues, string text, Regex declared, string message)
    {
        var body = CommentRx.Replace(text, "");
        foreach (var m in declared.Matches(text).Bounded(64))
        {
            var name = m.Groups[1].Value;
            var uses = Regex.Count(body, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.None,
                TimeSpan.FromMilliseconds(50));
            if (uses < 2 && issues.Count < MaxIssues)
            {
                var line = text.AsSpan(0, m.Index).Count('\n');
                issues.Add(new Diagnostic(line, 1, L(message, name), false));
            }
        }
    }
}
