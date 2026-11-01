using HarmonyLib;
using Komet.Api;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Text;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// Every feature, from the modules' lists, in install order; their knobs form the Knobs table (bench arms write via Knobs.Write,
// never KometSettings). Stops run in reverse: GraphicsMenu before the HUD window, TessWorkers before the pool.
internal static class Features
{
    private const int MaxModules = 16;

    // Set once by Modules.Register (the mod project names the modules and their order; this registry never names a module)
    private static Feature[] Table = [];

    // The modules' lists, each in its own install order, and the order across them (also the knobs' order in komet.json and
    // on the settings pages). Called again with the same lists, it keeps the table (tests register more than once).
    public static void Use(Feature[][] modules, string[] order)
    {
        if (!NotNull(modules) || !NotNull(order) || !Assert(modules.Length <= MaxModules)) return;
        if (Table.Length > 0 && Table.Length == order.Length) return;
        Table = Ordered(modules, order);
        Knobs.Collect(Table);
    }

    // A feature missing from the order, or listed twice, throws: Komet does not start with a table it cannot order
    private static Feature[] Ordered(Feature[][] modules, string[] order)
    {
        var byId = new Dictionary<string, Feature>(MaxFeatures, StringComparer.Ordinal);
        foreach (var module in modules.Bounded(MaxModules))
        {
            var last = -1;
            foreach (var feature in module.Bounded(MaxFeatures))
            {
                if (!byId.TryAdd(feature.Id, feature)) throw new InvalidOperationException("feature listed twice: " + feature.Id);
                var at = Array.IndexOf(order, feature.Id);
                if (at < 0) throw new InvalidOperationException("feature missing from Modules.InstallOrder: " + feature.Id);
                _ = Assert(at > last); // a module lists its features in install order
                last = at;
            }
        }

        var unknown = Array.FindAll(order, id => !byId.ContainsKey(id));
        if (unknown.Length > 0) throw new InvalidOperationException("Modules.InstallOrder names features no module has: " + string.Join(", ", unknown));
        _ = Assert(order.Length <= MaxBuiltIn);
        var table = new Feature[order.Length];
        for (var i = 0; i < Math.Min(order.Length, MaxBuiltIn); i++) table[i] = byId[order[i]];
        return table;
    }

    // Main thread only. A hold pins a feature's knobs at engine values (counted); the last release writes back the wanted
    // value. HUD rows are translated lazily, so drawing allocates nothing.
    // Komet's own and room for every other mod's (MaxExternal) beside them
    public const int MaxBuiltIn = 160, MaxFeatures = MaxBuiltIn + MaxExternal, MaxHolds = 96;
    private const int MaxId = 128;

    private static readonly bool[] Installed = new bool[MaxFeatures];
    private static readonly int[] Holds = new int[MaxFeatures];
    private static readonly bool[] Rivalled = new bool[MaxFeatures]; // stood down by Replaces
    private static readonly FeatureState[] States = new FeatureState[MaxFeatures];
    private static readonly Regex ExternalId = new("^[a-z0-9]+:[A-Za-z0-9_-]+$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));
    private static bool _remote; // the server runs elsewhere: server features stay out
    private static string? _owner; // the Harmony id Komet's features patch with
    private static string? _firstTroubled; // FirstTroubled's text until the states change

    public static ReadOnlySpan<Feature> All => Table;

    public static int NotActive { get; private set; }

    // Broken rather than chosen: stood down, engine changed, failed; off, held off or not applicable is no trouble
    public static int Troubled { get; private set; }

    // A throw propagates: Komet stands down as a whole.
    public static void Install(FeatureContext context, FeatureStage stage)
    {
        if (!NotNull(context)) return;
        if (stage == FeatureStage.Main) ProbeTarget.Epoch++; // a new world: the probes look again
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[i].Stage == stage)
                InstallAt(context, i);
        Poll();
    }

    // Remote server: marked installed without patches
    internal static void InstallAt(FeatureContext context, int feature)
    {
        if (!NotNull(context) || !Index(feature, Count)) return;
        _remote = !context.LocalServer;
        _owner = context.Harmony.Id; // Komet's patches, whatever id installed them (a test rig's own), never count as rivals
        if (!At(feature).ServerOnly || context.LocalServer) At(feature).Install?.Invoke(context);
        Installed[feature] = true;
        Guard(feature);
    }

    // Replaces: another mod's replacing patch on one of the feature's methods stands it down, its going lets it back
    private static void Guard(int feature)
    {
        if (!Index(feature, Count) || At(feature).Replaces is not { } replaces || At(feature).Knobs.Length == 0) return;
        MethodBase?[] seams;
        try
        {
            seams = [.. replaces().Where(m => m is not null).Take(EngineShape.MaxSeams)];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log?.Warning("Komet {0}: its methods could not be found, it stands down: {1}", At(feature).Id, e.Message);
            seams = [null];
        }

        var foreign = EngineShape.Report(Log, At(feature).Id, Rivalled[feature],
            seams.Length > 0 && EngineShape.Foreign(seams, At(feature).Rivals, _owner));
        if (foreign == Rivalled[feature]) return;
        Rivalled[feature] = foreign;
        _ = foreign ? Hold(feature) : Release(feature);
    }

    // Every mod has patched now; registrations end
    public static void Recheck()
    {
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Installed[i])
            {
                At(i).Recheck?.Invoke();
                Guard(i);
            }
        _sealed = true;
        Poll();
    }

    // Before UnpatchAll, every feature, installed or not (a stand-down clears all): the Last stage, other mods' features,
    // then the Main stage, each in reverse install order.
    public static void Stop(FeatureContext? context)
    {
        if (!Assert(Table.Length > 0)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[^(i + 1)].Stage != FeatureStage.Main)
                Table[^(i + 1)].Stop?.Invoke(context);
        for (var i = 0; i < Math.Min(Added.Count, MaxExternal); i++) Added[^(i + 1)].Feature.Stop?.Invoke(context);
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++)
            if (Table[^(i + 1)].Stage == FeatureStage.Main)
                Table[^(i + 1)].Stop?.Invoke(context);
    }

    public static void Unpatched()
    {
        if (!Assert(Table.Length > 0)) return;
        for (var i = 0; i < Math.Min(Table.Length, MaxFeatures); i++) Table[i].Unpatched?.Invoke();
        Close();
    }

    // Silently: holds end and write back, other mods' features and pages go (they belong to the world that closes)
    internal static void Close()
    {
        KometFeatures.Forget();
        KometDebug.Forget();
        KometOptions.Clear();
        KometStyles.Clear();
        ChunkViews.Release();
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Holds[i] > 0)
            {
                Holds[i] = 1;
                _ = Release(i);
            }

        Drop();
        Array.Clear(Installed);
        Array.Clear(Rivalled);
        _remote = false;
        Poll();
    }

    public static void Poll()
    {
        var (changed, off, troubled) = (false, 0, 0);
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
        {
            var (state, previous) = (Evaluate(i), States[i]);
            if (state != Active) off++;
            if (IsTrouble(state)) troubled++;
            if (state == previous) continue;
            (States[i], changed) = (state, true);
            KometFeatures.Raise(At(i).Id, previous, state);
        }

        (NotActive, Troubled) = (off, troubled);
        if (changed) _firstTroubled = null;
    }

    private static bool IsTrouble(FeatureState state) =>
        state is StoodDown or EngineChanged or Failed or Unknown;

    internal static FeatureState Evaluate(int feature)
    {
        if (!Index(feature, Count)) return Unknown;
        var f = At(feature);
        if (!Installed[feature]) return Pending;
        if (f.ServerOnly && _remote) return NotApplicable;
        if (f.Probe?.Invoke() is { } probed && probed != Active) return probed;
        if (Rivalled[feature]) return StoodDown;
        if (Holds[feature] > 0) return HeldOff;
        if (f.Knobs.Length == 0) return Active;
        foreach (var knob in f.Knobs.Bounded(Knobs.MaxKnobs))
            if (knob.Get() != knob.Engine)
                return Active;
        return Off;
    }

    public static FeatureState StateOf(int feature) =>
        Index(feature, Count) ? States[feature] : Unknown;

    // For the HUD's hint, kept until a state changes; "" when nothing is troubled
    public static string FirstTroubled()
    {
        if (_firstTroubled is { } text) return text;
        _firstTroubled = "";
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
        {
            if (!IsTrouble(States[i])) continue;
            var key = States[i] switch
            {
                StoodDown => "hud-feature-stooddown",
                EngineChanged => "hud-feature-changed",
                _ => "hud-feature-failed"
            };
            _firstTroubled = At(i).Id + ": " + KometText.Translate(key);
            break;
        }

        return _firstTroubled;
    }

    // Feature id or knob key (UploadCap: ChunkBudget); -1 for neither
    public static int Find(string id)
    {
        if (!NotNull(id)) return -1;
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (string.Equals(At(i).Id, id, StringComparison.Ordinal))
                return i;
        var knob = Knobs.Find(id);
        return knob >= 0 ? Knobs.At(knob).Owner : -1;
    }

    internal static bool Hold(int feature)
    {
        if (!Index(feature, Count) || !Assert(Holds[feature] < MaxHolds)) return false;
        var knobs = At(feature).Knobs;
        if (knobs.Length == 0) return false;
        if (Holds[feature]++ == 0)
            foreach (var knob in knobs.Bounded(Knobs.MaxKnobs))
                Knobs.Park(Knobs.Find(knob.Key));
        Poll();
        return true;
    }

    internal static bool Release(int feature)
    {
        if (!Index(feature, Count) || Holds[feature] == 0) return false;
        if (--Holds[feature] == 0)
            foreach (var knob in At(feature).Knobs.Bounded(Knobs.MaxKnobs))
                Knobs.Restore(Knobs.Find(knob.Key));
        Poll();
        return true;
    }

    // A refused hold (feature -1, e.g. the server thread's) holds nothing; off the main thread Handles is not ours to change
    internal static bool Release(FeatureHold hold) =>
        NotNull(hold) && hold.Feature >= 0 && !OffMain("releasing", hold.FeatureId) && Handles.Remove(hold) &&
        Release(hold.Feature);

    public static bool Held(int knob) =>
        Index(knob, Knobs.Count) && Index(Knobs.At(knob).Owner, Count) && Holds[Knobs.At(knob).Owner] > 0;

    // First holder and reason under a held knob's option; null while free
    public static string? LockText(int knob)
    {
        if (!Index(knob, Knobs.Count) || !Held(knob)) return null;
        var text = KometText.Translate("settings-held");
        if (Holder(Knobs.At(knob).Owner) is { } hold) text += "\n" + hold.ModId + ": " + hold.Reason;
        return text;
    }

    public static string HoldsText()
    {
        var text = new StringBuilder();
        for (var i = 0; i < Math.Min(Count, MaxFeatures); i++)
            if (Holds[i] > 0)
                _ = text.Append(text.Length > 0 ? ", " : "").Append(At(i).Id)
                    .Append(CultureInfo.InvariantCulture, $" ({Holds[i]})");
        return text.Length > 0 ? text.ToString() : "none";
    }

    public static bool IsExternalId(string id)
    {
        if (!NotNull(id) || id.Length > MaxId) return false;
        return ExternalId.IsMatch(id);
    }

    // Other mods' features and holds, indexed after Komet's. Registered before start: installed at InstallQueued; while
    // running: at once; after LevelFinalize: refused. A mod's throw fails only its feature; world close drops them all.
    // Main thread only: in singleplayer a mod's Start runs again on the server's thread, and that second call is ignored.
    public const int MaxExternal = 32;

    private static readonly List<External> Added = [];
    private static readonly List<FeatureHold> Handles = [];
    private static FeatureContext? _context;
    private static bool _sealed; // LevelFinalize has passed

    private static int Count => Table.Length + Added.Count;

    private static ILogger? Log => _context?.Logger ?? ApiEvents.Logger;

    private static Feature At(int feature)
    {
        if (!Index(feature, Count)) return Table[0];
        return feature < Table.Length ? Table[feature] : Added[feature - Table.Length].Feature;
    }

    public static void InstallQueued(FeatureContext context)
    {
        if (!NotNull(context)) return;
        _context = context;
        for (var i = 0; i < Math.Min(Added.Count, MaxExternal); i++)
            if (!Installed[Added[i].Slot])
                InstallAt(context, Added[i].Slot);
        Poll();
    }

    internal static bool Register(FeatureDefinition definition)
    {
        if (!NotNull(definition) || OffMain("registering", definition.Id)) return false;
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

    private static string? Refusal(FeatureDefinition definition)
    {
        if (!IsExternalId(definition.Id) || definition.Title.Length == 0)
            return "its id is not modid:name (a lower-case mod id; letters, digits, _ and - in the name) or its " +
                   "title is empty";
        if (_sealed) return "the world has loaded: register in StartClientSide";
        if (Find(definition.Id) >= 0) return "the id is taken";
        if (Count >= MaxFeatures || Added.Count >= MaxExternal ||
            (definition.Knob is not null && Knobs.Count >= Knobs.MaxKnobs))
            return "Komet holds as many features as it can";
        if (definition.Knob is { Valid: false }) return "its knob's range is empty or leaves out its engine value";
        if (definition.Fingerprint != 0 && definition.Shaped is null) return "it has a fingerprint but no Shaped";
        if (definition.Watched is not null && definition.Knob is null)
            return "it watches methods but has no knob to stand down with"; // its patches would stay in
        return definition.Watched is not null && (definition.StandDownFor & PatchKinds.All) == PatchKinds.None
            ? "it watches methods but stands down for no patch kind"
            : null;
    }

    // Logged at Debug only: the server thread's repeat of a singleplayer mod's Start is expected, not the mod's mistake
    private static bool OffMain(string what, string id)
    {
        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) return false;
        Log?.Debug("Komet: {0} {1} ignored off the main thread", what, id);
        return true;
    }

    private static Feature Describe(External external)
    {
        var (definition, slot) = (external.Definition, external.Slot);
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

    private static void Mount(FeatureContext context, External external)
    {
        var definition = external.Definition;
        if (!Assert(external.Harmony is null)) return;
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

    private static bool Shaped(FeatureDefinition definition, ILogger logger)
    {
        if (!NotNull(definition.Shaped)) return false;
        var shaped = definition.Shaped();
        MethodBase?[] methods = shaped is { Count: > 0 and <= EngineShape.MaxMethods } ? [.. shaped] : [];
        if (methods.Length == 0)
            logger.Warning("Komet: {0} is not installed, its Shaped names no method or more than {1}", definition.Id,
                EngineShape.MaxMethods);
        return methods.Length > 0 &&
               EngineShape.Matches(methods, definition.Fingerprint, definition.Id, logger, definition.ModId);
    }

    private static void Watch(External external)
    {
        var definition = external.Definition;
        if (definition.Watched is null || !external.Mounted || external.Failed) return;
        try
        {
            MethodBase?[] seams = definition.Watched() is { } list ? [.. list.Take(EngineShape.MaxSeams)] : [];
            var kinds = definition.StandDownFor & PatchKinds.All;
            var foreign = seams.Length > 0 && EngineShape.Foreign(seams, kinds, definition.Id);
            Stand(external, EngineShape.Report(Log, definition.Id, external.Foreign, foreign));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fail(external, e);
        }
    }

    // A stand-down holds the knob at engine value like a mod's hold
    private static void Stand(External external, bool foreign)
    {
        if (!Index(external.Slot, Count) || foreign == external.Foreign) return;
        external.Foreign = foreign;
        if (foreign) external.Parked = Hold(external.Slot);
        else if (external.Parked)
        {
            external.Parked = false;
            _ = Release(external.Slot);
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
    private static int Chosen(External external)
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
    private static void Choose(External external, int value)
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
        Poll();
    }

    // Logged once: a mod's getter that keeps throwing would log its stack trace at every compose of its options row
    private static void Fail(External external, Exception e)
    {
        if (!external.Failed)
            Log?.Error("Komet: {0} threw and is taken out, the engine's code runs: {1}", external.Definition.Id, e);
        external.Failed = true;
        Unpatch(external);
    }

    private static void Unpatch(External external)
    {
        external.Harmony?.UnpatchAll(external.Definition.Id);
        external.Harmony = null;
    }

    // World closes: the mod's cleanup, then its patches go
    private static void Unmount(External external)
    {
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

    private static void Drop()
    {
        foreach (var external in Added.Bounded(MaxExternal)) Unmount(external);
        Array.Clear(States, Table.Length, MaxFeatures - Table.Length);
        Array.Clear(Holds, Table.Length, MaxFeatures - Table.Length);
        Added.Clear();
        Handles.Clear();
        Knobs.Truncate();
        (_context, _sealed) = (null, false);
    }

    // Knob features only; one of MaxHolds is reserved for a stand-down
    internal static FeatureHold HoldOff(string id, string modId, string reason)
    {
        if (!NotNull(id) || OffMain("holding off", id)) return new FeatureHold("", modId, reason, -1);
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

    internal static bool Holding(FeatureHold hold) =>
        NotNull(hold) && Handles.Contains(hold);

    private static FeatureHold? Holder(int feature)
    {
        foreach (var hold in Handles.Bounded(MaxHolds))
            if (hold.Feature == feature)
                return hold;
        return null;
    }

    internal static FeatureInfo[] Snapshot()
    {
        var infos = new FeatureInfo[Math.Min(Count, MaxFeatures)];
        for (var i = 0; i < Math.Min(infos.Length, MaxFeatures); i++) infos[i] = Info(i);
        return infos;
    }

    private static FeatureInfo Info(int feature)
    {
        if (!Index(feature, Count))
            return new FeatureInfo("?", "?", "?", Unknown, false);
        var definition = feature >= Table.Length ? Added[feature - Table.Length].Definition : null;
        var (f, hold) = (At(feature), Holder(feature));
        var (title, owner) = (definition?.Title ?? f.Id, definition?.ModId ?? KometMod.Id);
        return new FeatureInfo(f.Id, title, owner, Evaluate(feature), f.Knobs.Length > 0)
            { HeldBy = hold?.ModId, Reason = hold?.Reason };
    }

    // Other mods' knobs in registration order under their group (on Komet's pages: mod id), locked while held
    internal static OptionPage AddRows(OptionPage page, bool komet)
    {
        if (!NotNull(page)) return page;
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

    internal static OptionPage Placed(OptionPage page)
    {
        if (!NotNull(page)) return page;
        foreach (var external in Added.Bounded(MaxExternal))
            if (external.Definition.Page == page.Id && external.Knob >= 0)
                return AddRows(page.Copy(), false);
        return page;
    }

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
