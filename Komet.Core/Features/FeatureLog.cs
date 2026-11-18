
namespace Komet.Core;

// Where the feature registry logs (the world's logger while one runs, else the public API's from StartPre), its main-thread
// gate, and cleanup that survives a thrower.
internal static class FeatureLog
{
    public static ILogger? Log => FeatureLifecycle.Context?.Logger ?? ModEvents.Logger;

    // Logged at Debug only: the server thread's repeat of a singleplayer mod's Start is expected, not the mod's mistake
    public static bool OffMain(string what, string id)
    {
        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) return false;

        Log?.Debug("Komet: {0} {1} ignored off the main thread", what, id);
        return true;
    }

    // Cleanup callbacks cross module and mod boundaries; every later owner must still get its cleanup.
    public static void Cleanup(string owner, string operation, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log?.Error("Komet {0}: {1} failed; cleanup continues: {2}", owner, operation, e);
        }
    }
}
