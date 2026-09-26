using System.Reflection;
using HarmonyLib;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// Other mods' features (KometFeatures.Register) and the mods' holds (KometFeatures.HoldOff). A registered feature takes the next index
// after Komet's, its knob the next after Komet's knobs. Registered before Komet started it waits for InstallQueued (after Komet's main
// stage, before the bench and PreJit); while Komet runs it installs at once; from LevelFinalize (Recheck) on registrations are refused.
// Whatever of the mod's throws is logged, its patches go and the feature fails; Komet carries on. The world's close drops them all.
internal static partial class Features
{
    public const int MaxExternal = 32;

    private static readonly List<External> Added = [];
    private static readonly List<FeatureHold> Handles = [];
    // Komet's install, from InstallQueued on: a registration then installs at once
    private static FeatureContext? _context;
    private static bool _sealed; // LevelFinalize has passed

    private static int Count => Table.Length + Added.Count;

    private static ILogger? Log => _context?.Logger ?? ApiEvents.Logger;

    private static Feature At(int feature)
    {
        if (!Index(feature, Count) || !Assert(Count <= MaxFeatures)) return Table[0];
        return feature < Table.Length ? Table[feature] : Added[feature - Table.Length].Feature;
    }

    // After Komet's main stage: the features other mods registered before Komet started, in registration order
    public static void InstallQueued(FeatureContext context)
    {
        if (!NotNull(context) || !Assert(Added.Count <= MaxExternal)) return;
        _context = context;
        for (var i = 0; i < Math.Min(Added.Count, MaxExternal); i++)
            if (!Installed[Added[i].Slot])
                InstallAt(context, Added[i].Slot);
        Poll();
    }

    // KometFeatures.Register: refused with the reason logged, else queued or installed at once
    internal static bool Register(FeatureDefinition definition)
    {
        if (!NotNull(definition) || !Assert(Added.Count <= MaxExternal)) return false;
        if (Refusal(definition) is { } reason)
        {
            Log?.Warning("Komet: {0} is not registered, {1}", definition.Id, reason);
            return false;
        }

        var external = new External(definition, Count);
        (States[external.Slot], Holds[external.Slot], Installed[external.Slot]) = (Unknown, 0, false);
        external.Feature = Describe(external);
        Added.Add(external);
        if (_context is { } context) InstallAt(context, external.Slot);
        Poll();
        return true;
    }

    // Why the definition cannot be registered; null when it can
    private static string? Refusal(FeatureDefinition definition)
    {
        if (!NotNull(definition) || !Assert(Count <= MaxFeatures)) return "it is null";
        if (!IsExternalId(definition.Id) || definition.Title.Length == 0)
            return "its id is not modid:name (a lower-case mod id; letters, digits, _ and - in the name) or its " +
                   "title is empty";
        if (_sealed) return "the world has loaded: register in Start or StartClientSide";
        if (Find(definition.Id) >= 0) return "the id is taken";
        if (Count >= MaxFeatures || Added.Count >= MaxExternal ||
            (definition.Knob is not null && Knobs.Count >= Knobs.MaxKnobs))
            return "Komet holds as many features as it can";
        if (definition.Knob is { Valid: false }) return "its knob's range is empty or leaves out its engine value";
        if (definition.Fingerprint != 0 && definition.Shaped is null) return "it has a fingerprint but no Shaped";
        return definition.Watched is not null && (definition.StandDownFor & PatchKinds.All) == PatchKinds.None
            ? "it watches methods but stands down for no patch kind"
            : null;
    }

    // Its entry in Komet's loops: installed by Mount, rechecked by Watch, stopped by Unmount; its knob, if any, after Komet's
    private static Feature Describe(External external)
    {
        var (definition, slot) = (external.Definition, external.Slot);
        if (!NotNull(definition) || !Index(slot, MaxFeatures)) return new Feature("");
        if (definition.Knob is { } knob)
        {
            external.Applied = knob.Engine;
            external.Knob = Knobs.Add(new Knob(definition.Id, definition.Page, definition.Group, knob.Min, knob.Max,
                    knob.Engine, () => external.Applied, value => Apply(external, value))
                { Unit = knob.Unit, Owner = slot });
        }

        return new Feature(definition.Id)
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

    // The fingerprint, the patches under the feature's own Harmony id, the player's value, the stand-down check; whatever of the
    // mod's throws takes its patches out again
    private static void Mount(FeatureContext context, External external)
    {
        var definition = external.Definition;
        if (!NotNull(context) || !Assert(external.Harmony is null)) return;
        try
        {
            if (definition.Fingerprint != 0 && !Shaped(definition, context.Logger))
            {
                external.Changed = true;
                return;
            }

            external.Harmony = new Harmony(definition.Id);
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

    // Whether the bodies the feature replaces are those its mod pinned; logged when not
    private static bool Shaped(FeatureDefinition definition, ILogger logger)
    {
        if (!NotNull(definition.Shaped) || !NotNull(logger)) return false;
        var shaped = definition.Shaped();
        MethodBase?[] methods = shaped is { Count: > 0 and <= EngineShape.MaxMethods } ? [.. shaped] : [];
        if (methods.Length == 0)
            logger.Warning("Komet: {0} is not installed, its Shaped names no method or more than {1}", definition.Id,
                EngineShape.MaxMethods);
        return methods.Length > 0 &&
               EngineShape.Matches(methods, definition.Fingerprint, definition.Id, logger, definition.ModId);
    }

    // Another mod's patch of a StandDownFor kind on a watched method stands the feature down, at install and at LevelFinalize,
    // until a later check finds them free
    private static void Watch(External external)
    {
        var definition = external.Definition;
        if (!NotNull(definition) || !Assert(external.Slot >= Table.Length)) return;
        if (definition.Watched is null || !external.Mounted || external.Failed) return;
        try
        {
            MethodBase?[] seams = definition.Watched() is { } list ? [.. list.Take(EngineShape.MaxSeams)] : [];
            var kinds = (EngineShape.Kinds)(definition.StandDownFor & PatchKinds.All);
            var foreign = seams.Length > 0 && EngineShape.Foreign(seams, kinds, definition.Id);
            Stand(external, EngineShape.Report(Log, definition.Id, external.Foreign, foreign));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
        }
    }

    // A stand-down holds the feature's knob at its engine value as a mod's hold does, released when it ends
    private static void Stand(External external, bool foreign)
    {
        if (!NotNull(external) || !Index(external.Slot, Count) || foreign == external.Foreign) return;
        external.Foreign = foreign;
        if (foreign) external.Parked = Hold(external.Slot);
        else if (external.Parked)
        {
            external.Parked = false;
            _ = Release(external.Slot);
        }
    }

    // The knob's static, what the mod's patches read, told to the mod while its patches are in (Mount tells it the value in place).
    // A throw fails the feature.
    private static void Apply(External external, int value)
    {
        if (!NotNull(external.Definition.Knob) || !Assert(external.Knob >= 0)) return;
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

    // The player's value as the mod keeps it, its engine value when out of range or once the feature has gone with its world
    private static int Chosen(External external)
    {
        if (!NotNull(external) || external.Definition.Knob is not { } knob || !Assert(Added.Count <= MaxExternal))
            return 0;
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

    // An option row's input: the mod saves it, the knob applies it unless the feature is held
    private static void Choose(External external, int value)
    {
        if (!NotNull(external) || external.Definition.Knob is not { } knob || !Assert(external.Knob >= 0)) return;
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
        Poll();
    }

    private static void Fail(External external, Exception e)
    {
        if (!NotNull(external) || !NotNull(e)) return;
        Log?.Error("Komet: {0} threw and is taken out, the engine's code runs: {1}", external.Definition.Id, e);
        external.Failed = true;
        Unpatch(external);
    }

    private static void Unpatch(External external)
    {
        if (!NotNull(external) || !Assert(external.Slot >= Table.Length)) return;
        external.Harmony?.UnpatchAll(external.Definition.Id);
        external.Harmony = null;
    }

    // The world closes (Stop, or Close when it was never stopped): the mod's own cleanup, then its patches go
    private static void Unmount(External external)
    {
        if (!NotNull(external) || !Assert(external.Slot >= Table.Length)) return;
        if (external.Mounted)
            try
            {
                external.Definition.Uninstall?.Invoke();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log?.Error("Komet: {0} threw in its uninstall: {1}", external.Definition.Id, e);
            }

        external.Mounted = false;
        Unpatch(external);
    }

    // Close: other mods' features, their knobs and every mod's hold go, registrations open again
    private static void Drop()
    {
        if (!Assert(Added.Count <= MaxExternal) || !Assert(Handles.Count <= MaxHolds)) return;
        foreach (var external in Added.Bounded(MaxExternal)) Unmount(external);
        Array.Clear(States, Table.Length, MaxFeatures - Table.Length);
        Array.Clear(Holds, Table.Length, MaxFeatures - Table.Length);
        Added.Clear();
        Handles.Clear();
        Knobs.Truncate();
        (_context, _sealed) = (null, false);
        Rows();
    }

    // KometFeatures.HoldOff: a handle holds only a feature with a knob; one hold of MaxHolds is kept for a stand-down
    internal static FeatureHold HoldOff(string id, string modId, string reason)
    {
        if (!NotNull(id) || !Assert(Handles.Count < MaxHolds)) return new FeatureHold("", modId, reason, -1);
        var feature = Find(id);
        var hold = new FeatureHold(feature >= 0 ? At(feature).Id : id, modId, reason, feature);
        string? refused = null;
        if (feature < 0) refused = "it is no feature";
        else if (At(feature).Knobs.Length == 0) refused = "it has no switch";
        else if (Handles.Count >= MaxHolds - 1) refused = "it is held too often";
        if (refused is not null) Log?.Warning("Komet: {0} cannot hold off {1}, {2}", modId, id, refused);
        else
        {
            Handles.Add(hold);
            if (!Hold(feature)) _ = Handles.Remove(hold);
        }

        return hold;
    }

    // Once per handle, and never after the world it held in closed
    internal static bool Release(FeatureHold hold)
    {
        if (!NotNull(hold) || !Assert(Handles.Count <= MaxHolds) || !Handles.Remove(hold)) return false;
        return Release(hold.Feature);
    }

    internal static bool Holding(FeatureHold hold)
    {
        return NotNull(hold) && Assert(Handles.Count <= MaxHolds) && Handles.Contains(hold);
    }

    // The first hold still in place on the feature
    private static FeatureHold? Holder(int feature)
    {
        if (!Assert(Handles.Count <= MaxHolds) || !Assert(feature >= -1)) return null;
        foreach (var hold in Handles.Bounded(MaxHolds))
            if (hold.Feature == feature)
                return hold;
        return null;
    }

    // KometFeatures.Snapshot: every feature in index order, each state evaluated now
    internal static FeatureInfo[] Snapshot()
    {
        var infos = new FeatureInfo[Math.Min(Count, MaxFeatures)];
        for (var i = 0; i < Math.Min(infos.Length, MaxFeatures); i++) infos[i] = Info(i);
        return Assert(infos.Length >= Table.Length) ? infos : [];
    }

    private static FeatureInfo Info(int feature)
    {
        if (!Index(feature, Count) || !Assert(Added.Count <= MaxExternal))
            return new FeatureInfo("?", "?", "?", Unknown, false);
        var definition = feature >= Table.Length ? Added[feature - Table.Length].Definition : null;
        var (f, hold) = (At(feature), Holder(feature));
        var (title, owner) = (definition?.Title ?? f.Id, definition?.ModId ?? KometModSystem.ModId);
        return new FeatureInfo(f.Id, title, owner, Evaluate(feature), f.Knobs.Length > 0)
            { HeldBy = hold?.ModId, Reason = hold?.Reason };
    }

    // Other mods' knobs placed on the page, in registration order, each under its group (on Komet's pages the mod id without one),
    // locked like Komet's own while held
    internal static OptionPage AddRows(OptionPage page, bool komet)
    {
        if (!NotNull(page) || !Assert(Added.Count <= MaxExternal)) return page;
        string? group = null;
        foreach (var external in Added.Bounded(MaxExternal))
        {
            var (definition, knob) = (external.Definition, external.Knob);
            if (definition.Page != page.Id || definition.Knob is not { } k || knob < 0) continue;
            var heading = definition.Group ?? (komet ? definition.ModId : null);
            if (heading is not null && heading != group) _ = page.Group(heading);
            (group, var hint, var unit) = (heading, definition.Hint, k.Unit.Length > 0 ? " " + k.Unit : "");
            _ = (k.IsSwitch
                ? page.Switch(definition.Title, () => Chosen(external) != 0, on => Choose(external, on ? 1 : 0), hint)
                : page.Slider(definition.Title, k.Min, k.Max, 1, () => Chosen(external), v => Choose(external, (int)v),
                    unit, hint)).LockedWhen(() => LockText(knob));
        }

        return page;
    }

    // A mod's own options page as the options screen shows it: with the features it places there
    internal static OptionPage Placed(OptionPage page)
    {
        if (!NotNull(page) || !Assert(Added.Count <= MaxExternal)) return page;
        foreach (var external in Added.Bounded(MaxExternal))
            if (external.Definition.Page == page.Id && external.Knob >= 0)
                return AddRows(page.Copy(), false);
        return page;
    }

    // A registered feature: its definition, the entry Komet's loops run, what its install and checks found
    private sealed class External(FeatureDefinition definition, int slot)
    {
        public FeatureDefinition Definition { get; } = definition;
        public int Slot { get; } = slot; // its feature index
        public Feature Feature { get; set; } = new(definition.Id);
        public Harmony? Harmony { get; set; } // while its patches are in
        public int Knob { get; set; } = -1;
        public int Applied { get; set; } // what apply was given last
        public bool Mounted { get; set; } // Install returned: Uninstall runs when the world closes
        public bool Changed { get; set; } // the fingerprint differs
        public bool Failed { get; set; }
        public bool Foreign { get; set; } // another mod patches what it watches
        public bool Parked { get; set; } // held by its stand-down
    }
}
