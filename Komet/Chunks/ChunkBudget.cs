using System.Diagnostics;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// ChunkTesselatorManager.OnBeforeFrame uploads at most num * (3 + queued >> limiter) vertices per frame, a budget that grows with the
// backlog: after a teleport or a burst of retessellations it costs several milliseconds a frame. So the normal queue also stops once
// the frame has spent CapMillis in OnBeforeFrame. The first chunk always goes, the rest stays sorted in the queue for the next frame,
// and the priority queue (chunks next to the player, block edits) is never cut. 3 ms is about twice the worst steady frame of this
// stage (rend3D-ret-lide 1.35 ms), so the cap binds only on a backlog. A backlog of finished meshes past BoostAbove (a world join or a
// teleport: 2000 in the bench, flight peaks at 160-260) raises the cap to BoostMillis until it falls under BoostBelow, so the world
// fills in seconds instead of dribbling out 3 ms a frame.
internal static class ChunkBudget
{
    public const int Uncapped = 0, MaxCapMillis = 20, DefaultCapMillis = 3, BoostMillis = 8;
    private const int BoostAbove = 400, BoostBelow = 100;
    private const int MaxLookahead = 16;
    private static long _deadline;
    private static bool _admitted, _fired;

    public static int CapMillis { get; set; } = DefaultCapMillis;
    public static bool Capped { get; private set; } // the cap sits in the normal queue's loop condition
    public static bool Boosted { get; private set; } // the upload backlog raised the cap to BoostMillis

    // Frames in which the cap ended the normal queue early, while Counting.Hud
    public static long CapHits { get; private set; }

    public static void Install(Harmony harmony)
    {
        (_deadline, _admitted, _fired, Boosted) = (0, false, false, false);
        var frame = AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager),
            nameof(ChunkTesselatorManager.OnBeforeFrame));
        if (!NotNull(harmony) || !NotNull(frame)) return;
        _ = NotNull(harmony.Patch(frame, new HarmonyMethod(typeof(ChunkBudget), nameof(Begin)),
            transpiler: new HarmonyMethod(typeof(ChunkBudget), nameof(Rewrite))));
    }

    // Prefix: the frame's deadline. Whatever the priority queue takes counts against it, but that queue is never cut.
    internal static void Begin()
    {
        var waiting = RuntimeStats.chunksAwaitingPooling;
        Boosted = Boosted ? waiting >= BoostBelow : waiting > BoostAbove;
        var millis = CapMillis == Uncapped || !Boosted ? CapMillis : Math.Max(CapMillis, BoostMillis);
        _deadline = millis != Uncapped && Assert(millis is > Uncapped and <= MaxCapMillis)
            ? Stopwatch.GetTimestamp() + millis * Stopwatch.Frequency / 1000
            : 0;
        (_admitted, _fired) = (false, false);
    }

    // The normal queue loops while uploaded < Cap(budget). The first test comes before the first chunk and always passes, so every
    // frame takes at least one; after that int.MinValue ends the loop once the deadline is gone. Asked again after that, the loop did
    // not end where the transpiler meant it to, and the engine's budget stands.
    internal static int Cap(int budget)
    {
        if (!Assert(!_fired) || _deadline == 0 || !_admitted)
        {
            _admitted = true;
            return budget;
        }

        if (Stopwatch.GetTimestamp() < _deadline) return budget;
        (_deadline, _fired) = (0, true);
        if (Counting.Hud) CapHits++;
        return int.MinValue;
    }

    // The normal queue loops while uploaded (num2) < budget (num3), a test at the bottom of the loop that ends in ldloc num3, blt back
    // into the body; the cap goes between the two. num3 is the local stored right after the only read of the limiter setting, and the
    // site the only backward blt on it after tessChunksQueue.Sort() (the early return before the lock reads num3 too, but branches
    // forward). Any other shape keeps the engine's IL.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Capped = false;
        var limiter = AccessTools.PropertyGetter(typeof(ClientSettings),
            nameof(ClientSettings.ChunkVerticesUploadRateLimiter));
        var sort = AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<>.Sort));
        var cap = AccessTools.Method(typeof(ChunkBudget), nameof(Cap));
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code)) || !NotNull(limiter) || !NotNull(sort) ||
            !NotNull(cap))
            return code;
        int read = Il.Single(code, c => c.Calls(limiter)), sorted = Il.Single(code, c => c.Calls(sort));
        if (!Assert(read >= 0) || !Assert(sorted > read)) return code;
        var site = Site(code, sorted, Budget(code, read));
        if (!Assert(site > sorted)) return code;
        code.Insert(site + 1, new CodeInstruction(OpCodes.Call, cap)); // consumes num3, leaves the cap; no label moves
        Capped = true;
        return code;
    }

    // The local the limiter expression is stored into: the first stloc after the read
    private static int Budget(List<CodeInstruction> code, int read)
    {
        if (!Index(read, code.Count)) return -1;
        for (var step = 1; step < Math.Min(code.Count - read, MaxLookahead); step++)
            if (Il.Local(code[read + step], Il.Uses.Store) is var local and >= 0)
                return local;
        return -1;
    }

    // The only ldloc budget, blt pair after Sort() that branches back to a label between Sort() and itself; -1 for none or several
    private static int Site(List<CodeInstruction> code, int sorted, int budget)
    {
        if (budget < 0 || !Index(sorted, code.Count)) return -1;
        var site = -1;
        for (var i = sorted + 1; i < Math.Min(code.Count - 1, Il.MaxInstructions); i++)
        {
            var branch = code[i + 1];
            if (Il.Local(code[i], Il.Uses.Load) != budget ||
                (branch.opcode != OpCodes.Blt && branch.opcode != OpCodes.Blt_S) ||
                branch.operand is not Label target ||
                code.FindIndex(sorted, i - sorted, c => c.labels.Contains(target)) < 0) continue;
            if (site >= 0) return -1;
            site = i;
        }

        return site;
    }
}
