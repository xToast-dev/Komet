
namespace Komet.Sdk;

internal static class ShaderSymbolNavigator
{
    private const int MaxSource = 48 * 1024, MaxSymbol = 128;
    private static readonly string[] DeclarationWords = ["uniform", "attribute", "varying", "const", "in", "out"];

    internal static bool TryFindDefinition(string source, string symbol, out int offset)
    {
        offset = -1;
        if (symbol.Length is 0 or > MaxSymbol || source.Length > MaxSource) return false;

        for (var at = 0; at <= source.Length - symbol.Length && at < MaxSource; at++)
        {
            if (!Match(source, symbol, at) || !BoundedIdentifier(source, at, symbol.Length)) continue;

            var start = at == 0 ? 0 : source.LastIndexOf('\n', at - 1) + 1;
            var end = source.IndexOf('\n', at);
            end = end < 0 ? source.Length : end;
            if (Declaration(source.AsSpan(start, end - start), at - start, symbol.Length))
            {
                offset = at;
                return true;
            }

        }

        return false;
    }

    private static bool Match(string source, string symbol, int at) =>
        source.AsSpan(at, symbol.Length).SequenceEqual(symbol.AsSpan());

    private static bool BoundedIdentifier(string source, int at, int length)
    {
        var before = at == 0 || !Identifier(source[at - 1]);
        var afterAt = at + length;
        var after = afterAt == source.Length || !Identifier(source[afterAt]);
        return before && after;
    }

    private static bool Declaration(ReadOnlySpan<char> line, int at, int length)
    {
        var prefix = line[..at].TrimEnd();
        var suffix = line[(at + length)..].TrimStart();
        if (prefix.Length == 0 || prefix.Contains(';') || prefix.Contains('.') ||
            prefix.StartsWith("//", StringComparison.Ordinal) || prefix.StartsWith("/*", StringComparison.Ordinal) ||
            prefix.StartsWith("*", StringComparison.Ordinal))
        {
            return false;
        }

        if (suffix.StartsWith("(", StringComparison.Ordinal))
        {
            return !prefix.Contains('=') && !prefix.Contains('(') &&
                   !prefix.EndsWith("return", StringComparison.Ordinal) &&
                   !prefix.EndsWith("discard", StringComparison.Ordinal);
        }

        var lastAssignment = prefix.LastIndexOf('=');
        foreach (var word in DeclarationWords.Bounded(DeclarationWords.Length))
        {
            var keyword = WordIndex(prefix, word);
            if (keyword > lastAssignment) return true;
        }

        return lastAssignment < 0 && prefix.StartsWith("#define", StringComparison.Ordinal);
    }

    private static int WordIndex(ReadOnlySpan<char> source, string word)
    {
        for (var at = 0; at <= source.Length - word.Length && at < MaxSource; at++)
        {
            if (!source.Slice(at, word.Length).SequenceEqual(word.AsSpan())) continue;

            var before = at == 0 || !Identifier(source[at - 1]);
            var afterAt = at + word.Length;
            var after = afterAt == source.Length || !Identifier(source[afterAt]);
            if (before && after) return at;
        }

        return -1;
    }

    private static bool Identifier(char value) => char.IsAsciiLetterOrDigit(value) || value == '_';
}
