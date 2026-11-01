namespace Komet.Core;

// The engine lets a tick listener go only on the main thread and throws on any other. A world torn down from another
// thread (a crash path, or the bench ending a run it refused) drops its listeners with its event manager anyway, so
// there the call is left out instead of throwing out of Dispose and crashing the client.
internal static class Listeners
{
    public static void Unregister(IEventAPI? events, long listener)
    {
        if (events is null || listener == 0 || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;
        events.UnregisterGameTickListener(listener);
    }
}
