using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet;

// Checks for must-never-happen conditions: logged once per call site, returned for explicit recovery; one branch.
internal static class Contracts
{
    private static readonly HashSet<(string File, int Line)> Reported = [];
    private static readonly Lock Gate = new();
    private static ILogger? _logger;

    // null detaches: failures go to standard error
    public static void Attach(ILogger? logger)
    {
        lock (Gate)
        {
            Reported.Clear(); // otherwise a failure from the previous world silences this one
        }

        _logger = logger;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Assert(bool condition, [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (condition) return true;
        Report("", file, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool NotNull<T>([NotNullWhen(true)] T? value,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "") where T : class
    {
        if (value is not null) return true;
        Report("null", file, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Finite(double value, [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (double.IsFinite(value)) return true;
        Report("not finite", file, line);
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Index(int value, int count, [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if ((uint)value < (uint)count) return true;
        Report("out of range", file, line);
        return false;
    }

    // At most max items, reported when more. Spans don't allocate; the IEnumerable iterator stays out of frame code.
    public static ReadOnlySpan<T> Bounded<T>(this List<T> items, int max,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Count > max) Over(max, file, line);
        return CollectionsMarshal.AsSpan(items)[..Math.Min(items.Count, max)];
    }

    public static ReadOnlySpan<T> Bounded<T>(this T[] items, int max,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Length > max) Over(max, file, line);
        return items.AsSpan(0, Math.Min(items.Length, max));
    }

    public static ReadOnlySpan<T> Bounded<T>(this ReadOnlySpan<T> items, int max,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Length > max) Over(max, file, line);
        return items[..Math.Min(items.Length, max)];
    }

    public static IEnumerable<T> Bounded<T>(this IEnumerable<T> items, int max,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        using var e = items.GetEnumerator();
        for (var i = 0; i < max; i++)
        {
            if (!e.MoveNext()) yield break;
            yield return e.Current;
        }

        if (e.MoveNext()) Over(max, file, line);
    }

    // A dictionary's values with neither the IEnumerable iterator nor its boxed enumerator: foreach takes the struct
    public static BoundedValues<TKey, TValue> Bounded<TKey, TValue>(this Dictionary<TKey, TValue>.ValueCollection items,
        int max, [CallerLineNumber] int line = 0, [CallerFilePath] string file = "") where TKey : notnull
    {
        if (items.Count > max) Over(max, file, line);
        return new BoundedValues<TKey, TValue>(items, Math.Min(items.Count, max));
    }

    // A dictionary's pairs the same way (frame code walks pass, program and band tables per draw)
    public static BoundedPairs<TKey, TValue> Bounded<TKey, TValue>(this Dictionary<TKey, TValue> items, int max,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "") where TKey : notnull
    {
        if (items.Count > max) Over(max, file, line);
        return new BoundedPairs<TKey, TValue>(items, Math.Min(items.Count, max));
    }

    // A set's items the same way
    public static BoundedSet<T> Bounded<T>(this HashSet<T> items, int max,
        [CallerLineNumber] int line = 0, [CallerFilePath] string file = "")
    {
        if (items.Count > max) Over(max, file, line);
        return new BoundedSet<T>(items, Math.Min(items.Count, max));
    }

    public readonly struct BoundedPairs<TKey, TValue>(Dictionary<TKey, TValue> items, int max) where TKey : notnull
    {
        public Enumerator GetEnumerator() => new(items.GetEnumerator(), max);

        public struct Enumerator(Dictionary<TKey, TValue>.Enumerator inner, int left)
        {
            private Dictionary<TKey, TValue>.Enumerator _inner = inner;
            private int _left = left;

            public KeyValuePair<TKey, TValue> Current => _inner.Current;

            public bool MoveNext()
            {
                if (_left <= 0) return false;
                _left--;
                return _inner.MoveNext();
            }
        }
    }

    public readonly struct BoundedSet<T>(HashSet<T> items, int max)
    {
        public Enumerator GetEnumerator() => new(items.GetEnumerator(), max);

        public struct Enumerator(HashSet<T>.Enumerator inner, int left)
        {
            private HashSet<T>.Enumerator _inner = inner;
            private int _left = left;

            public T Current => _inner.Current;

            public bool MoveNext()
            {
                if (_left <= 0) return false;
                _left--;
                return _inner.MoveNext();
            }
        }
    }

    public readonly struct BoundedValues<TKey, TValue>(Dictionary<TKey, TValue>.ValueCollection items, int max)
        where TKey : notnull
    {
        public Enumerator GetEnumerator() => new(items.GetEnumerator(), max);

        public struct Enumerator(Dictionary<TKey, TValue>.ValueCollection.Enumerator inner, int left)
        {
            private Dictionary<TKey, TValue>.ValueCollection.Enumerator _inner = inner;
            private int _left = left;

            public TValue Current => _inner.Current;

            public bool MoveNext()
            {
                if (_left <= 0) return false;
                _left--;
                return _inner.MoveNext();
            }
        }
    }

    // File and line find the check in the source; its text is not kept (thousands of call sites, half a megabyte of DLL)
    private static void Report(string what, string file, int line)
    {
        if (First(file, line)) Log(what, file, line);
    }

    // A cap that stays exceeded in frame code is asked every frame: the text is built only for the first report
    private static void Over(int max, string file, int line)
    {
        if (First(file, line)) Log($"more than {max}", file, line);
    }

    private static bool First(string file, int line)
    {
        lock (Gate) return Reported.Add((file, line));
    }

    private static void Log(string what, string file, int line)
    {
        var site = file.Replace('\\', '/').Replace("/_/", "", StringComparison.Ordinal) + ":" + line;
        if (_logger is null) Console.Error.WriteLine($"Komet: check failed at {site} {what}");
        else _logger.Error("Komet: check failed at {0} {1}", site, what);
    }
}
