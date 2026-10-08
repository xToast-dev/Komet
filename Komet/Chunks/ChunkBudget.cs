using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// ChunkTesselatorManager.OnBeforeFrame uploads at most num * (3 + queued >> limiter) vertices per frame, a budget that grows with the
// backlog: after a teleport or a burst of retessellations it costs several milliseconds a frame. So the normal queue also stops once
// the frame has spent CapMillis in OnBeforeFrame. The first chunk always goes, the rest stays sorted in the queue for the next frame.
// 3 ms is about twice the worst steady frame of this stage (rend3D-ret-lide 1.35 ms), so the cap binds only on a backlog. A backlog of
// finished meshes past BoostAbove (a world join or a teleport: 2000 in the bench, flight peaks at 160-260) raises the cap to
// BoostMillis until it falls under BoostBelow, so the world fills in seconds instead of dribbling out 3 ms a frame.
//
// The priority queue (chunks next to the player, block edits) goes first and whole, as in the engine, up to CeilingMillis (never
// below the cap in force): past it the rest stays queued in order, the engine's own flag asks for it again, and the next frame takes
// it first, the normal queue waiting until it is through. A block edit's few chunks never reach it; a burst (an explosion, a
// teleport) is spread over frames instead of one frame of tens of milliseconds. Its first chunk always goes.
//
// A frame already slow when its uploads come (a collection, a burst of block entities before them) takes only the first chunk of the
// normal queue (Yield): the rest waits a frame for one with room. Slow is YieldMillis past the usual time into the frame, which follows
// the frames (Typical); never two frames in a row, so a machine slow throughout still uploads at least every other frame in full.
//
// Spent adds the pool work the uploads cause later in the stage (Vulkan adopting a pool) to the frame's time, so Over counts the
// frames whose chunk work in the Before stage went past the cap, whoever did it. The profiler marks split the Before stage's tail
// (rendOpaque-12before without them): what ran between the entities and the uploads, then the uploads.
internal static class ChunkBudget
{
    // EngineShape of OnBeforeFrame in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xC843DFD7A971F8EBUL;
    public const int Uncapped = 0, MaxCapMillis = 20, DefaultCapMillis = 3, DefaultCeilingMillis = 6;
    private const int BoostMillis = 8, BoostAbove = 400, BoostBelow = 100, MaxLookahead = 16, YieldMillis = 6, TypicalShift = 4;
    private const string PreMark = "komet-upload-pre", UploadMark = "komet-upload";
    private static long _deadline, _ceiling, _began, _spent, _allowed, _typical;
    private static bool _admitted, _fired, _asked, _cut, _yielded;

    public static int CapMillis { get; set; } = DefaultCapMillis;
    public static int CeilingMillis { get; set; } = DefaultCeilingMillis;
    public static bool Capped { get; private set; }
    public static bool Ceiled { get; private set; } // the priority loop's gate is in
    public static bool Boosted { get; private set; }
    public static bool Yield { get; set; } = true;

    // Totals while Counting.Hud: frames the cap ended the normal queue, frames the ceiling ended the priority queue, frames whose
    // chunk work went past the cap and by how much (Stopwatch ticks)
    public static long CapHits { get; private set; }
    public static long PriorityCuts { get; private set; }
    public static long Over { get; private set; }
    public static long OverTicks { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "processPrioQueue")]
    private static extern ref bool PriorityAsked(ChunkTesselatorManager manager);

    internal static MethodBase?[] Seams() =>
        [AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.OnBeforeFrame))];

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_deadline, _ceiling, _began, _spent, _allowed, _typical) = (0, 0, 0, 0, 0, 0);
        (_admitted, _fired, _asked, _cut, Boosted, Capped, Ceiled, _yielded) =
            (false, false, false, false, false, false, false, false);
        var frame = Seams()[0];
        if (!NotNull(harmony) || !NotNull(frame) || !EngineShape.Matches([frame], fingerprint, nameof(ChunkBudget), logger))
            return;
        _ = NotNull(harmony.Patch(frame, new HarmonyMethod(Begin), new HarmonyMethod(End), new HarmonyMethod(Rewrite)));
    }

    private static long Ticks(int millis) =>
        Assert(millis is >= Uncapped and <= MaxCapMillis) ? millis * Stopwatch.Frequency / 1000 : 0;

    // Whatever the priority queue takes counts against the deadline. The frame before is booked first: its pool work came after it.
    internal static void Begin()
    {
        ScreenManager.FrameProfiler?.Mark(PreMark);
        Book();
        var waiting = RuntimeStats.chunksAwaitingPooling;
        _ = Assert(waiting >= 0);
        Boosted = Boosted ? waiting >= BoostBelow : waiting > BoostAbove;
        var millis = CapMillis == Uncapped || !Boosted ? CapMillis : Math.Max(CapMillis, BoostMillis);
        var now = Stopwatch.GetTimestamp();
        _deadline = millis != Uncapped && Assert(millis is > Uncapped and <= MaxCapMillis) ? now + Ticks(millis) : 0;
        var ceiling = CeilingMillis == Uncapped ? Uncapped : Math.Max(CeilingMillis, millis);
        _ceiling = ceiling != Uncapped && Assert(ceiling <= MaxCapMillis) ? now + Ticks(ceiling) : 0;
        (_admitted, _fired, _asked, _cut, _began, _spent, _allowed) = (false, false, false, false, now, 0, Ticks(millis));
        if (Yielding(now, FrameClock.FrameStart) && _deadline != 0) _deadline = now + 1; // the first chunk, then the cap
        _ = Assert(_deadline == 0 || _deadline > now) && Assert(_ceiling == 0 || _deadline == 0 || _ceiling >= _deadline);
    }

    // Whether this frame is YieldMillis past the usual time into the frame at its uploads, the frame before not having yielded
    internal static bool Yielding(long now, long start)
    {
        if (!Yield || start <= 0 || now < start)
        {
            _yielded = false;
            return false;
        }

        var (into, slack) = (now - start, Ticks(YieldMillis));
        if (_typical == 0) _typical = Math.Min(into, slack);
        var slow = !_yielded && _typical > 0 && into > _typical + slack;
        _typical += (Math.Min(into, _typical + slack) - _typical) >> TypicalShift; // a slow frame moves it by the slack at most
        _yielded = slow;
        return Assert(_typical >= 0) && Assert(!slow || into > slack) && slow;
    }

    // Harmony injects __instance by name
    internal static void End(ChunkTesselatorManager __instance)
    {
        if (_cut && NotNull(__instance)) PriorityAsked(__instance) = true; // what the engine cleared: the rest goes next frame
        if (_began > 0) Spent(Stopwatch.GetTimestamp() - _began);
        _began = 0;
        _ = Assert(!_cut || _asked);
        ScreenManager.FrameProfiler?.Mark(UploadMark);
    }

    // Chunk work of this frame's Before stage outside OnBeforeFrame, on the main thread
    public static void Spent(long ticks)
    {
        if (Assert(ticks >= 0) && Assert(_spent >= 0)) _spent += ticks;
    }

    private static void Book()
    {
        _ = Assert(_spent >= 0) && Assert(_allowed >= 0);
        if (!Counting.Hud || _allowed == 0 || _spent <= _allowed) return;
        (Over, OverTicks) = (Over + 1, OverTicks + _spent - _allowed);
    }

    // Stands in for tessChunksQueuePriority.Count where the priority loop asks whether to go on: asked first in front of the first
    // chunk, which always goes; 0 ends the loop once the ceiling is gone
    internal static int Priority(int count)
    {
        if (count <= 0 || _ceiling == 0 || !_asked)
        {
            _asked = true;
            return count;
        }

        if (Stopwatch.GetTimestamp() < _ceiling) return count;
        _cut = true;
        if (Counting.Hud) PriorityCuts++;
        return Assert(_asked) && Assert(count > 0) ? 0 : count;
    }

    // The normal queue loops while uploaded < Cap(budget). The first test comes before the first chunk and always passes, so every
    // frame takes at least one; after that int.MinValue ends the loop once the deadline is gone. Asked again after that, the loop did
    // not end where the transpiler meant it to, and the engine's budget stands. A priority queue cut short has the frame: none.
    internal static int Cap(int budget)
    {
        if (!Assert(!_fired))
        {
            _admitted = true;
            return budget;
        }

        if (_cut)
        {
            _fired = true;
            return int.MinValue;
        }

        if (_deadline == 0 || !_admitted)
        {
            _admitted = true;
            return budget;
        }

        if (Stopwatch.GetTimestamp() < _deadline) return budget;
        (_deadline, _fired) = (0, true);
        _ = Assert(_admitted);
        if (Counting.Hud) CapHits++;
        return int.MinValue;
    }

    // The cap goes into the normal queue's loop condition, the gate into the priority queue's; either is placed only where the body
    // has the shape it was written for, and the other stays the engine's
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        (Capped, Ceiled) = (false, false);
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code))) return code;
        var (cap, gate) = (CapSite(code), GateSite(code));
        var (capping, gating) = (AccessTools.Method(typeof(ChunkBudget), nameof(Cap)),
            AccessTools.Method(typeof(ChunkBudget), nameof(Priority)));
        if (!NotNull(capping) || !NotNull(gating)) return code;
        if (cap >= 0) code.Insert(cap + 1, new CodeInstruction(OpCodes.Call, capping)); // consumes num3, leaves the cap
        var gated = gate >= 0 && (cap < 0 || gate < cap); // before the cap: inserting it moved nothing earlier
        if (gated) code.Insert(gate + 1, new CodeInstruction(OpCodes.Call, gating)); // takes the count, leaves it or 0
        (Capped, Ceiled) = (cap >= 0, gated);
        return code;
    }

    // The normal queue loops while uploaded (num2) < budget (num3), a test at the bottom of the loop that ends in ldloc num3, blt back
    // into the body; the cap goes between the two. num3 is the local stored right after the only read of the limiter setting, and the
    // site the only backward blt on it after tessChunksQueue.Sort() (the early return before the lock reads num3 too, but branches
    // forward). -1 for any other shape.
    private static int CapSite(List<CodeInstruction> code)
    {
        var limiter = AccessTools.PropertyGetter(typeof(ClientSettings),
            nameof(ClientSettings.ChunkVerticesUploadRateLimiter));
        var sort = AccessTools.Method(typeof(SortableQueue<TesselatedChunk>), nameof(SortableQueue<>.Sort));
        if (!NotNull(limiter) || !NotNull(sort)) return -1;
        int read = Il.Single(code, c => c.Calls(limiter)), sorted = Il.Single(code, c => c.Calls(sort));
        if (!Assert(read >= 0) || !Assert(sorted > read)) return -1;
        var site = Site(code, sorted, Budget(code, read));
        return Assert(site > sorted) ? site : -1;
    }

    private static int Budget(List<CodeInstruction> code, int read)
    {
        if (!Index(read, code.Count)) return -1;
        for (var step = 1; step < Math.Min(code.Count - read, MaxLookahead); step++)
            if (Il.Local(code[read + step], Il.Uses.Store) is var local and >= 0) return local;
        return -1;
    }

    // The only ldloc budget, blt pair after Sort() that branches back to a label between Sort() and itself; -1 for none or several
    private static int Site(List<CodeInstruction> code, int sorted, int budget)
    {
        if (budget < 0 || !Index(sorted, code.Count) || !Assert(code.Count <= Il.MaxInstructions)) return -1;
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

    // The priority loop goes on while tessChunksQueuePriority.Count > 0: ldfld tessChunksQueuePriority, Count, ldc.i4.0, bgt back
    // into the body. The gate takes the count. The only such test, and the flag the end sets again must be the engine's bool.
    private static int GateSite(List<CodeInstruction> code)
    {
        var queue = AccessTools.DeclaredField(typeof(ChunkTesselatorManager), "tessChunksQueuePriority");
        var count = AccessTools.PropertyGetter(typeof(Queue<TesselatedChunk>), nameof(Queue<>.Count));
        var asked = AccessTools.DeclaredField(typeof(ChunkTesselatorManager), "processPrioQueue");
        if (queue is null || !NotNull(count) || asked?.FieldType != typeof(bool) || asked.IsStatic ||
            !Assert(code.Count <= Il.MaxInstructions)) return -1;
        var site = Il.CountLoop(code, queue, count);
        return Assert(site < code.Count) ? site : -1;
    }
}
