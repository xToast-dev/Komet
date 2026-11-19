using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Common;

namespace Komet.Chunks;

internal static class ChunkFactories
{
    private static readonly ConditionalWeakTable<ChunkDataPool, Func<int[]>> Factories = [];
    private static readonly MethodInfo Factory = AccessTools.DeclaredMethod(typeof(ChunkFactories), nameof(ForPool));
    private static readonly MethodInfo? NewData = AccessTools.Method(typeof(ChunkDataPool), "NewData");

    internal static void Install(Harmony harmony)
    {
        if (NewData is null) return;

        foreach (var name in (ReadOnlySpan<string>)["Decompress", "DecompressSeparate"])
        {
            if (AccessTools.DeclaredMethod(typeof(ChunkDataLayer), name) is { } target)
            {
                _ = harmony.Patch(target, transpiler: new HarmonyMethod(Rewrite));
            }
        }
    }

    internal static Func<int[]> ForPool(ChunkDataPool pool) =>
        DecompressScratch.Enabled
            ? Factories.GetValue(pool, static source => NewData!.CreateDelegate<Func<int[]>>(source))
            : NewData!.CreateDelegate<Func<int[]>>(pool);

    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        if (!Il.Take(instructions, Il.MaxInstructions, out var code)) return code;

        var at = Il.Single(code, c => c.opcode == OpCodes.Ldftn && Equals(c.operand, NewData));
        if (at < 0 || at + 1 >= code.Count ||
            code[at + 1].opcode != OpCodes.Newobj ||
            code[at + 1].operand is not ConstructorInfo { DeclaringType: var type } ||
            type != typeof(Func<int[]>) || code[at + 1].labels.Count != 0 ||
            code[at + 1].blocks.Count != 0)
        {
            return code;
        }

        (code[at].opcode, code[at].operand) = (OpCodes.Call, Factory);
        (code[at + 1].opcode, code[at + 1].operand) = (OpCodes.Nop, null);
        return code;
    }
}
