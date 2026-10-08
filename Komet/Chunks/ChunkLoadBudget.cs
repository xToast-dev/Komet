using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Common.Entities;
using Vintagestory.Client.NoObf;

namespace Komet.Chunks;

// Every packet the client receives becomes a main-thread task, queued by the network thread in arrival order: a chunk as a
// "loadchunk" task that inserts it and initialises its block entities, everything else as a ProcessPacketTask. ExecuteMainThreadTasks
// runs the whole queue every frame, so a burst of chunks (a server tick's worth, or a backlog after a stall) initialises every one of
// their block entities in one frame, with whatever garbage collection that allocation sets off landing in the same frame.
//
// Once the tasks of a frame have taken Millis, the queue stops in front of the next task that may wait: the load of a chunk more than
// NearChunks columns from the player, or a chunk unload. What is left stays in the engine's own queue, in order, ahead of whatever
// arrives meanwhile, and runs first next frame; so no packet ever overtakes another, and a later block update or unload of the chunk
// still finds it loaded. The first task of a frame always runs. A near chunk load further back is never held: the queue runs through
// it. A backlog past BoostAbove tasks (a world join, a teleport) raises the budget to BoostMillis. Before the player has spawned the
// engine's loop is left alone.
//
// A far chunk whose block entities did not all fit into the budget (BlockEntityBudget) goes on first at the frame's first look at the
// queue, on the same clock, and while it is still not done nothing behind it runs.
internal static class ChunkLoadBudget
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x222221D9946996AEUL;
    public const int Engine = 0, MaxMillis = 20, DefaultMillis = 3;
    private const int BoostMillis = 8, BoostAbove = 256, NearChunks = 3, MaxScan = 1024, ChunkShift = 5;
    private const int DimensionHeight = 1024, UnloadPacket = 11; // chunk Y of dimension 1; Packet_ServerIdEnum.UnloadServerChunk

    private static MethodBase?[] _seams = [];
    private static Type? _closure; // LoadChunkFromPacket's closure, the target of every "loadchunk" task
    private static AccessTools.FieldRef<object, Packet_ServerChunk>? _packet;
    private static long _start;
    private static int _forced; // tasks to run whatever the clock says: up to a near chunk load
    private static bool _first; // the frame's first look at the queue is still to come

    public static int Millis { get; set; } = DefaultMillis;
    public static bool Rewritten { get; private set; }

    // The loop is gated and has a budget: a far chunk's block entities may then spread over frames (BlockEntityBudget)
    internal static bool Budgeting => Rewritten && Millis != Engine;

    // Totals while Counting.Hud: frames the budget ended the queue, and the tasks it left for later
    public static long Stops { get; private set; }
    public static long Held { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "reversedQueue")]
    private static extern ref Queue<ClientTask> Pending(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "packet")]
    private static extern ref Packet_Server Packet(ProcessPacketTask task);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (Rewritten, _seams, _closure, _packet, _start, _forced, _first, Stops, Held) =
            (false, Seams(), null, null, 0, 0, false, 0, 0);
        if (!NotNull(harmony) || !Assert(_seams.Length == 3) || !Assert(!Array.Exists(_seams, s => s is null))) return;
        if (!EngineShape.Matches(_seams, fingerprint, nameof(ChunkLoadBudget), logger)) return;
        _closure = _seams[2]!.DeclaringType;
        if (_closure is null || AccessTools.DeclaredField(_closure, "p")?.FieldType != typeof(Packet_ServerChunk) ||
            !Assert(AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue")?.FieldType == typeof(Queue<ClientTask>)) ||
            !Assert(AccessTools.DeclaredField(typeof(ProcessPacketTask), "packet")?.FieldType == typeof(Packet_Server)))
            return;
        _packet = AccessTools.FieldRefAccess<Packet_ServerChunk>(_closure, "p");
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Begin), transpiler: new HarmonyMethod(Rewrite)));
        if (!Rewritten)
            logger?.Warning("Komet ChunkLoadBudget: ClientMain.ExecuteMainThreadTasks is not the loop it was written for; " +
                            "every queued chunk loads at once, as in the engine");
    }

    // The task loop, LoadChunkFromPacket, which queues a chunk as "loadchunk", and that task's body: the one that never runs a
    // chunk it reads from the closure's p before its turn
    internal static MethodBase?[] Seams()
    {
        var load = AccessTools.DeclaredMethod(typeof(ClientWorldMap), "LoadChunkFromPacket");
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.ExecuteMainThreadTasks)), load,
            load is null ? null : Task(load)
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // The one method LoadChunkFromPacket turns into the task's delegate
    private static MethodInfo? Task(MethodBase load)
    {
        if (!Il.Take(PatchProcessor.GetOriginalInstructions(load), Il.MaxInstructions, out var code)) return null;
        var at = Il.Single(code, c => c.opcode == OpCodes.Ldftn);
        return at >= 0 && Assert(code[at].operand is MethodInfo) ? code[at].operand as MethodInfo : null;
    }

    internal static void Begin()
    {
        (_start, _forced, _first) = (Stopwatch.GetTimestamp(), 0, true);
        _ = Assert(_start > 0);
    }

    // Stands in for reversedQueue.Count where the loop asks whether to go on: the count, or 0 to end the frame's tasks here. The loop
    // only asks once no game launch task is due and tasks are not suspended, so its first look is where a part-initialised far chunk
    // goes on; while one still is, the queue behind it waits.
    internal static int Gate(int count, ClientMain game)
    {
        _ = Assert(count >= 0);
        if (_first)
        {
            _first = false;
            if (BlockEntityBudget.Pending) BlockEntityBudget.Resume(game);
        }

        if (BlockEntityBudget.Pending)
        {
            if (Counting.Hud && count > 0) (Stops, Held) = (Stops + 1, Held + count);
            return 0;
        }

        return count <= 0 || Millis == Engine || game is not { Spawned: true }
            ? count
            : Decide(Pending(game), count, game.EntityPlayer?.Pos);
    }

    // Whether the frame's tasks have taken their budget by Begin's clock, boosted as Decide boosts it: where a far chunk's block
    // entities stop (BlockEntityBudget)
    internal static bool Spent(ClientMain game)
    {
        var queue = NotNull(game) ? Pending(game) : null;
        var millis = Budget(queue?.Count ?? 0);
        return Assert(millis is >= Engine and <= MaxMillis) && millis > Engine && Elapsed(millis);
    }

    // The frame's budget for this many queued tasks: BoostMillis past BoostAbove
    private static int Budget(int queued) => Assert(queued >= 0) && Assert(Millis >= Engine) && queued > BoostAbove ? Math.Max(Millis, BoostMillis) : Millis;

    // Begin's clock has run this many milliseconds
    private static bool Elapsed(int millis) =>
        Assert(millis >= 0) && Assert(_start >= 0) && Stopwatch.GetTimestamp() - _start >= millis * Stopwatch.Frequency / 1000;

    // Whether a chunk's load may stop half way and hold every task behind it: the budget is on, the queue is not being run through
    // to a near chunk load, the chunk is still far, and no near chunk load waits further back
    internal static bool MayHold(ClientMain game, Packet_ServerChunk? chunk)
    {
        if (!Budgeting || _forced > 0 || game is not { Spawned: true } ||
            game.EntityPlayer?.Pos is not { } pos || !Far(chunk, pos)) return false;
        var queue = Pending(game);
        return NotNull(queue) && Assert(_start > 0) && LastNear(queue, pos) < 0; // Begin started this frame's clock
    }

    internal static int Decide(Queue<ClientTask>? queue, int count, EntityPos? pos)
    {
        if (_forced > 0)
        {
            _ = Assert(_forced <= MaxScan);
            _forced--;
            return count;
        }

        var millis = Budget(count);
        if (!Assert(millis is > Engine and <= MaxMillis) || !Elapsed(millis)) return count;
        if (!NotNull(queue) || pos is null || !queue.TryPeek(out var next) || !Waits(next, pos)) return count;
        _forced = LastNear(queue, pos);
        if (_forced >= 0) return count;
        _forced = 0;
        if (Counting.Hud) (Stops, Held) = (Stops + 1, Held + count);
        return 0;
    }

    // A chunk unload, or the load of a chunk of the world (not a moving dimension's) more than NearChunks columns away
    internal static bool Waits(ClientTask? task, EntityPos pos) => task?.Action?.Target switch
    {
        ProcessPacketTask packet => Packet(packet) is { Id: UnloadPacket },
        { } target when target.GetType() == _closure && _packet is not null => Far(_packet(target), pos),
        _ => false
    };

    internal static bool Far(Packet_ServerChunk? chunk, EntityPos pos) =>
        chunk is { Y: >= 0 and < DimensionHeight } && NotNull(pos) &&
        Math.Max(Math.Abs(chunk.X - ((int)pos.X >> ChunkShift)), Math.Abs(chunk.Z - ((int)pos.Z >> ChunkShift))) > NearChunks;

    // The index of the last chunk load among the first MaxScan tasks that may not wait, -1 for none
    private static int LastNear(Queue<ClientTask> queue, EntityPos pos)
    {
        var (last, tasks) = (-1, queue.GetEnumerator());
        for (var i = 0; i < Math.Min(queue.Count, MaxScan) && tasks.MoveNext(); i++)
            if (tasks.Current?.Action?.Target is { } target && target.GetType() == _closure && _packet is not null &&
                !Far(_packet(target), pos))
                last = i;
        return Assert(last < MaxScan) ? last : -1;
    }

    // The loop's test, reversedQueue.Count > 0 branching back into the body, is the one get_Count on reversedQueue followed by
    // ldc.i4.0 and a bgt to a label before it; Gate goes between the count and the comparison. Any other shape: the engine's IL.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var queue = AccessTools.DeclaredField(typeof(ClientMain), "reversedQueue");
        var count = AccessTools.PropertyGetter(typeof(Queue<ClientTask>), nameof(Queue<>.Count));
        var gate = AccessTools.DeclaredMethod(typeof(ChunkLoadBudget), nameof(Gate));
        if (!Il.Take(instructions, Il.MaxInstructions, out var code) || !NotNull(queue) || !NotNull(count) ||
            !NotNull(gate)) return code;
        var site = Il.CountLoop(code, queue, count);
        if (site < 0) return code;
        code.InsertRange(site + 1, [new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Call, gate)]);
        Rewritten = true;
        return code;
    }
}
