namespace Komet.Core;

// A world's run of the features: install in table order, recheck once every mod has patched, stop in reverse order
// before UnpatchAll, the unpatched callbacks after it, and the close that ends holds and other mods' features and runs
// every WorldEnd reset. A throw at install propagates: Komet stands down as a whole. Main thread only.
internal static class FeatureLifecycle
{
    private static readonly bool[] Installed = new bool[FeatureTable.MaxFeatures];

    // The world's context from Start (or InstallQueued) until the close
    public static FeatureContext? Context { get; private set; }

    // The server runs elsewhere: server-only features are marked installed without patches
    public static bool Remote { get; private set; }

    // LevelFinalize has passed: other mods' registrations are refused from then on
    public static bool Sealed { get; private set; }

    public static bool IsInstalled(int feature) => Index(feature, FeatureTable.MaxFeatures) && Installed[feature];

    // Komet's features, then the other mods' registered before
    public static void Start(FeatureContext context)
    {
        if (!NotNull(context)) return;

        Context = context;
        ProbeTarget.Epoch++; // a new world: the probes look again
        for (var i = 0; i < Math.Min(FeatureTable.BuiltInCount, FeatureTable.MaxBuiltIn); i++)
        {
            InstallAt(context, i);
        }

        FeatureStates.Poll();
        InstallQueued(context);
    }

    // Other mods' features registered before Komet started
    public static void InstallQueued(FeatureContext context)
    {
        if (!NotNull(context)) return;

        Context = context;
        for (var i = FeatureTable.BuiltInCount; i < Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures); i++)
        {
            if (!Installed[i]) InstallAt(context, i);
        }

        FeatureStates.Poll();
    }

    // Remote server: marked installed without patches
    public static void InstallAt(FeatureContext context, int feature)
    {
        if (!NotNull(context) || !Index(feature, FeatureTable.Count)) return;

        Remote = !context.LocalServer;
        FeatureGuards.Own(context.Harmony.Id);
        var f = FeatureTable.At(feature);
        if (!f.ServerOnly || context.LocalServer) f.Life.Install?.Invoke(context);

        Installed[feature] = true;
        FeatureGuards.Guard(feature);
    }

    // A slot another mod's feature takes starts uninstalled
    internal static void Reset(int feature)
    {
        if (Index(feature, FeatureTable.MaxFeatures)) Installed[feature] = false;
    }

    public static void Recheck()
    {
        for (var i = 0; i < Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures); i++)
        {
            if (Installed[i])
            {
                FeatureTable.At(i).Life.Recheck?.Invoke();
                FeatureGuards.Guard(i);
            }
        }

        Sealed = true;
        FeatureStates.Poll();
    }

    // Before UnpatchAll, every feature, installed or not (a stand-down clears all): other mods' features, then Komet's,
    // each in reverse install order
    public static void Stop(FeatureContext? context)
    {
        if (!Assert(FeatureTable.BuiltInCount > 0)) return;

        var count = Math.Min(FeatureTable.Count, FeatureTable.MaxFeatures);
        for (var n = 0; n < Math.Min(count, FeatureTable.MaxFeatures); n++)
        {
            var feature = FeatureTable.At(count - 1 - n);
            FeatureLog.Cleanup(feature.Id, "stop", () => feature.Life.Stop?.Invoke(context));
        }
    }

    // After UnpatchAll: Komet's features' callbacks in install order, then the close
    public static void Unpatched()
    {
        if (!Assert(FeatureTable.BuiltInCount > 0)) return;

        for (var i = 0; i < Math.Min(FeatureTable.BuiltInCount, FeatureTable.MaxBuiltIn); i++)
        {
            var feature = FeatureTable.At(i);
            FeatureLog.Cleanup(feature.Id, "after unpatching", () => feature.Life.Unpatched?.Invoke());
        }

        Close();
    }

    // Silently: the public API's registrations go first (no other mod hears of the rest), then holds end and write back,
    // other mods' features and pages go (they belong to the world that closes)
    public static void Close()
    {
        WorldEnd.Run();
        FeatureHolds.EndAll();
        ExternalFeatures.Drop();
        CompatibilityLog.Clear();
        Array.Clear(Installed);
        FeatureGuards.Clear();
        (Context, Remote, Sealed) = (null, false, false);
        FeatureStates.Poll();
    }
}
