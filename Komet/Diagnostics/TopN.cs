namespace Komet.Diagnostics;

internal static class TopN
{
    public const int MaxRank = 8;

    // Insertion into top-N slots sorted descending: a new item enters at its rank, the last one falls off, an equal one stays behind
    public static void Rank<T>(Span<(T? Item, double Ms)> top, T item, double ms) where T : class
    {
        if (!Assert(top.Length is > 0 and <= MaxRank) || !Assert(ms >= 0) || !NotNull(item)) return;
        var slot = Math.Min(top.Length, MaxRank);
        for (var i = 0; i < Math.Min(top.Length, MaxRank); i++)
            if (top[i].Item is null || top[i].Ms < ms)
            {
                slot = i;
                break;
            }

        if (slot == top.Length) return;
        top[slot..^1].CopyTo(top[(slot + 1)..]);
        top[slot] = (item, ms);
    }
}
