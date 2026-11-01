using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;

namespace Komet.World;

// ChunkMapLayer.OnTick (every 20 ms on the main thread) takes up to 200 finished map pieces at once and uploads their
// tiles: joining at view distance 1536 queues some 9000, which came in frames of up to 12 ms. A smaller share a tick
// spreads them over a few seconds; none is dropped, the rest waits in the engine's queue for the next tick.
// OnOffThreadTick saves its pieces at 100 waiting or every 4 s by diskSaveAccum, which nothing ever counts up (1.22.7):
// a few chunks explored stayed out of the map database (Komet's globe read it) until 100 came together or the world was
// left. Its prefix counts the seconds, on the map's own thread.
internal static class MapPieceBudget
{
    private const int MaxInstructions = 4096, Engine = 200;

    // 48 a tick: 2400 a second, the map of a 1536 join filled in about four seconds
    public const int PerTick = 48;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        var layer = AccessTools.TypeByName("Vintagestory.GameContent.ChunkMapLayer");
        var tick = layer is null ? null : AccessTools.DeclaredMethod(layer, "OnTick", [typeof(float)]);
        if (!NotNull(harmony) || tick is null) return;
        _ = harmony.Patch(tick, transpiler: new HarmonyMethod(AccessTools.Method(typeof(MapPieceBudget), nameof(Rewrite))));
        var offThread = AccessTools.DeclaredMethod(layer, "OnOffThreadTick", [typeof(float)]);
        if (offThread is not null && AccessTools.Field(layer, "diskSaveAccum")?.FieldType == typeof(float) &&
            AccessTools.Field(layer, "mapdb") is not null &&
            typeof(System.Collections.ICollection).IsAssignableFrom(AccessTools.Field(layer, "toSaveList")?.FieldType))
            _ = harmony.Patch(offThread, new HarmonyMethod(AccessTools.Method(typeof(MapPieceBudget), nameof(Saving))));
    }

    // The save clock the engine declares and never advances; only where a database is open (the server's layer has none:
    // a save there threw, the world crashed on loading) and pieces wait
    internal static void Saving(float dt, ref float ___diskSaveAccum, object? ___mapdb, System.Collections.ICollection? ___toSaveList)
    {
        if (!Enabled || ___mapdb is null || ___toSaveList is not { Count: > 0 } || !Finite(dt) || dt < 0) return;
        ___diskSaveAccum += dt;
    }

    // The one `ldc.i4 200` (the engine's pieces per tick) becomes Pieces(); any other shape is left as it is
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var pieces = AccessTools.Method(typeof(MapPieceBudget), nameof(Pieces));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(pieces)) return code;
        Rewritten = Il.SubstituteSingle(code, static c => c.opcode == OpCodes.Ldc_I4 && c.operand is Engine, pieces);
        return code;
    }

    internal static int Pieces() => Enabled ? PerTick : Engine; // called from the rewritten tick alone
}
