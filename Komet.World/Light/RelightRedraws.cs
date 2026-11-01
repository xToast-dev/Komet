using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.World;

// Every block change on the client queues a lighting task, and the relight thread (ClientSystemRelight, every 10 ms)
// ends each task by marking the block's chunk dirty again (SetChunkDirty), though the change itself already marked it
// (MarkChunkDirty in SetBlock, ExchangeBlock and the bulk commits) and most changes alter no light at all: snow layers,
// grass, leaves, crops. By then the tesselator has often taken the chunk, so it is built and uploaded a second time
// (user world 2026-10-31: 95-99 % of the lighting tasks changed no light and re-queued 30 chunks a second standing
// still, 300 right after the join, that were not queued any more). A task that removes no light, where neither block
// emits light and both absorb alike, is dropped here: all the engine's task would have done is re-mark that chunk.
internal static class RelightRedraws
{
    private const int Value = 2; // a light's brightness in its HSV bytes

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static long Dropped => Interlocked.Read(ref _dropped);

    private static long _dropped;

    internal static MethodBase? Target() =>
        AccessTools.DeclaredMethod(typeof(ClientSystemRelight), "ProcessLightingTask", [typeof(EntityPos), typeof(UpdateLightingTask)]);

    public static void Install(Harmony harmony)
    {
        Installed = false;
        if (!NotNull(harmony) || Target() is not { } target ||
            AccessTools.DeclaredField(typeof(ClientSystem), "game")?.FieldType != typeof(ClientMain)) return;
        _ = harmony.Patch(target, new HarmonyMethod(AccessTools.Method(typeof(RelightRedraws), nameof(Prefix))));
        Installed = true;
    }

    // ProcessLightingTask prefix (relight thread): false drops a task that changes no light
    private static bool Prefix(ClientSystemRelight __instance, UpdateLightingTask? task)
    {
        if (!Enabled || task is not { absorbUpdate: false, removeLightHsv: null, pos: { } pos } || Game(__instance) is not { } game ||
            game.Blocks is not { } blocks || (uint)task.oldBlockId >= (uint)blocks.Count || (uint)task.newBlockId >= (uint)blocks.Count ||
            blocks[task.oldBlockId] is not { } old || blocks[task.newBlockId] is not { } now ||
            !Unlit(old, now, game.BlockAccessor, pos)) return true;
        _ = Interlocked.Increment(ref _dropped);
        return false;
    }

    // Neither block emits light and both absorb alike: the engine's task would relight nothing
    internal static bool Unlit(Block old, Block now, IBlockAccessor accessor, BlockPos pos) =>
        Dark(old.GetLightHsv(accessor, pos)) && Dark(now.GetLightHsv(accessor, pos)) &&
        old.GetLightAbsorption(accessor, pos) == now.GetLightAbsorption(accessor, pos);

    // An unreadable HSV counts as light: the engine's own task meets it
    private static bool Dark(byte[]? hsv) => hsv is { Length: > Value } && hsv[Value] == 0;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    private static extern ref ClientMain? Game(ClientSystem system);
}
