using Komet.Api;

namespace Komet.Core;

// The feature registry's one name for the modules, the HUD, the options and the tests. Each member forwards to the type
// that owns it: FeatureTable (the features by index), FeatureOrder (the install order), FeatureLifecycle (install to
// close), FeatureStates (states and the HUD's hint), FeatureHolds (holds), FeatureGuards (rival stand-downs),
// ExternalFeatures and ExternalRows (other mods'), WorldEnd (the resets at close).
internal static class Features
{
    public const int MaxExternal = FeatureTable.MaxExternal, MaxFeatures = FeatureTable.MaxFeatures;

    public static int Troubled => FeatureStates.Troubled;

    public static void Use(Feature[][] modules) => FeatureTable.Use(modules);

    public static int Find(string id) => FeatureTable.Find(id);

    public static void Start(FeatureContext context) => FeatureLifecycle.Start(context);

    public static void Recheck() => FeatureLifecycle.Recheck();

    public static void Stop(FeatureContext? context) => FeatureLifecycle.Stop(context);

    public static void Unpatched() => FeatureLifecycle.Unpatched();

    public static void Cleanup(string owner, string operation, Action action) =>
        FeatureLog.Cleanup(owner, operation, action);

    public static void Poll() => FeatureStates.Poll();

    public static KapiFeatureState Evaluate(int feature) => FeatureStates.Evaluate(feature);

    public static string FirstTroubled() => FeatureStates.FirstTroubled();

    public static KapiHold HoldOff(string id, string modId, string reason) =>
        FeatureHolds.HoldOff(id, modId, reason);

    public static string? LockText(int knob) => FeatureHolds.LockText(knob);

    public static string HoldsText() => FeatureHolds.HoldsText();

    public static bool IsExternalId(string id) => ExternalFeatures.IsExternalId(id);

    public static OptionPage AddRows(OptionPage page, bool komet) => ExternalRows.AddRows(page, komet);

    public static OptionPage Placed(OptionPage page) => ExternalRows.Placed(page);
}
