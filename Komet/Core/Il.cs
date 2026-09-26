using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Komet.Core;

// The install-time IL analysis the transpilers share, and Binds for the patches Harmony hands arguments by name. A transpiler hands
// the engine's IL back unchanged whenever the body is not the shape it was written for, so every question here answers no (false, -1)
// when it cannot prove yes. The helpers do not assert on a shape they do not find: whether that is unexpected is the caller's to say,
// at its own call site.
internal static class Il
{
    // The instructions that name a local: ldloc, ldloca, stloc
    [Flags]
    public enum Uses
    {
        None = 0,
        Load = 1,
        Address = 2,
        Store = 4,
        Any = Load | Address | Store
    }

    public const int MaxInstructions = 1 << 13, MaxReach = 32;
    private const int MaxParameters = 32;

    private static readonly string[] Injected =
    [
        "__instance", "__originalMethod", "__args", "__result", "__resultRef", "__state", "__exception", "__runOriginal"
    ];

    // The whole body: a truncated one would be invalid IL, so a transpiler rejects a body past its cap rather than cutting it. True
    // when it has at most `max` instructions.
    public static bool Take(IEnumerable<CodeInstruction> instructions, int max, out List<CodeInstruction> code)
    {
        code = NotNull(instructions) ? [.. instructions] : [];
        return Assert(max is > 0 and <= MaxInstructions) && code.Count <= max;
    }

    // How many instructions match; -1 for a body past MaxInstructions
    public static int Count(List<CodeInstruction> code, System.Func<CodeInstruction, bool> match)
    {
        if (!NotNull(code) || !NotNull(match) || !Assert(code.Count <= MaxInstructions)) return -1;
        var count = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++) count += match(code[i]) ? 1 : 0;
        return count;
    }

    // The index of the one instruction that matches; -1 when none or several do
    public static int Single(List<CodeInstruction> code, System.Func<CodeInstruction, bool> match)
    {
        if (!NotNull(code) || !NotNull(match) || !Assert(code.Count <= MaxInstructions)) return -1;
        var at = -1;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (!match(code[i])) continue;
            if (at >= 0) return -1;
            at = i;
        }

        return at;
    }

    // The instruction at `at` becomes a call of the static `method` in place, so its labels and exception blocks stay where they were.
    // Only for a replacement with the same stack effect (checked); false, and nothing changed, otherwise.
    public static bool Substitute(List<CodeInstruction> code, int at, MethodInfo method)
    {
        if (!NotNull(code) || !NotNull(method) || !Index(at, code.Count) || !Assert(method.IsStatic)) return false;
        var effect = Effect(code[at]);
        if (effect.Pops < 0 || Effect(new CodeInstruction(OpCodes.Call, method)) != effect) return false;
        (code[at].opcode, code[at].operand) = (OpCodes.Call, method);
        return true;
    }

    // The local an ldloc, ldloca or stloc refers to when it is one of the uses asked for, -1 for anything else. The short forms come
    // without an operand, the others with a LocalBuilder (or the bare index).
    public static int Local(CodeInstruction code, Uses uses = Uses.Any)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return -1;
        var op = code.opcode;
        var use = Uses.None;
        if (code.IsStloc()) use = Uses.Store;
        // IsLdloc takes both
        else if (code.IsLdloc()) use = op == OpCodes.Ldloca || op == OpCodes.Ldloca_S ? Uses.Address : Uses.Load;
        if ((use & uses) == Uses.None) return -1;
        if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return 0;
        if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return 1;
        if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return 2;
        if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return 3;
        return code.operand switch
        {
            LocalBuilder builder => builder.LocalIndex,
            byte index => index,
            short index => index,
            int index => index,
            _ => -1
        };
    }

    // The instruction that takes the value pushed at `at` off the stack, and as which operand (0 the first, `this` of an instance call),
    // found by walking at most `reach` instructions forward over ones that neither jump nor are jumped to; (-1, -1) when that proves
    // nothing
    public static (int At, int Operand) Consumer(List<CodeInstruction> code, int at, int reach = MaxReach)
    {
        if (!NotNull(code) || !Index(at, code.Count) || !Assert(reach is > 0 and <= MaxReach)) return (-1, -1);
        var height = 1; // the tracked value and everything pushed on top of it
        for (var step = 1; step <= Math.Min(Math.Min(code.Count - at - 1, reach), MaxReach); step++)
        {
            var i = at + step;
            var c = code[i];
            if (c.labels.Count > 0 || c.blocks.Count > 0) return (-1, -1);
            var (pops, pushes) = Effect(c);
            if (pops < 0) return (-1, -1);
            if (pops >= height) return (i, pops - height);
            if (c.opcode.FlowControl is not (FlowControl.Next or FlowControl.Call)) return (-1, -1);
            height += pushes - pops;
            if (!Assert(height > 0)) return (-1, -1);
        }

        return (-1, -1);
    }

    // Values an instruction takes from and puts on the stack; (-1, -1) for anything whose count depends on more than its operand
    public static (int Pops, int Pushes) Effect(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return (-1, -1);
        var op = code.opcode;
        if (op.FlowControl == FlowControl.Call || op == OpCodes.Newobj)
        {
            if (code.operand is not MethodBase m || op == OpCodes.Calli) return (-1, -1);
            var pops = m.GetParameters().Length + (m.IsStatic || op == OpCodes.Newobj ? 0 : 1);
            return (pops, op == OpCodes.Newobj || (m is MethodInfo i && i.ReturnType != typeof(void)) ? 1 : 0);
        }

        var popped = op.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            _ => -1
        };
        var pushed = op.StackBehaviourPush switch
        {
            StackBehaviour.Push0 => 0,
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => -1
        };
        return popped < 0 || pushed < 0 ? (-1, -1) : (popped, pushed);
    }

    // Whether the local the stloc at `store` fills stays in the method: every other instruction naming it is one of the `uses` (plain
    // loads, or addresses for a struct whose members are called) and hands it, as Consumer follows it, to an instruction `allowed` takes
    // as that operand. A second store, a use of another kind or one Consumer cannot follow is an escape.
    public static bool Confined(List<CodeInstruction> code, int store, System.Func<CodeInstruction, int, bool> allowed,
        Uses uses = Uses.Load, int reach = MaxReach)
    {
        if (!NotNull(code) || !NotNull(allowed) || !Index(store, code.Count) || !Assert(code[store].IsStloc()))
            return false;
        var local = Local(code[store]);
        if (local < 0 || !Assert(uses is Uses.Load or Uses.Address or (Uses.Load | Uses.Address)) ||
            !Assert(code.Count <= MaxInstructions))
            return false;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (i == store || Local(code[i]) != local) continue;
            if (Local(code[i], uses) != local) return false;
            var (use, operand) = Consumer(code, i, reach);
            if (use < 0 || !allowed(code[use], operand)) return false;
        }

        return true;
    }

    // Whether Harmony can bind every parameter of the patch by name. It does so while it builds the replacement and throws on one it
    // cannot (2.4 MethodCreatorTools.EmitCallParameter): ___x is a field of the original's type or a base, the Injected names are its
    // own, any other name is a parameter of the original.
    public static bool Binds(MethodBase original, MethodInfo patch)
    {
        if (!NotNull(original) || !NotNull(patch)) return false;
        var (wanted, given) = (patch.GetParameters(), original.GetParameters());
        if (!Assert(wanted.Length <= MaxParameters) || !Assert(given.Length <= MaxParameters)) return false;
        for (var i = 0; i < Math.Min(wanted.Length, MaxParameters); i++)
        {
            var name = wanted[i].Name ?? "";
            var bound = name.StartsWith("___", StringComparison.Ordinal)
                ? AccessTools.Field(original.DeclaringType, name[3..]) is not null
                : Array.IndexOf(Injected, name) >= 0 || Array.Exists(given, parameter => parameter.Name == name);
            if (!bound) return false;
        }

        return true;
    }
}
