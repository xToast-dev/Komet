namespace Komet.Api;

// What the public API sends other mods: its events, and the log lines of what it refused. The logger is Komet's from StartPre, before
// any mod's Start, so a registration refused before Komet started is logged too.
internal static class ApiEvents
{
    public const int MaxHandlers = 64;

    public static ILogger? Logger { get; set; }

    // Every handler on its own, at most MaxHandlers: one that throws is logged once, with the assembly that declared it, and removed,
    // so neither Komet nor the other handlers see its exception again
    public static void Raise<T>(T? handlers, Action<T> invoke, Action<T> remove, string name) where T : Delegate
    {
        if (handlers is null || !NotNull(invoke) || !NotNull(remove)) return;
        foreach (var handler in handlers.GetInvocationList().Bounded(MaxHandlers))
            try
            {
                invoke((T)handler);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                remove((T)handler);
                Logger?.Error("Komet: a {0} handler of {1} threw and is unsubscribed: {2}", name,
                    handler.Method.DeclaringType?.Assembly.GetName().Name ?? "?", e);
            }
    }
}
