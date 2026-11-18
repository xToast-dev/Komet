namespace Komet.Core;

// The events other mods subscribe to through KAPI (features, options, styles, the renderer) and the refusal log; the
// logger is set at StartPre, so refusals before Komet's Start log too
internal static class ModEvents
{
    private const int MaxHandlers = 64;

    public static ILogger? Logger { get; set; }

    // Each handler separately, at most MaxHandlers; one that throws is logged once with its assembly and removed
    public static void Raise<T>(T? handlers, Action<T> invoke, Action<T> remove, string name) where T : Delegate
    {
        if (handlers is null || !NotNull(invoke) || !NotNull(remove)) return;

        foreach (var handler in handlers.GetInvocationList().Bounded(MaxHandlers))
        {
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
}
