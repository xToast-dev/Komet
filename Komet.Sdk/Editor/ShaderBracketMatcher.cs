namespace Komet.Sdk;

internal static class ShaderBracketMatcher
{
    private const int MaxDepth = 4096, MaxSource = 1 << 20;

    internal static Dictionary<int, int> Build(string source)
    {
        Dictionary<int, int> pairs = [];
        Stack<(char Value, int Index)> stack = new(MaxDepth);
        var state = LexicalState.Code;
        var escaped = false;
        for (var i = 0; i < Math.Min(source.Length, MaxSource); i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';
            if (state == LexicalState.LineComment)
            {
                state = current == '\n' ? LexicalState.Code : state;
            }
            else if (state == LexicalState.BlockComment)
            {
                if (current == '*' && next == '/') state = LexicalState.Code;
            }
            else if (state is LexicalState.String or LexicalState.Character)
            {
                state = StringState(current, state, ref escaped);
            }
            else
            {
                state = CodeState(current, next, i, stack, pairs, ref escaped);
            }
        }

        return pairs;
    }

    private static LexicalState CodeState(char current, char next, int index,
        Stack<(char Value, int Index)> stack, Dictionary<int, int> pairs, ref bool escaped)
    {
        if (current == '/' && next == '/') return LexicalState.LineComment;

        if (current == '/' && next == '*') return LexicalState.BlockComment;

        if (current == '"')
        {
            escaped = false;
            return LexicalState.String;
        }

        if (current == '\'')
        {
            escaped = false;
            return LexicalState.Character;
        }

        if (IsOpen(current))
        {
            if (stack.Count >= MaxDepth) stack.Clear();

            stack.Push((current, index));
        }
        else if (stack.Count > 0 && Matches(stack.Peek().Value, current))
        {
            var open = stack.Pop();
            pairs[open.Index] = index;
            pairs[index] = open.Index;
        }

        return LexicalState.Code;
    }

    private static LexicalState StringState(char current, LexicalState state, ref bool escaped)
    {
        if (current == '\n')
        {
            escaped = false;
            return LexicalState.Code;
        }

        if (escaped)
        {
            escaped = false;
        }
        else if (current == '\\')
        {
            escaped = true;
        }
        else if ((state == LexicalState.String && current == '"') ||
                 (state == LexicalState.Character && current == '\''))
        {
            return LexicalState.Code;
        }

        return state;
    }

    private static bool IsOpen(char value) => value is '(' or '[' or '{';

    private static bool Matches(char open, char close) => (open, close) is ('(', ')') or ('[', ']') or ('{', '}');

    private enum LexicalState { Code, LineComment, BlockComment, String, Character }
}
