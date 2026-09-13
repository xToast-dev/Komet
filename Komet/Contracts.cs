using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet;

// Assertions are side effect free Boolean tests for conditions that must never occur. A failure is logged once per call site
// and handed back to the caller, which recovers explicitly (return, skip, fall back). The passing path costs one branch.
internal static class Contracts
{
    private static readonly HashSet<(string Member, int Line)> Reported = [];
    private static readonly Lock Gate = new();
    private static ILogger? _logger;

    public static void Attach(ILogger logger) => _logger = logger;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Assert(bool condition, [CallerArgumentExpression(nameof(condition))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (condition) return true;
        Report(expression, member, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NotNull<T>([NotNullWhen(true)] T? value, [CallerArgumentExpression(nameof(value))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0) where T : class
    {
        if (value is not null) return true;
        Report(expression + " is null", member, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Finite(double value, [CallerArgumentExpression(nameof(value))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (double.IsFinite(value)) return true;
        Report(expression + " is not finite", member, line);
        return false;
    }

    // 0 <= value < count
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Index(int value, int count, [CallerArgumentExpression(nameof(value))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if ((uint)value < (uint)count) return true;
        Report(expression + " out of range", member, line);
        return false;
    }

    // Bounded iteration: at most max items, reported when the source holds more. Lists and arrays enumerate as spans without allocating;
    // the IEnumerable version is an iterator and stays out of per-frame code (dictionaries there use a struct enumerator in a bounded for).
    public static ReadOnlySpan<T> Bounded<T>(this List<T> items, int max, [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (items.Count > max) Report(expression + " exceeds " + max.ToString(System.Globalization.CultureInfo.InvariantCulture), member, line);
        return CollectionsMarshal.AsSpan(items)[..Math.Min(items.Count, max)];
    }

    public static ReadOnlySpan<T> Bounded<T>(this T[] items, int max, [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        if (items.Length > max) Report(expression + " exceeds " + max.ToString(System.Globalization.CultureInfo.InvariantCulture), member, line);
        return items.AsSpan(0, Math.Min(items.Length, max));
    }

    public static IEnumerable<T> Bounded<T>(this IEnumerable<T> items, int max, [CallerArgumentExpression(nameof(items))] string expression = "", [CallerMemberName] string member = "", [CallerLineNumber] int line = 0)
    {
        using var e = items.GetEnumerator();
        for (var i = 0; i < max; i++)
        {
            if (!e.MoveNext()) yield break;
            yield return e.Current;
        }
        if (e.MoveNext()) Report(expression + " exceeds " + max.ToString(System.Globalization.CultureInfo.InvariantCulture), member, line);
    }

    private static void Report(string expression, string member, int line)
    {
        lock (Gate)
        {
            if (!Reported.Add((member, line))) return;
        }
        if (_logger is null) Console.Error.WriteLine($"Komet: assertion failed in {member} (line {line}): {expression}");
        else _logger.Error("Komet: assertion failed in {0} (line {1}): {2}", member, line, expression);
    }
}
