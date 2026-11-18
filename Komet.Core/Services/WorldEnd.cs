namespace Komet.Core;

// What goes when a world closes, each part registering its own reset the first time it is used (its static constructor):
// the public API's registrations and subscriptions, the chunk views' caches. The feature lifecycle runs them first at
// its close, before the holds end, so no other mod hears of that. A reset that throws is logged; the others still run.
internal static class WorldEnd
{
    private const int MaxResets = 32;

    private static readonly List<(string Owner, Action Reset)> Resets = [];
    private static readonly Lock Gate = new();

    // Once per owner: a second registration of the same owner is ignored
    public static void On(string owner, Action reset)
    {
        if (!NotNull(owner) || !NotNull(reset)) return;

        lock (Gate)
        {
            if (!Resets.Exists(r => r.Owner == owner) && Assert(Resets.Count < MaxResets)) Resets.Add((owner, reset));
        }
    }

    // In registration order
    public static void Run()
    {
        (string Owner, Action Reset)[] resets;
        lock (Gate)
        {
            resets = [.. Resets.Bounded(MaxResets)];
        }

        foreach (var (owner, reset) in resets.Bounded(MaxResets))
        {
            FeatureLog.Cleanup(owner, "world end", reset);
        }
    }
}
