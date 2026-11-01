using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Diagnostics;
using Vintagestory.Client.NoObf;

namespace Komet.Shapes;

// First-draw inventory meshes of creatures and wearables cost 5-15 ms each (a creative row of them: 150 ms). In the
// creative inventory such a first draw happens only while BudgetMs of the frame is left; the rest wait for a later
// frame. Only expensive ones wait: a collectible drawn before always draws (its mesh is kept), as does a new one unless
// its class is expensive: one that builds a creature or wearable mesh, or whose first draw of any of its collectibles
// took LearnMs or more (mods' classes are learnt; never the plain Item and Block classes).
internal static class IconBudget
{
    public const double BudgetMs = 6;
    private const double LearnMs = 3;
    private const int MaxDepth = 16;
    private const string Creative = "creative";

    private static readonly ConditionalWeakTable<CollectibleObject, object> Drawn = [];
    private static readonly ConcurrentDictionary<Type, bool> Classes = new(); // expensive?
    private static readonly ConcurrentDictionary<Type, bool> Behaviors = new(); // a wearable?
    private static readonly object Seen = new();
    private static long _frame = -1, _spent, _budget;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    internal static MethodInfo? Target() => AccessTools.Method(typeof(InventoryItemRenderer), nameof(InventoryItemRenderer.RenderItemstackToGui),
        [typeof(ItemSlot), typeof(double), typeof(double), typeof(double), typeof(float), typeof(int), typeof(float), typeof(bool), typeof(bool),
            typeof(bool)]);

    public static void Install(Harmony harmony)
    {
        Patched = false;
        if (!NotNull(harmony) || Target() is not { } target) return;
        _ = harmony.Patch(target, new HarmonyMethod(Drawing), new HarmonyMethod(Done));
        Patched = true;
    }

    // Creature items and wearables (a wearable behaviour, or a mod's subclass of one, since 1.22; the old ItemWearable class
    // before) build whole-body meshes from their shapes at the first draw; other classes as learnt
    internal static bool Heavy(CollectibleObject collectible) =>
        Classes.GetOrAdd(collectible.GetType(), static type => Named(type, typeof(CollectibleObject))) ||
        Wearable(collectible);

    private static bool Wearable(CollectibleObject collectible)
    {
        foreach (var behavior in (collectible.CollectibleBehaviors ?? []).Bounded(MaxDepth * 4))
            if (behavior is not null && Behaviors.GetOrAdd(behavior.GetType(), static type => Named(type, typeof(CollectibleBehavior))))
                return true;
        return false;
    }

    // The type or a base type below root is a creature item or a wearable (CollectibleBehaviorWearable derives from
    // CollectibleBehaviorWearableAttachment, which builds the same meshes)
    private static bool Named(Type type, Type root)
    {
        var t = type;
        for (var depth = 0; depth < MaxDepth && t is not null && t != root; depth++, t = t.BaseType)
            if (t.Name is "ItemCreature" or "ItemWearable" or "CollectibleBehaviorWearable" or "CollectibleBehaviorWearableAttachment")
                return true;
        return false;
    }

    // False skips the draw this frame: an expensive creative first draw once the frame's budget is spent
    private static bool Drawing(ItemSlot inSlot, out long __state)
    {
        __state = 0;
        if (!Enabled || FrameClock.Frames == 0 || inSlot?.Inventory?.ClassName != Creative ||
            inSlot.Itemstack?.Collectible is not { } collectible) return true;
        if (Drawn.TryGetValue(collectible, out _)) return true; // drawn before: its mesh is kept
        if (FrameClock.Frames != _frame) // not Completed: it stands still without the HUD
            (_frame, _spent, _budget) = (FrameClock.Frames, 0, (long)(BudgetMs * Stopwatch.Frequency / 1000));
        if (Heavy(collectible) && _spent >= _budget) return false;

        __state = Stopwatch.GetTimestamp();
        return true;
    }

    private static void Done(ItemSlot inSlot, long __state)
    {
        if (__state == 0 || inSlot?.Itemstack?.Collectible is not { } collectible) return;
        var ticks = Stopwatch.GetTimestamp() - __state;
        _spent += ticks;
        var ms = ticks * 1000.0 / Stopwatch.Frequency;
        Drawn.AddOrUpdate(collectible, Seen);
        // its class's next collectibles wait their turn (a slow first item or block alone teaches nothing)
        if (ms >= LearnMs && collectible.GetType() is var type && type != typeof(Item) && type != typeof(Block)) Classes[type] = true;
    }
}
