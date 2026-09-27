using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Komet.World;

// Every climate lookup ends in EventManager.TriggerOnGetClimate (GetClimateAt, 34k cloud tiles every 40 ms on the cloud thread
// alone), and every wind lookup in TriggerOnGetWindSpeed; both copy the event's handlers into a new array (GetInvocationList) to call
// them in turn: 0.7 MB/s. A delegate never changes - adding or removing a handler makes a new one - so its list is the same every time.
// The triggers now get the list made for the same delegate before, kept in a few slots by the delegate's identity, and a new list only
// when the event changed. The array is only read (its length and its elements, checked on the IL), so sharing it between calls and
// threads changes nothing. Leaving the world empties the slots.
internal static class HandlerLists
{
    private const int MaxInstructions = 256, SlotCount = 16, ClimateBit = 1, WindBit = 2, AllBits = 3;

    private static readonly Entry?[] Slots = new Entry?[SlotCount];
    private static int _rewritten;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == AllBits;

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;
        _ = Clear();
        var manager = typeof(EventManager);
        var climate = AccessTools.DeclaredMethod(manager, nameof(EventManager.TriggerOnGetClimate),
            [typeof(ClimateCondition).MakeByRefType(), typeof(BlockPos), typeof(EnumGetClimateMode), typeof(double)]);
        var wind = AccessTools.DeclaredMethod(manager, nameof(EventManager.TriggerOnGetWindSpeed),
            [typeof(Vec3d), typeof(Vec3d).MakeByRefType()]);
        if (!NotNull(harmony) || !NotNull(climate) || !NotNull(wind)) return;
        _ = NotNull(harmony.Patch(climate, transpiler: new HarmonyMethod(Rewrite)));
        _ = NotNull(harmony.Patch(wind, transpiler: new HarmonyMethod(Rewrite)));
    }

    // Leaving the world: no handler of it stays reachable through a slot
    internal static bool Clear()
    {
        Array.Clear(Slots);
        return Assert(Slots.Length == SlotCount);
    }

    // The method's one GetInvocationList goes through Of when the array it makes is only stored once and then read by length and
    // index; any other shape keeps the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var bit = original?.Name == nameof(EventManager.TriggerOnGetWindSpeed) ? WindBit : ClimateBit;
        _rewritten &= ~bit;
        var (list, of) = (AccessTools.Method(typeof(Delegate), nameof(Delegate.GetInvocationList)),
            AccessTools.Method(typeof(HandlerLists), nameof(Of)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(list) || !NotNull(of))
            return code;
        var site = Il.Single(code, c => c.Calls(list));
        if (!Index(site + 1, code.Count) || !code[site + 1].IsStloc() ||
            !Il.Confined(code, site + 1, (use, operand) => operand == 0 && NotNull(use) &&
                (use.opcode == OpCodes.Ldlen || use.opcode == OpCodes.Ldelem_Ref))) return code;
        if (Il.Substitute(code, site, of)) _rewritten |= bit;
        return code;
    }

    // Stands in for handlers.GetInvocationList(): the list made for this delegate before, else a new one, kept for next time
    internal static Delegate[] Of(Delegate handlers)
    {
        if (!Enabled) return handlers.GetInvocationList(); // a null delegate throws as the engine's callvirt did
        var slot = (int)((uint)RuntimeHelpers.GetHashCode(handlers) % SlotCount);
        if (Volatile.Read(ref Slots[slot]) is { } kept && ReferenceEquals(kept.Handlers, handlers)) return kept.List;
        var made = handlers.GetInvocationList();
        Volatile.Write(ref Slots[slot], new Entry(handlers, made));
        _ = Assert(made.Length > 0);
        return made;
    }

    private sealed record Entry(Delegate Handlers, Delegate[] List);
}
