using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Komet.Chunks;

// The integrated server's chunk thread calls loadOrGenerateChunkColumn_OnChunkThread for every queued request on every tick, and most
// calls just requeue a column that is not ready. The method captures chunkRequest in a lambda it builds only once a column is done,
// but the compiler allocates the closure in the prologue: 7.5 GB of closures in one world join, a fifth of all allocation, on the heap
// the client shares in singleplayer. The rewrite reads the argument directly and builds the closure where the delegate is made. On a
// dedicated server Komet is not installed and nothing changes.
internal static class ChunkThreadClosure
{
    private const int MaxInstructions = 4096, Prologue = 8;

    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static MethodInfo? Target()
    {
        var supply = AccessTools.TypeByName("Vintagestory.Server.ServerSystemSupplyChunks"); // internal
        var request = AccessTools.TypeByName("Vintagestory.Server.ChunkColumnLoadRequest");
        if (!NotNull(supply) || !NotNull(request)) return null;
        var method = AccessTools.Method(supply, "loadOrGenerateChunkColumn_OnChunkThread", [request, typeof(int)]);
        return NotNull(method) && Assert(method.ReturnType == typeof(bool)) ? method : null;
    }

    // The whole shape is checked before anything changes: the closure is built in the first eight instructions, and every later read
    // of local 0 loads chunkRequest from it except exactly one, which hands it to ldftn. Anything else keeps the engine's IL.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !Assert(code.Count > Prologue))
            return code;
        if (code[0].opcode != OpCodes.Newobj || code[0].operand is not ConstructorInfo ctor) return code;
        var closure = ctor.DeclaringType;
        var self = AccessTools.Field(closure, "<>4__this");
        var request = AccessTools.Field(closure, "chunkRequest");
        if (!NotNull(closure) || !NotNull(self) || !NotNull(request) ||
            !Assert(IsPrologue(code, self, request))) return code;
        var delegateAt = -1;
        for (var i = Prologue; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            var local = Il.Local(code[i]);
            if (WritesArgument(code[i]) || (local == 0 && Il.Local(code[i], Il.Uses.Load) != 0))
                return code; // chunkRequest could change
            if (local != 0 || (i + 1 < code.Count && code[i + 1].LoadsField(request))) continue;
            if (i + 1 >= code.Count || code[i + 1].opcode != OpCodes.Ldftn || !Assert(delegateAt < 0)) return code;
            delegateAt = i;
        }

        if (!Assert(delegateAt > 0)) return code;
        for (var i = Prologue; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (Il.Local(code[i], Il.Uses.Load) != 0 || i == delegateAt) continue;
            // In place: labels and exception blocks stay on it
            (code[i].opcode, code[i].operand) = (OpCodes.Ldarg_1, null);
            (code[i + 1].opcode, code[i + 1].operand) = (OpCodes.Nop, null);
        }

        for (var i = 0; i < Prologue; i++) (code[i].opcode, code[i].operand) = (OpCodes.Nop, null);
        (code[delegateAt].opcode, code[delegateAt].operand) = (OpCodes.Newobj, ctor);
        code.InsertRange(delegateAt + 1,
        [
            new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Stfld, self),
            new CodeInstruction(OpCodes.Dup), new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Stfld, request)
        ]);
        Rewritten = true;
        return code;
    }

    // newobj, stloc.0, ldloc.0, ldarg.0, stfld this, ldloc.0, ldarg.1, stfld chunkRequest - with no branch landing inside
    private static bool IsPrologue(List<CodeInstruction> code, FieldInfo self, FieldInfo request)
    {
        if (!Assert(code.Count > Prologue) || !NotNull(self)) return false;
        for (var i = 0; i < Prologue; i++)
            if (code[i].labels.Count > 0 || code[i].blocks.Count > 0) return false;

        return Il.Local(code[1], Il.Uses.Store) == 0 && Il.Local(code[2], Il.Uses.Load) == 0 && code[3].IsLdarg(0) &&
               code[4].StoresField(self) && Il.Local(code[5], Il.Uses.Load) == 0 && code[6].IsLdarg(1) &&
               code[7].StoresField(request);
    }

    // chunkRequest is argument 1: a store or an address taken would let the method change it after the rewrite reads it directly
    private static bool WritesArgument(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return true;
        var op = code.opcode;
        return (op == OpCodes.Starg || op == OpCodes.Starg_S || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S) &&
               code.operand is 1 or (byte)1 or (short)1 or ParameterInfo { Position: 0 };
    }
}
