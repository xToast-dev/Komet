using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Common;

namespace Komet.World;

// Tick slots: the first-empty search starts at the mark, below which no slot is empty
internal static partial class ListenerSlots
{
    // Per block tick list: no slot below Lowest is empty; Count is the length when Lowest was last raised
    private static readonly ConditionalWeakTable<List<GameTickListenerBlock?>, Free> Slots = [];

    private static AccessTools.FieldRef<List<GameTickListenerBlock?>, int>? _tickVersion;

    internal static MethodBase?[] TickSeams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(EventManager), "AddGameTickListenerBlockInternal",
                [typeof(GameTickListenerBlock), typeof(long)]),
            AccessTools.DeclaredMethod(typeof(EventManager), nameof(EventManager.RemoveGameTickListener),
                [typeof(long)])
        ];
        return seams;
    }

    private static bool PatchTicks(Harmony harmony)
    {
        _tickVersion = Versioned<GameTickListenerBlock?>();
        if (Seams[Ticks] is not [{ } add, { } remove] || !NotNull(_tickVersion) || !Binds(add, nameof(AddTick)) ||
            !Binds(remove, nameof(RemovingTick), nameof(RemovedTick)))
        {
            return false;
        }

        _ = harmony.Patch(add, Own(nameof(AddTick)));
        _ = harmony.Patch(remove, Own(nameof(RemovingTick)), Own(nameof(RemovedTick)));
        return true;
    }

    // Prefix on AddGameTickListenerBlockInternal: the first-empty search, starting at the mark
    private static bool AddTick(List<GameTickListenerBlock?>? ___GameTickListenersBlock,
        ConcurrentDictionary<long, int>? ___GameTickListenersBlockIndices, GameTickListenerBlock listener,
        long newListenerId,
        ref long __result)
    {
        var (list, indices) = (___GameTickListenersBlock, ___GameTickListenersBlockIndices);
        if (!On(Ticks) || list is null || indices is null || list.Count >= MaxListeners) return true;

        var free = Slots.GetValue(list, static _ => new Free());
        var read = Volatile.Read(ref free.Lowest);
        var from = list.Count < free.Count || read > list.Count ? 0 : read; // shrunk by another hand: from the start
        var slot = list.Count;
        for (var i = from; i < Math.Min(list.Count, MaxListeners); i++)
        {
            if (list[i] is not null) continue;

            slot = i;
            break;
        }

        if (slot < list.Count)
        {
            list[slot] = listener;
        }
        else
        {
            list.Add(listener);
        }

        indices[newListenerId] = slot;
        _ = Interlocked.CompareExchange(ref free.Lowest, slot + 1,
            read); // a removal lowered it meanwhile: that one stands
        free.Count = list.Count;
        __result = newListenerId;
        return false;
    }

    // Prefix on RemoveGameTickListener: the block slot it empties (0 when scanning, -1 for an entity slot), version
    private static void RemovingTick(List<GameTickListener?>? ___GameTickListenersEntity,
        ConcurrentDictionary<long, int>? ___GameTickListenersEntityIndices,
        List<GameTickListenerBlock?>? ___GameTickListenersBlock,
        ConcurrentDictionary<long, int>? ___GameTickListenersBlockIndices, long listenerId,
        out (int Slot, int Version) __state)
    {
        __state = (-1, 0);
        var (entities, blocks) = (___GameTickListenersEntity, ___GameTickListenersBlock);
        if (listenerId == 0 || _tickVersion is not { } version || entities is null || blocks is null ||
            ___GameTickListenersEntityIndices is not { } byEntity ||
            ___GameTickListenersBlockIndices is not { } byBlock)
        {
            return;
        }

        var inEntities = byEntity.TryGetValue(listenerId, out var e);
        var entityHeld = inEntities && e >= 0 && e < entities.Count && entities[e] is { } entity &&
                         entity.ListenerId == listenerId;
        var blockAt = !inEntities && byBlock.TryGetValue(listenerId, out var b) && b >= 0 && b < blocks.Count &&
                      blocks[b] is { } block && block.ListenerId == listenerId
            ? b
            : 0;
        var slot = entityHeld ? -1 : blockAt;
        __state = (slot, version(blocks));
    }

    // Postfix: when the list changed, the slot emptied lowers the mark
    private static void RemovedTick(List<GameTickListenerBlock?>? ___GameTickListenersBlock,
        (int Slot, int Version) __state)
    {
        var list = ___GameTickListenersBlock;
        if (__state.Slot < 0 || _tickVersion is not { } version || list is null || version(list) == __state.Version ||
            !Slots.TryGetValue(list, out var free))
        {
            return;
        }

        for (var i = 0; i < MaxSteps; i++)
        {
            var lowest = Volatile.Read(ref free.Lowest);
            if (lowest <= __state.Slot ||
                Interlocked.CompareExchange(ref free.Lowest, __state.Slot, lowest) == lowest)
            {
                return;
            }
        }

        Volatile.Write(ref free.Lowest, 0); // contended past MaxSteps: the start is never wrong
    }

    private sealed class Free
    {
        public int Count;
        public int Lowest;
    }
}
