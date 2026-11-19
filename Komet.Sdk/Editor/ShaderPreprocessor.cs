using System.Globalization;

namespace Komet.Sdk;

// Which lines of a shader the preprocessor drops with the engine's defines (and the file's own #define/#undef): #if,
// #ifdef, #ifndef, #elif, #else, #endif. Conservative: a condition it cannot evaluate (an unknown name, a macro with
// arguments) leaves every branch of that block shown as active. Expressions: integers, names, defined(X), ! ~ unary -,
// * / % + - < > <= >= == != && || and parentheses, evaluated with explicit stacks (no recursion).
internal static class ShaderPreprocessor
{
    internal const int MaxTokens = 256;
    private const int MaxLines = 8192, MaxDepth = 32;

    public static bool[] Inactive(IReadOnlyList<string> lines, IReadOnlyList<(string Name, string Value)> engine)
    {
        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(engine.Count, 256); i++)
        {
            defines[engine[i].Name] = engine[i].Value;
        }

        var result = new bool[lines.Count];
        var stack = new Block[MaxDepth];
        var depth = 0;
        for (var i = 0; i < Math.Min(lines.Count, MaxLines); i++)
        {
            var active = depth == 0 || stack[depth - 1].Active;
            var line = lines[i].TrimStart();
            if (!line.StartsWith('#'))
            {
                result[i] = !active;
                continue;
            }

            result[i] = !active && !IsBranch(line);
            depth = Directive(line, defines, stack, depth, active);
        }

        return result;
    }

    private static bool IsBranch(string line)
    {
        var word = Word(line);
        return word is "elif" or "else" or "endif";
    }

    private static string Word(string line)
    {
        var rest = line[1..].TrimStart();
        var end = 0;
        for (var i = 0; i < Math.Min(rest.Length, 16) && char.IsLetter(rest[i]); i++)
        {
            end = i + 1;
        }

        return rest[..end];
    }

    private static int Directive(string line, Dictionary<string, string> defines, Block[] stack, int depth, bool active)
    {
        var word = Word(line);
        var rest = line[(line.IndexOf(word, StringComparison.Ordinal) + word.Length)..].Trim();
        var comment = rest.IndexOf("//", StringComparison.Ordinal);
        rest = comment >= 0 ? rest[..comment].Trim() : rest;
        switch (word)
        {
            case "if" or "ifdef" or "ifndef" when depth < MaxDepth:
                var value = word == "if" ? Evaluate(rest, defines) : Defined(defines, rest, word == "ifdef");
                stack[depth] = new Block
                {
                    Parent = active, Unknown = value is null, Taken = value is not 0,
                    Active = active && (value is null || value != 0)
                };
                return depth + 1;
            case "elif" when depth > 0:
                Elif(ref stack[depth - 1], Evaluate(rest, defines));
                return depth;
            case "else" when depth > 0:
                ref var b = ref stack[depth - 1];
                b.Active = b.Parent && (b.Unknown || !b.Taken);
                b.Taken = true;
                return depth;
            case "endif" when depth > 0:
                return depth - 1;
            case "define" when active:
                Define(rest, defines);
                return depth;
            case "undef" when active:
                _ = defines.Remove(rest);
                return depth;
            default:
                return depth;
        }
    }

    private static long Defined(Dictionary<string, string> defines, string name, bool wanted) =>
        defines.ContainsKey(name) == wanted ? 1 : 0;

    private static void Elif(ref Block b, long? value)
    {
        b.Unknown |= value is null;
        b.Active = b.Parent && (b.Unknown || (!b.Taken && value != 0));
        b.Taken |= value is not 0;
    }

    private static void Define(string rest, Dictionary<string, string> defines)
    {
        var space = rest.IndexOfAny([' ', '\t']);
        var name = space < 0 ? rest : rest[..space];
        if (name.Contains('(', StringComparison.Ordinal))
        {
            return; // a function-like macro: not followed
        }

        defines[name] = space < 0 ? "1" : rest[(space + 1)..].Trim();
    }

    // The condition's value, null when it names something unknown or does not parse
    public static long? Evaluate(string expression, IReadOnlyDictionary<string, string> defines)
    {
        var tokens = Tokens(expression, defines);
        return tokens is null ? null : Calculator.Run(tokens);
    }

    // Numbers and operators; names resolve to their define's number (defined(X) to 0/1); null when one cannot
    private static List<string>? Tokens(string text, IReadOnlyDictionary<string, string> defines)
    {
        var tokens = new List<string>();
        var i = 0;
        for (var guard = 0; guard < MaxTokens && i < text.Length; guard++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            var (token, next) = char.IsLetter(c) || c == '_' ? Name(text, i, defines) : Operator(text, i);
            if (token is null) return null;

            tokens.Add(token);
            i = next;
        }

        return i >= text.Length ? tokens : null;
    }

    private static (string? Token, int Next) Name(string text, int i, IReadOnlyDictionary<string, string> defines)
    {
        var end = Ident(text, i);
        var name = text[i..end];
        if (name != "defined")
        {
            return defines.TryGetValue(name, out var value) && long.TryParse(value.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var number)
                ? (number.ToString(CultureInfo.InvariantCulture), end)
                : (null, end);
        }

        var rest = text[end..].TrimStart();
        var paren = rest.StartsWith('(');
        var skipped = text.Length - rest.Length + (paren ? 1 : 0);
        var inner = text[skipped..].TrimStart();
        var start = text.Length - inner.Length;
        var after = Ident(text, start);
        var close = paren ? text.IndexOf(')', after) : after - 1;
        if (close < 0) return (null, text.Length);

        return (defines.ContainsKey(text[start..after]) ? "1" : "0", close + 1);
    }

    private static int Ident(string text, int i)
    {
        var end = i;
        for (var n = 0; n < Math.Min(text.Length - i, 128); n++)
        {
            if (!char.IsLetterOrDigit(text[i + n]) && text[i + n] != '_') break;

            end = i + n + 1;
        }

        return end;
    }

    private static (string? Token, int Next) Operator(string text, int i)
    {
        if (char.IsDigit(text[i]))
        {
            var end = Ident(text, i);
            var digits = text[i..end].TrimEnd('u', 'U', 'l', 'L');
            return long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? (digits, end)
                : (null, end);
        }

        var two = i + 1 < text.Length ? text.Substring(i, 2) : "";
        if (two is "&&" or "||" or "==" or "!=" or "<=" or ">=") return (two, i + 2);

        return "!~*/%+-<>()".Contains(text[i], StringComparison.Ordinal) ? (text[i].ToString(), i + 1) : (null, i + 1);
    }

    private struct Block
    {
        public bool Parent, Taken, Active, Unknown;
    }
}
