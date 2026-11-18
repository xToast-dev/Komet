using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Komet.Api;
using static Komet.Api.KapiFeatureState;

namespace Komet.Core;

// Other mods' features (KapiFeatures.Register): a registration before LevelFinalize takes the next feature slot and, its
// knob, the next knob; failures and cleanup stay local to their feature. They go when the world closes (Drop).
// Main thread only.
internal static class ExternalFeatures
{
    private const int MaxId = 128;

    private static readonly List<External> Added = [];

    private static readonly Regex ExternalId = new("^[a-z0-9]+:[A-Za-z0-9_-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private static ILogger? Log => FeatureLog.Log;

    internal static ReadOnlySpan<External> All => Added.Bounded(FeatureTable.MaxExternal);

    public static bool IsExternalId(string id)
    {
        if (!NotNull(id) || id.Length > MaxId) return false;

        return ExternalId.IsMatch(id);
    }

    // The other mod's feature behind a feature slot; null for Komet's own
    internal static External? Of(int feature)
    {
        foreach (var external in Added.Bounded(FeatureTable.MaxExternal))
        {
            if (external.Slot == feature) return external;
        }

        return null;
    }

    internal static bool Register(string modId, KapiFeature definition)
    {
        if (!NotNull(modId) || !NotNull(definition)) return false;

        var external = new External(modId, definition, FeatureTable.Count);
        if (FeatureLog.OffMain("registering", external.Id)) return false;

        if (Refusal(external) is { } reason)
        {
            Log?.Warning("Komet: {0} is not registered, {1}", external.Id, reason);
            return false;
        }

        FeatureStates.Reset(external.Slot);
        FeatureHolds.Reset(external.Slot);
        FeatureLifecycle.Reset(external.Slot);
        external.Feature = Describe(external);
        if (!Assert(FeatureTable.Add(external.Feature) == external.Slot)) return false;

        Added.Add(external);
        if (FeatureLifecycle.Context is { } context) FeatureLifecycle.InstallAt(context, external.Slot);

        FeatureStates.Poll();
        return true;
    }

    private static string? Refusal(External external)
    {
        var definition = external.Definition;
        if (!IsExternalId(external.Id) || definition.Title.Length == 0)
        {
            return "its id is not modid:name (a lower-case mod id; letters, digits, _ and - in the name) or its " +
                   "title is empty";
        }

        if (FeatureLifecycle.Sealed) return "the world has loaded: register in StartClientSide";

        if (FeatureTable.Find(external.Id) >= 0) return "the id is taken";

        if (FeatureTable.Count >= FeatureTable.MaxFeatures || Added.Count >= FeatureTable.MaxExternal ||
            (definition.Knob is not null && Knobs.Count >= Knobs.MaxKnobs))
        {
            return "Komet holds as many features as it can";
        }

        if (definition.Knob is { Valid: false }) return "its knob's range is empty or leaves out its engine value";

        if (definition.Fingerprint != 0 && definition.Shaped is null) return "it has a fingerprint but no Shaped";

        if (definition.Watched is not null && definition.Knob is null)
        {
            return "it watches methods but has no knob to stand down with"; // its patches would stay in
        }

        return definition.Watched is not null && (definition.StandDownFor & KapiPatchKinds.All) == KapiPatchKinds.None
            ? "it watches methods but stands down for no patch kind"
            : null;
    }

    private static Feature Describe(External external)
    {
        var (definition, slot) = (external.Definition, external.Slot);
        if (definition.Knob is { } knob)
        {
            external.Applied = knob.Engine;
            external.Knob = Knobs.Add(new Knob(external.Id, definition.Page, definition.Group, knob.Min, knob.Max,
                    knob.Engine, () => external.Applied, value => Apply(external, value))
                { Unit = knob.Unit, Owner = slot });
        }

        return new Feature(external.Id)
        {
            Install = c => Mount(c, external),
            ServerOnly = definition.ServerOnly,
            Knobs = external.Knob >= 0 ? [Knobs.At(external.Knob)] : [],
            Recheck = () => Watch(external),
            Stop = _ => Unmount(external),
            Probe = () => (external.Failed, external.Changed, external.Foreign) switch
            {
                (true, _, _) => Failed,
                (_, true, _) => EngineChanged,
                (_, _, true) => StoodDown,
                _ => Active
            }
        };
    }

    private static void Mount(FeatureContext context, External external)
    {
        var definition = external.Definition;
        if (!Assert(external.Harmony is null)) return;

        try
        {
            if (definition.Fingerprint != 0 && !Shaped(external, context.Logger))
            {
                external.Changed = true;
                return;
            }

            external.Harmony = new Harmony(external.Id);
            external.CleanupNeeded = true;
            definition.Install?.Invoke(external.Harmony, context.Logger);
            external.Mounted = true;
            if (external.Knob >= 0) Knobs.Seed(external.Knob, Chosen(external));

            Watch(external);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
        }
    }

    private static bool Shaped(External external, ILogger logger)
    {
        var definition = external.Definition;
        if (!NotNull(definition.Shaped)) return false;

        var shaped = definition.Shaped();
        MethodBase?[] methods = shaped is { Count: > 0 and <= EngineShape.MaxMethods } ? [.. shaped] : [];
        if (methods.Length == 0)
        {
            logger.Warning("Komet: {0} is not installed, its Shaped names no method or more than {1}", external.Id,
                EngineShape.MaxMethods);
        }

        return methods.Length > 0 &&
               EngineShape.Matches(methods, definition.Fingerprint, external.Id, logger, external.ModId);
    }

    private static void Watch(External external)
    {
        var definition = external.Definition;
        if (definition.Watched is null || !external.Mounted || external.Failed) return;

        try
        {
            MethodBase?[] seams = definition.Watched() is { } list ? [.. list.Take(EngineShape.MaxSeams + 1)] : [null];
            var kinds = definition.StandDownFor & KapiPatchKinds.All;
            var foreign = seams.Length > 0 && EngineShape.Foreign(seams, kinds, external.Id);
            Stand(external,
                EngineShape.Report(Log, external.Id, external.Foreign, foreign, seams, kinds, external.Id));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
        }
    }

    // A stand-down holds the knob at engine value like a mod's hold
    private static void Stand(External external, bool foreign)
    {
        if (!Index(external.Slot, FeatureTable.Count) || foreign == external.Foreign) return;

        external.Foreign = foreign;
        if (foreign)
        {
            external.Parked = FeatureHolds.Hold(external.Slot);
        }
        else if (external.Parked)
        {
            external.Parked = false;
            _ = FeatureHolds.Release(external.Slot);
        }
    }

    // Told to the mod while its patches are in (Mount tells it in place).
    private static void Apply(External external, int value)
    {
        if (!NotNull(external.Definition.Knob)) return;

        external.Applied = value;
        if (external.Failed || !external.Mounted) return;

        try
        {
            external.Definition.Knob.Apply(value);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
        }
    }

    // Engine value when out of range or once the feature went with its world
    internal static int Chosen(External external)
    {
        if (external.Definition.Knob is not { } knob) return 0;

        if (!Added.Contains(external)) return knob.Engine;

        try
        {
            var value = knob.Get();
            return value >= knob.Min && value <= knob.Max ? value : knob.Engine;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
            return knob.Engine;
        }
    }

    // The mod saves it; the knob applies it unless held
    internal static void Choose(External external, int value)
    {
        if (external.Definition.Knob is not { } knob) return;

        if (!Added.Contains(external) || value < knob.Min || value > knob.Max) return;

        try
        {
            knob.Set(value);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
            return;
        }

        _ = Knobs.Write(external.Knob, value);
        FeatureStates.Poll();
    }

    // Logged once: a mod's getter that keeps throwing would log its stack trace at every compose of its options row
    private static void Fail(External external, Exception e)
    {
        if (!external.Failed)
        {
            Log?.Error("Komet: {0} threw and is taken out, the engine's code runs: {1}", external.Id, e);
        }

        external.Failed = true;
        CompatibilityLog.Record(external.Id, "failure", e.ToString());
        Unmount(external);
    }

    private static void Unpatch(External external)
    {
        var harmony = external.Harmony;
        external.Harmony = null;
        if (harmony is not null)
        {
            FeatureLog.Cleanup(external.Id, "unpatch external feature",
                () => harmony.UnpatchAll(external.Id));
        }
    }

    // World closes: the mod's cleanup, then its patches go
    private static void Unmount(External external)
    {
        external.Mounted = false;
        var cleanup = external.CleanupNeeded;
        external.CleanupNeeded = false;
        if (cleanup)
        {
            try
            {
                external.Definition.Uninstall?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log?.Error("Komet: {0} threw in its uninstall: {1}", external.Id, e);
            }
        }

        Unpatch(external);
    }

    // The world closes: every mod's cleanup and patches go, and with them their slots and knobs
    internal static void Drop()
    {
        foreach (var external in Added.Bounded(FeatureTable.MaxExternal))
        {
            Unmount(external);
        }

        FeatureStates.ClearExternal();
        FeatureHolds.ClearExternal();
        Added.Clear();
        FeatureTable.Truncate();
        Knobs.Truncate();
    }
}

// One other mod's feature and what Komet knows of it
internal sealed class External(string modId, KapiFeature definition, int slot)
{
    public string ModId { get; } = modId;
    public KapiFeature Definition { get; } = definition;
    public string Id { get; } = modId + ":" + definition.Name; // modid:name, also its Harmony id
    public int Slot { get; } = slot; // its feature index
    public Feature Feature { get; set; } = new(modId + ":" + definition.Name);
    public Harmony? Harmony { get; set; } // while its patches are in
    public int Knob { get; set; } = -1;
    public int Applied { get; set; } // what apply was given last
    public bool Mounted { get; set; } // Install returned: Uninstall runs when the world closes
    public bool CleanupNeeded { get; set; } // Install began, including a partial install that threw
    public bool Changed { get; set; } // the fingerprint differs
    public bool Failed { get; set; }
    public bool Foreign { get; set; } // another mod patches what it watches
    public bool Parked { get; set; } // held by its stand-down
}
