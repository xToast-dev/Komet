using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;

namespace Komet.World;

// Three Initialize registrations search a session-growing list from the start: AddGameTickListenerBlockInternal,
// RegisterRenderer, RegisterEventBusListener. Each part knows where the search ends, else the engine searches:
// - Tick slots: no slot below the mark is empty; RemoveGameTickListener, the only null writer, lowers it.
// - Renderers: a stage proven sorted with fixed RenderOrders takes one above its last at the end.
// - Event bus: a list proven sorted by falling priority takes one at most its last's priority at the end.
// A proof holds for its List<T>._version; the engine's inserts and single removals carry it over.
internal static partial class ListenerSlots
{
    // EngineShape of TickSeams(), RendererSeams() and BusSeams() in Vintage Story 1.22.7
    internal const ulong TickFingerprint = 0x19C99C1EAAD550FFUL,
        RendererFingerprint = 0x04CA523EABBC56AAUL,
        BusFingerprint = 0x5AD59681C6AC02EBUL;

    private const int Ticks = 0, Renderers = 1, Bus = 2, Parts = 3;

    private const int MaxListeners = 1 << 24,
        MaxRenderers = 1 << 16,
        MaxReservations = 64,
        MaxStages = 64,
        MaxTypes = 4096;

    private const int MaxSteps = 64, MaxDepth = 32, MaxMembers = 4096, ConstantGetter = 10, FieldGetter = 6;

    private static readonly bool[] Shaped = new bool[Parts], Foreign = new bool[Parts], Patched = new bool[Parts];
    private static readonly MethodBase?[][] Seams = new MethodBase?[Parts][];

    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;

    // Bodies verified / foreign patch: that part leaves its registrations to the engine
    internal static bool Matched => Array.TrueForAll(Shaped, shaped => shaped);
    internal static bool Blocked => Array.Exists(Foreign, foreign => foreign);

    private static bool Main => Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId;

    public static void Install(Harmony harmony, ILogger? logger)
    {
        _logger = logger;
        Clear();
        Array.Clear(Shaped);
        Array.Clear(Foreign);
        Array.Clear(Patched);
        (Seams[Ticks], Seams[Renderers], Seams[Bus]) = (TickSeams(), RendererSeams(), BusSeams());

        Shaped[Ticks] = EngineShape.Matches(Seams[Ticks], TickFingerprint, nameof(ListenerSlots), logger);
        Shaped[Renderers] = EngineShape.Matches(Seams[Renderers], RendererFingerprint, nameof(ListenerSlots), logger);
        Shaped[Bus] = EngineShape.Matches(Seams[Bus], BusFingerprint, nameof(ListenerSlots), logger);
        if (Shaped[Ticks]) Patched[Ticks] = PatchTicks(harmony);

        if (Shaped[Renderers]) Patched[Renderers] = PatchRenderers(harmony);

        if (Shaped[Bus]) Patched[Bus] = PatchBus(harmony);

        Recheck();
    }

    // A foreign prefix, transpiler or infix on what a part answers or reads its proof off stands that part down
    internal static void Recheck()
    {
        var was = Blocked;
        for (var part = 0; part < Parts; part++)
        {
            Foreign[part] = Patched[part] && Seams[part] is { Length: > 0 } seams &&
                            EngineShape.Foreign(seams, KapiPatchKinds.Replacing, null, typeof(ListenerSlots));
        }

        _ = EngineShape.Report(_logger, nameof(ListenerSlots), was, Blocked);
    }

    // At world leave: the lists go with the world
    public static void Clear()
    {
        Slots.Clear();
        Array.Clear(Orders);
        Types.Clear();
        (_bus, _busSeen, _busProven) = (null, 0, false);
    }

    private static bool On(int part) => Enabled && Patched[part] && !Foreign[part];

    private static HarmonyMethod Own(string name) =>
        new(AccessTools.DeclaredMethod(typeof(ListenerSlots), name));

    // Whether Harmony binds every patch's parameters by name, checked before any goes in
    private static bool Binds(MethodBase? target, params ReadOnlySpan<string> patches)
    {
        if (!NotNull(target)) return false;

        for (var i = 0; i < Math.Min(patches.Length, MaxSteps); i++)
        {
            if (AccessTools.DeclaredMethod(typeof(ListenerSlots), patches[i]) is not { } patch ||
                !Il.Binds(target, patch))
            {
                return false;
            }
        }

        return true;
    }

    private static AccessTools.FieldRef<List<T>, int>? Versioned<T>() =>
        AccessTools.DeclaredField(typeof(List<T>), "_version")?.FieldType == typeof(int)
            ? AccessTools.FieldRefAccess<List<T>, int>("_version")
            : null;
}
