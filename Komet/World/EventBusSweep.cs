using System.Reflection;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.World;

// On the client BlockEntityDisplay (ground storage, shelves, display cases and the rest), the tool and mold racks, the firepit and
// BEBehaviorDisplay register an event bus listener in Initialize and never unregister it: not when their chunk unloads, not when the
// block is broken. ClientEventManager.EventBusListeners keeps every one the session ever loaded, and with it the block entity, its
// inventory and stacks. RegisterEventBusListener walks the whole list to place each new one (all at the default priority), so every
// display block entity that loads costs more than the one before (0.1 ms at 50 000 listeners, 0.2 ms at 100 000, measured on the
// engine's code with a compact heap) and every PushEvent (chat keys, item pickups) calls all of them.
//
// A block entity whose base OnBlockUnloaded or OnBlockRemoved ran on the client is gone for good: the engine drops it from its chunk
// and never initialises that object again. After the frame's main-thread tasks, one pass removes the listeners whose target is such a
// block entity or one of its behaviours, the ones the block entity itself would have to unregister. A dead one's handler only ever
// redid what the live block entity at its place does itself (drop a shared mesh cache entry, mark the block dirty), so the events
// change nothing. One initialised again before the pass is taken off the list first. Only block entities seen registering a listener
// are remembered, so the pass, one walk of the list, runs only in frames where one of those died.
internal static class EventBusSweep
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x444714F8DA4CFC84UL;
    private const int MaxDead = 1 << 16, MaxListening = 1 << 20;

    // Main thread: the block entities that registered a listener themselves or through a behaviour, and those of them gone since the
    // last pass. Only a death among the first makes a pass.
    private static readonly HashSet<BlockEntity> Listening = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<BlockEntity> Dead = new(ReferenceEqualityComparer.Instance);
    private static readonly Predicate<EventBusListener> Gone = static listener => listener?.handler?.Target switch
    {
        BlockEntity entity => Dead.Contains(entity),
        BlockEntityBehavior { Blockentity: { } entity } => Dead.Contains(entity),
        _ => false
    };

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // While Counting.Hud: listeners removed in total, and how many the list held after the last pass
    public static long Swept { get; private set; }
    public static int Listeners { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (Installed, Swept, Listeners) = (false, 0, 0);
        Clear();
        var seams = Seams();
        if (!NotNull(harmony) || !Assert(seams.Length == 3) || !Assert(!Array.Exists(seams, s => s is null)) ||
            !EngineShape.Matches(seams, fingerprint, nameof(EventBusSweep), logger)) return;
        var type = typeof(BlockEntity);
        MethodBase?[] targets =
        [
            AccessTools.DeclaredMethod(type, nameof(BlockEntity.OnBlockUnloaded)),
            AccessTools.DeclaredMethod(type, nameof(BlockEntity.OnBlockRemoved)),
            AccessTools.DeclaredMethod(type, nameof(BlockEntity.Initialize)),
            AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.ExecuteMainThreadTasks))
        ];
        if (!Assert(Array.TrueForAll(targets, t => t is not null)) ||
            !Assert(AccessTools.DeclaredField(typeof(ClientEventManager), nameof(ClientEventManager.EventBusListeners))
                ?.FieldType == typeof(List<EventBusListener>))) return;
        _ = NotNull(harmony.Patch(targets[0], postfix: new HarmonyMethod(Died)));
        _ = NotNull(harmony.Patch(targets[1], postfix: new HarmonyMethod(Died)));
        _ = NotNull(harmony.Patch(targets[2], new HarmonyMethod(Revived)));
        _ = NotNull(harmony.Patch(targets[3], postfix: new HarmonyMethod(Sweep)));
        _ = NotNull(harmony.Patch(seams[0], postfix: new HarmonyMethod(Registered)));
        Installed = true;
    }

    // Where the engine keeps the listeners, adds, removes and calls them: what the sweep relies on
    internal static MethodBase?[] Seams()
    {
        var api = typeof(ClientEventAPI);
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.RegisterEventBusListener)),
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.UnregisterEventBusListener)),
            AccessTools.DeclaredMethod(api, nameof(ClientEventAPI.PushEvent))
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // At world leave: the block entities go with the world
    public static void Clear()
    {
        Dead.Clear();
        Listening.Clear();
        _ = Assert(Dead.Count == 0) && Assert(Listening.Count == 0);
    }

    // Postfix on RegisterEventBusListener, whose parameter Harmony hands over by its name
    internal static void Registered(EventBusListenerDelegate OnEvent)
    {
        var owner = OnEvent?.Target switch
        {
            BlockEntity entity => entity,
            BlockEntityBehavior behavior => behavior.Blockentity,
            _ => null
        };
        if (owner is not null && Assert(Listening.Count < MaxListening) &&
            Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) _ = Listening.Add(owner);
    }

    // Postfix on the base bodies, which every override that unloads or removes properly calls. A block entity never initialised has
    // registered nothing; the server's (singleplayer) are not the client's.
    internal static void Died(BlockEntity __instance)
    {
        if (!Enabled || !Installed || __instance?.Api is not ICoreClientAPI ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;
        if (Listening.Remove(__instance) && Assert(Dead.Count < MaxDead)) _ = Dead.Add(__instance);
    }

    internal static void Revived(BlockEntity __instance)
    {
        if (Dead.Count > 0 && Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId) _ = Dead.Remove(__instance);
        _ = Assert(Dead.Count <= MaxDead);
    }

    internal static void Sweep(ClientMain __instance)
    {
        if (Dead.Count == 0) return;
        if (Enabled && __instance?.eventManager?.EventBusListeners is { } listeners &&
            Assert(Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId))
        {
            var removed = listeners.RemoveAll(Gone);
            if (Counting.Hud) (Swept, Listeners) = (Swept + removed, listeners.Count);
        }

        _ = Assert(Dead.Count <= MaxDead);
        Dead.Clear();
    }
}
