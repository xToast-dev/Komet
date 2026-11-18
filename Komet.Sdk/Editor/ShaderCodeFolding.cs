
namespace Komet.Sdk;

internal static class ShaderCodeFolding
{
    private const int MaxSource = 48 * 1024;

    internal static Dictionary<int, int> Build(string source)
    {
        var pairs = ShaderBracketMatcher.Build(source);
        Dictionary<int, int> ranges = [];
        foreach (var (open, close) in pairs.Bounded(MaxSource))
        {
            if (open >= close || source[open] != '{' || source[close] != '}') continue;

            var first = CountLines(source, open);
            var last = CountLines(source, close);
            if (last > first + 1 && (!ranges.TryGetValue(first, out var current) || last > current))
            {
                ranges[first] = last;
            }
        }

        return ranges;
    }

    private static int CountLines(string source, int end) => source.AsSpan(0, Math.Min(end, source.Length)).Count('\n');
}
