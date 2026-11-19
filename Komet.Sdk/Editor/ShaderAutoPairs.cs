namespace Komet.Sdk;

internal static class ShaderAutoPairs
{
    internal static Edit? ForInput(string source, int anchor, int caret, string input)
    {
        if (input.Length != 1) return null;

        var start = Math.Min(anchor, caret);
        var end = Math.Max(anchor, caret);
        var typed = input[0];
        if (start == end && IsClosing(typed) && caret < source.Length && source[caret] == typed)
        {
            return new Edit(caret, caret, "", caret + 1, true);
        }

        var closing = typed switch
        {
            '(' => ')', '[' => ']', '{' => '}', '"' => '"', _ => '\0'
        };
        return closing == '\0'
            ? null
            : new Edit(start, end, typed + source[start..end] + closing, start + 1 + end - start, false);
    }

    private static bool IsClosing(char typed) => typed is ')' or ']' or '}' or '"';

    internal readonly record struct Edit(int Start, int End, string Text, int Caret, bool Skip);
}
