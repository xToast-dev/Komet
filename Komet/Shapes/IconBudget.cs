using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Shapes;

// Many items build their inventory mesh the first time they are drawn (clutter variants, containers, clothing, creature items:
// tesselated, textures baked, uploaded), 5 to 15 ms each. A row of the creative inventory scrolling in drew fifteen of them in one
// frame, 150 ms. Now the creative inventory draws items it has not drawn before only while BudgetMs of this frame is left; the
// others stay empty for the frames until their turn, slots and stack sizes drawn as before. An item drawn once is drawn every frame.
internal static class IconBudget
{
    public const double BudgetMs = 6;
    private const string Creative = "creative";

    private static readonly ConditionalWeakTable<ItemSlot, object> Drawn = [];
    private static readonly object Seen = new();
    private static long _frame = -1, _spent;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    internal static MethodInfo? Target() => !Assert(BudgetMs > 0) ? null : AccessTools.Method(typeof(InventoryItemRenderer), nameof(InventoryItemRenderer.RenderItemstackToGui),
        [typeof(ItemSlot), typeof(double), typeof(double), typeof(double), typeof(float), typeof(int), typeof(float), typeof(bool), typeof(bool),
            typeof(bool)]);

    public static void Install(Harmony harmony)
    {
        Patched = false;
        if (!NotNull(harmony) || !Assert(BudgetMs > 0) || Target() is not { } target) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Drawing), new HarmonyMethod(Done)));
    }

    // False skips the draw this frame: a creative slot drawn for the first time once the frame's budget is spent
    private static bool Drawing(ItemSlot inSlot, out long __state)
    {
        __state = 0;
        if (!Assert(_spent >= 0) || !Enabled || inSlot?.Inventory?.ClassName != Creative || Drawn.TryGetValue(inSlot, out _)) return true;
        if (FrameClock.Completed != _frame) (_frame, _spent) = (FrameClock.Completed, 0);
        if (_spent >= BudgetMs * Stopwatch.Frequency / 1000 || !Assert(_spent >= 0)) return false;
        __state = Stopwatch.GetTimestamp();
        return true;
    }

    private static void Done(ItemSlot inSlot, long __state)
    {
        if (__state == 0 || !NotNull(inSlot) || !Assert(__state > 0)) return;
        _spent += Stopwatch.GetTimestamp() - __state;
        _ = Assert(_spent >= 0);
        Drawn.AddOrUpdate(inSlot, Seen);
    }
}
