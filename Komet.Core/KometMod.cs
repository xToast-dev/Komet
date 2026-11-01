using System.Reflection;

namespace Komet.Core;

// What every module may know about the mod that carries it, set by KometModSystem (the project above all modules): its id,
// whether the server runs in this process, and the assembly the build stamps (channel, commit, edition)
internal static class KometMod
{
    public const string Id = "komet";

    // Server in this process (singleplayer or LAN; set at StartClientSide): only then are server-side patches installed
    public static bool LocalServer { get; internal set; }

    // The mod's own assembly (Komet.dll), for the build metadata; the module assemblies carry none
    public static Assembly Assembly { get; internal set; } = typeof(KometMod).Assembly;

    public static void Attach(Assembly mod)
    {
        if (!NotNull(mod)) return;
        Assembly = mod;
    }
}
