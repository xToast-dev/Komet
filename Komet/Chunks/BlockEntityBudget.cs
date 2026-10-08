using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Chunks;

// A chunk's block entities are initialised in one go when its "loadchunk" task runs (ClientWorldMap.loadChunkMT calls
// ClientChunk.InitBlockEntitiesFromPacket): a chunk of a base, with its firepits, ground storage and racks, can take milliseconds of
// one frame, and ChunkLoadBudget cannot split it, its unit being the task. For a chunk more than three columns from the player the engine's loop runs
// here instead, one block entity at a time on the arrival budget's clock, and stops once that is spent. The chunk then stays in the map
// with loadedFromServer false - the state the network and tessellation threads already see between its insert and its init, and that
// tessellation and the minimap wait out - and loadChunkMT returns before its tail (loadedFromServer, the dirty marks, the chunk
// events). At the next frame's first look at the task queue loadChunkMT runs again for it: its insert is the same one, the loop goes on
// with the block entities not yet initialised, in the engine's order, and the tail runs once they all are. Until then ChunkLoadBudget
// holds the queue, so no packet overtakes the chunk.
//
// Every block entity is initialised once, with the engine's error logging and profiler mark. One read through GetBlockEntity on the
// main thread before its turn (a tick, a dialog, a neighbour across the chunk border) is initialised there, as the safety net; reads by
// the chunk's own block entities while the loop initialises them find the others as the engine's loop leaves them. Near chunks, those of
// a moving dimension, overloads and a chunk with a near chunk load queued behind it stay whole, and a chunk still not done after
// MaxFrames frames finishes at once, so a few slow block entities cannot hold the queue for long.
internal static class BlockEntityBudget
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x5A091E345DCCC308UL;
    private const int MaxEntities = 32 * 32 * 32; // one per block of the chunk
    private const int MaxFrames = 16; // frames a chunk may hold the queue before the rest of it goes at once
    private const string InitMark = "initbe-"; // the engine's profiler mark after each one

    // Main thread: the pending chunk's block entities initialised so far, by the loop or on access
    private static readonly HashSet<BlockEntity> Done = new(ReferenceEqualityComparer.Instance);
    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    // Main thread: the far chunk whose block entities are part initialised, and the packet and game it loads with
    private static ClientChunk? _chunk;
    private static Packet_ServerChunk? _packet;
    private static ClientMain? _game;
    private static bool _inside; // Komet is initialising one of them
    private static int _frames; // frames the pending chunk has gone on in

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    // Body not verified, not rewritten, or another mod patches what the split skips or repeats: every chunk loads whole
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;
    internal static bool Pending => _chunk is not null;

    // Totals while Counting.Hud: chunks spread over frames, block entities initialised on access before their turn

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "InitBlockEntitiesFromPacket")]
    private static extern void InitAll(ClientChunk chunk, ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "loadChunkMT")]
    private static extern void Load(ClientWorldMap map, Packet_ServerChunk p, ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetClientChunk")]
    private static extern ClientChunk Held(ClientWorldMap map, int chunkX, int chunkY, int chunkZ);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (Rewritten, _shaped, Blocked, _seams, _logger, _foreign) = (false, false, true, Seams(), logger, false);
        Clear();
        if (!NotNull(harmony) || !Assert(_seams.Length == 3) || !Assert(Array.TrueForAll(_seams, s => s is not null))) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(BlockEntityBudget), logger);
        var accessed = AccessTools.DeclaredMethod(typeof(BlockEntityBudget), nameof(Accessed));
        if (!_shaped || !NotNull(accessed) || !Assert(Il.Binds(_seams[2]!, accessed))) return;
        _ = NotNull(harmony.Patch(_seams[1], transpiler: new HarmonyMethod(Rewrite)));
        if (!Rewritten)
        {
            logger?.Warning("Komet BlockEntityBudget: ClientWorldMap.loadChunkMT is not the body it was written for; every " +
                            "chunk's block entities initialise at once, as in the engine");
            return;
        }

        _ = NotNull(harmony.Patch(_seams[2], postfix: new HarmonyMethod(accessed)));
        Recheck();
    }

    // A patch on the loop or on loadChunkMT would be skipped for a split chunk, or run once per frame of it. KometModSystem asks
    // again on LevelFinalize, when every mod has patched.
    internal static void Recheck()
    {
        var seamed = _shaped && Rewritten && Assert(_seams.Length == 3);
        _foreign = EngineShape.Report(_logger, nameof(BlockEntityBudget), _foreign,
            seamed && EngineShape.Foreign(_seams.AsSpan(0, 2), EngineShape.Kinds.All, null, typeof(BlockEntityBudget)));
        Blocked = !seamed || _foreign;
    }

    // The loop the split reproduces, the method it rewrites and runs again, and the read it initialises on
    internal static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(ClientChunk), "InitBlockEntitiesFromPacket", [typeof(ClientMain)]),
            AccessTools.DeclaredMethod(typeof(ClientWorldMap), "loadChunkMT", [typeof(Packet_ServerChunk), typeof(ClientChunk)]),
            AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.GetLocalBlockEntityAtBlockPos), [typeof(BlockPos)])
        ];
        return Assert(seams.Length <= EngineShape.MaxMethods) ? seams : [];
    }

    // When the chunk is done, or left the map some other way: what is not initialised goes with it
    public static void Clear()
    {
        (_chunk, _packet, _game, _inside, _frames) = (null, null, null, false, 0);
        Done.Clear();
        _ = Assert(Done.Count == 0) && Assert(Done.Comparer == ReferenceEqualityComparer.Instance);
    }

    // At world leave, from Komet's Dispose, which the engine's dispose runs before SystemUnloadChunks unloads every chunk in the map.
    // The engine never unloads a block entity that did not go through Initialize, and overrides of OnBlockUnloaded do not hold up on
    // one (the windmill rotor's behaviour reads its Api, set in Initialize; the base's catch then reads the block entity's). The pending
    // chunk's not yet initialised ones have registered nothing, so they leave it first, and the unload sees only initialised ones.
    public static void Leave()
    {
        if (_chunk is { } chunk)
        {
            var fresh = new List<BlockPos>();
            foreach (var (pos, entity) in chunk.BlockEntities.Bounded(MaxEntities))
                if (!Done.Contains(entity) && entity?.Api is null) fresh.Add(pos);
            foreach (var pos in fresh.Bounded(MaxEntities)) _ = chunk.RemoveBlockEntity(pos);
            _ = Assert(Done.Count > 0) && Assert(fresh.Count < MaxEntities); // at least one went before the chunk stopped
        }

        Clear();
    }

    // loadChunkMT's one call of chunk.InitBlockEntitiesFromPacket(game), outside its lock, becomes Init(chunk, game, p) with a return
    // when that is false. Any other shape: the engine's IL.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, ILGenerator generator,
        MethodBase original)
    {
        Rewritten = false;
        var (loop, init) = (_seams is [MethodInfo first, ..] ? first : null,
            AccessTools.DeclaredMethod(typeof(BlockEntityBudget), nameof(Init)));
        if (!Il.Take(instructions, Il.MaxInstructions, out var code) || !NotNull(generator) || !NotNull(loop) ||
            !NotNull(init) || original?.GetParameters() is not [{ ParameterType: var packet }, ..] ||
            packet != typeof(Packet_ServerChunk)) return code;
        var at = Il.Single(code, c => c.Calls(loop));
        if (at < 0 || !Assert(at + 1 < code.Count) || !Outside(code, at) || code[at].labels.Count > 0) return code;
        var next = generator.DefineLabel();
        code[at + 1].labels.Add(next);
        (code[at].opcode, code[at].operand) = (OpCodes.Call, init);
        code.InsertRange(at + 1, [new CodeInstruction(OpCodes.Brtrue, next), new CodeInstruction(OpCodes.Ret)]);
        code.Insert(at, new CodeInstruction(OpCodes.Ldarg_1));
        Rewritten = Assert(code[at + 1].Calls(init));
        return code;
    }

    // Whether the instruction at `at` lies outside every exception block, where a return may leave the method
    private static bool Outside(List<CodeInstruction> code, int at)
    {
        if (!NotNull(code) || !Index(at, code.Count)) return false;
        var depth = 0;
        for (var i = 0; i <= Math.Min(at, Il.MaxInstructions); i++)
            foreach (var block in code[i].blocks.Bounded(Il.MaxInstructions))
                depth += block.blockType switch
                {
                    ExceptionBlockType.BeginExceptionBlock => 1,
                    ExceptionBlockType.EndExceptionBlock => -1,
                    _ => 0
                };
        return Assert(depth >= 0) && depth == 0 && code[at].blocks.Count == 0;
    }

    // In place of loadChunkMT's chunk.InitBlockEntitiesFromPacket(game): false while the chunk's block entities are not all
    // initialised, and loadChunkMT returns before its tail
    internal static bool Init(ClientChunk chunk, ClientMain game, Packet_ServerChunk p)
    {
        _ = NotNull(chunk) && NotNull(game); // the engine's call throws on either
        var resuming = _chunk is not null && ReferenceEquals(chunk, _chunk);
        if (!resuming && !Splits(chunk, game, p))
        {
            InitAll(chunk, game);
            return true;
        }

        var complete = Run(chunk, game, p, resuming);
        if (complete) Clear();
        _ = Assert(complete || ReferenceEquals(_chunk, chunk));
        return complete;
    }

    // A chunk that may be split: far, two block entities or more, none null (the engine's loop would throw on it), none pending, and
    // the budget on; whether it is still far and nothing near waits behind it is asked again once the budget runs out
    private static bool Splits(ClientChunk? chunk, ClientMain? game, Packet_ServerChunk? p) =>
        Enabled && !Blocked && _chunk is null && ChunkLoadBudget.Budgeting && game is { Spawned: true } &&
        game.EntityPlayer?.Pos is { } pos && ChunkLoadBudget.Far(p, pos) &&
        chunk?.BlockEntities is { Count: > 1 and <= MaxEntities } entities && !entities.ContainsValue(null!) &&
        Assert(Done.Count == 0);

    // The engine's loop from where the chunk stopped, in its order: true once every block entity is initialised, false when the
    // budget ran out first. At least one block entity goes each time, so a chunk always gets done.
    private static bool Run(ClientChunk chunk, ClientMain game, Packet_ServerChunk p, bool resuming)
    {
        var (entities, ran, mayStop) = (chunk.BlockEntities.Values.GetEnumerator(), 0, true);
        for (var i = 0; i < MaxEntities && entities.MoveNext(); i++)
        {
            var entity = entities.Current;
            if (resuming && (Done.Contains(entity) || entity?.Api is not null)) continue; // initialised already
            if (ran > 0 && mayStop && ChunkLoadBudget.Spent(game))
            {
                if (Enabled && !Blocked && _frames < MaxFrames && ChunkLoadBudget.MayHold(game, p))
                {
                    Stop(chunk, game, p, entity);
                    return false;
                }

                mayStop = false; // near now, something near waits or it held long enough: the rest goes at once
            }

            // Recorded as it goes, not counted out again later: an Initialize that removes an entry the loop already passed (a mod's
            // block entity dropping itself) would shift a count onto one not initialised
            _ = Done.Add(entity!);
            Initialize(entity!, game, true);
            ran++;
        }

        return Assert(chunk.BlockEntities.Count <= MaxEntities) && Assert(ran > 0 || resuming);
    }

    // The chunk stops in front of a block entity not yet initialised; Done holds those that are
    private static void Stop(ClientChunk chunk, ClientMain game, Packet_ServerChunk p, BlockEntity? next)
    {
        (_chunk, _packet, _game) = (chunk, p, game);
        _ = Assert(Done.Count > 0) && Assert(!Done.Contains(next!));
    }

    // The engine's loop body for one block entity: Initialize, logged as the engine logs it when it throws, then the profiler's mark
    private static void Initialize(BlockEntity entity, ClientMain game, bool mark)
    {
        if (!NotNull(entity) || !NotNull(game)) return;
        _inside = true;
        try
        {
            entity.Initialize(game.api);
        }
        catch (Exception e)
        {
            Failed(entity, game, e);
        }
        finally
        {
            _inside = false;
        }

        if (mark && ScreenManager.FrameProfiler.Enabled)
            ScreenManager.FrameProfiler.Mark(InitMark, game.ClassRegistryInt.blockEntityTypeToClassnameMapping[entity.GetType()]);
    }

    private static void Failed(BlockEntity entity, ClientMain game, Exception e)
    {
        if (!NotNull(entity) || !NotNull(game)) return;
        if (game.ClassRegistryInt is { } registry)
            game.Logger.Error("Exception thrown when initializing a block entity with classname {0}:",
                registry.blockEntityTypeToClassnameMapping[entity.GetType()]);
        else game.Logger.Error("Exception thrown when initializing a block entity {0}:", entity.GetType());
        game.Logger.Error(e);
    }

    // From the frame's first look at the task queue (ChunkLoadBudget.Gate): the pending chunk goes on where it stopped, its
    // loadChunkMT run again ahead of every task behind it
    internal static void Resume(ClientMain game)
    {
        var (chunk, packet) = (_chunk, _packet);
        if (chunk is null) return;
        var map = NotNull(game) ? game.WorldMap : null;
        if (packet is null || map is null || !ReferenceEquals(game, _game) ||
            !ReferenceEquals(Held(map, packet.X, packet.Y, packet.Z), chunk))
        {
            Clear();
            return;
        }

        _frames++;
        Load(map, packet, chunk);
        _ = Assert(_chunk is null || ReferenceEquals(_chunk, chunk));
    }

    // Postfix on WorldChunk.GetLocalBlockEntityAtBlockPos, which ClientWorldMap.GetBlockEntity and the block accessors read through: a
    // block entity of the pending chunk read on the main thread before its turn is initialised now, as the loop would have
    internal static void Accessed(WorldChunk __instance, BlockEntity? __result)
    {
        if (_chunk is not { } chunk || !ReferenceEquals(__instance, chunk) || __result is not { Api: null } entity || _inside ||
            Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId || _game is not { } game) return;
        if (!Assert(Done.Count < MaxEntities) || !Done.Add(entity)) return;
        Initialize(entity, game, false);
    }
}
