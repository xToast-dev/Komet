using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Monitor = System.Threading.Monitor;

namespace Komet.Tessellation;

// Makes engine state shared between tessellation passes safe on several threads (TessWorkers; one chunk per thread via
// TessQueue). Audited against 1.22.7 and vanilla mods, smallest fix per hazard so passes act sequential:
// - BlockChunkDataLayer.blocksByPaletteIndex (static table BuildFastBlockAccessArray fills, the getBlock decoders
//   read): one per thread via transpilers. The build writes the engine's table too, so a rewrite another mod's
//   transpiler undoes (Broken) still reads the table of its own chunk.
// - CrossTesselator.startRot/endRot, static Vec3f DrawCross overwrites for rotated crosses: one pair per thread.
// - BlockEntity.OnTesselation: vanilla entities cache in api.ObjectCache, scratch fields, lazy arrays: one lock.
// - Block.OnJsonTesselation on the shared mesh: overrides write per-position flags or fill fields lazily: lock on the
//   block (TessSafety.Blocks); BlockFenceStackAware's family shares one. Block's own and empty overrides (BlockFence)
//   lock nothing unless another mod patches them. FlagWriters (plants, ferns, seaweed, leaves, hanging lichen) only
//   rewrite flags: per-thread mesh copy, flags refilled per call, no lock (locks there serialised half of each
//   tessellation thread's time in a load).
// - BuildDecorPolygons raises a shared block's VertexFlags.ZOffset around a call: one lock.
// - Lazy shapes and meshes (UnloadableShape.Load marks loaded before parsing, OptimizeRamMode 2; default meshes built
//   on first use): TesselateShape, GetCachedShape, GetDefaultBlockMesh while missing share one lock.
// - SunRelightChunk, MeshDataRecycler, texture atlas, BlendedTextureManager, LoadOrCreateLerpedClimateMapOffthread: one
//   lock each; the two hottest are eased: a busy recycler skips DoRecycling, cached maps skip the lock.
// Only guarded passes lock (Guard): workers always, the tessellation thread while Parallel; the main thread keeps its
// races. The recycler is the exception: its lock is taken on every thread. Lock order: decor, block/fence family, block
// entity, shape, then leaves (climate, relight, recycler, textures)
internal static partial class TessSafety
{
    private const int PaletteBuildSites = 4, DecoderSites = 1, CrossSites = 8, EntitySites = 1, JsonSites = 1;
    private const int MaxPatches = 64;

    // Busy-lock counts per block type, logged at powers of four from WaitReport: overrides that still serialise
    private const int WaitReport = 256, MaxWaitTypes = 4096;

    private static readonly Lock EntityGate = new(), TableGate = new();
    private static readonly object DecorGate = new(), ShapeGate = new(), ClimateGate = new();
    private static readonly object RelightGate = new(), TextureGate = new();

    // The recycler's lists are touched only under it
    private static readonly object RecycleGate = new();

    private static readonly FieldInfo? PaletteField =
        AccessTools.DeclaredField(typeof(BlockChunkDataLayer), "blocksByPaletteIndex");

    private static readonly FieldInfo? StartField = AccessTools.DeclaredField(typeof(CrossTesselator), "startRot");
    private static readonly FieldInfo? EndField = AccessTools.DeclaredField(typeof(CrossTesselator), "endRot");

    private static readonly MethodInfo? EntityCall = AccessTools.DeclaredMethod(typeof(BlockEntity),
        nameof(BlockEntity.OnTesselation), [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);

    private static readonly List<(MethodBase Target, MethodInfo Patch)> Patched = [];
    private static readonly Dictionary<string, long> Waits = [];
    private static readonly Lock WaitGate = new();

    [ThreadStatic] private static Block[]? _palette;
    [ThreadStatic] private static Vec3f? _start, _end;
    [ThreadStatic] private static bool _guarded; // this thread's passes take the locks
    private static ILogger? _logger;
    private static int _parallel, _sites;

    // Every patch below is in: the only state in which TessWorkers runs passes
    public static bool Installed { get; private set; }

    // A rewrite found another count when Harmony ran it again: no workers
    public static bool Broken { get; private set; }

    public static bool Parallel => Volatile.Read(ref _parallel) != 0;

    private static int LeavesHeld => (Monitor.IsEntered(ClimateGate) ? 1 : 0) +
                                     (Monitor.IsEntered(RelightGate) ? 1 : 0) +
                                     (Monitor.IsEntered(RecycleGate) ? 1 : 0) +
                                     (Monitor.IsEntered(TextureGate) ? 1 : 0);

    // Switched on by the tessellation thread only (TessWorkers.Steer), with a full fence
    internal static void Open(bool on)
    {
        _ = Assert(!on || Installed);
        _ = Interlocked.Exchange(ref _parallel, on ? 1 : 0);
    }

    internal static void Guard(bool on)
    {
        _ = Assert(!on || Installed);
        _guarded = on;
    }

    // The mod is unpatched: the engine's table is the one written again
    public static void Clear()
    {
        (Installed, Broken, _gates) = (false, false, []);
        Open(false);
        lock (TableGate)
        {
            Patched.Clear();
        }
    }

    // All or nothing: a transpiler missing its exact sites undoes all patches (else a decoder reads an unfilled table)
    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        Clear();
        _logger = logger;
        lock (WaitGate)
        {
            Waits.Clear();
        }

        if (!NotNull(PaletteField) || !NotNull(StartField) || !NotNull(EndField) ||
            !NotNull(EntityCall) || !NotNull(JsonHook))
        {
            return;
        }

        if (Transpilers(harmony) && Locks(harmony))
        {
            Installed = true;
            return;
        }

        lock (TableGate)
        {
            foreach (var (target, patch) in Patched.Bounded(MaxPatches))
            {
                harmony.Unpatch(target, patch);
            }

            Patched.Clear();
        }

        logger?.Warning(
            "Komet TessSafety: an engine method no longer looks as expected, extra tessellation threads stay off");
    }

    private static bool Transpilers(Harmony harmony)
    {
        var (layer, vars) = (typeof(BlockChunkDataLayer), typeof(TCTCache));
        string[] decoders =
            ["getBlockOne", "getBlockTwo", "getBlockThree", "getBlockFour", "getBlockFive", "getBlockGeneralCase"];
        var ok = true;
        foreach (var name in decoders.Bounded(decoders.Length))
        {
            ok &= Transpile(harmony, TessSeams.Method(layer, name, typeof(int)));
        }

        ok &= Transpile(harmony,
            TessSeams.Method(TessSeams.ClientData, "BuildFastBlockAccessArray", typeof(Block[])));
        ok &= Transpile(harmony,
            TessSeams.Method(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross), vars, typeof(float)));
        ok &= Transpile(harmony, TessSeams.Method(typeof(JsonTesselator), nameof(JsonTesselator.Tesselate), vars));
        ok &= Transpile(harmony,
            TessSeams.Method(typeof(BleedingJsonTesselator), nameof(BleedingJsonTesselator.Tesselate), vars));
        ok &= Transpile(harmony,
            TessSeams.Method(typeof(JsonTesselator), nameof(JsonTesselator.doMesh), vars, typeof(MeshData),
                typeof(int)));
        ok &= Transpile(harmony,
            TessSeams.Method(typeof(BleedingJsonTesselator), "DoMeshWithBleeding", vars, typeof(MeshData),
                typeof(int)));
        return ok;
    }

    // The locks around their methods; the unpatch loop takes at most MaxPatches
    private static bool Locks(Harmony harmony)
    {
        var (vars, manager, recycler) = (typeof(TCTCache), typeof(ShapeTesselatorManager), typeof(MeshDataRecycler));
        var (atlas, i, position) = (typeof(TextureAtlasManager), typeof(int), typeof(TextureAtlasPosition));
        Type[] shape =
        [
            typeof(string), typeof(AssetLocation), typeof(CompositeShape), typeof(MeshData).MakeByRefType(),
            typeof(ITexPositionSource), i, typeof(byte), typeof(byte), typeof(int?), typeof(string[])
        ];
        Type[] insert =
        [
            typeof(AssetLocationAndSource), i.MakeByRefType(), position.MakeByRefType(), typeof(CreateTextureDelegate),
            typeof(float)
        ];
        Type[] allocate = [i, i, i.MakeByRefType(), position.MakeByRefType(), typeof(AssetLocationAndSource)];
        Type[] blend = [i, typeof(int[]), typeof(int[]), typeof(int[]), atlas, i];
        return Around(harmony,
                   TessSeams.Method(typeof(JsonTesselator), nameof(JsonTesselator.doMesh), vars, typeof(MeshData), i),
                   nameof(EnterBlock)) &&
               Around(harmony,
                   TessSeams.Method(typeof(BleedingJsonTesselator), "DoMeshWithBleeding", vars, typeof(MeshData), i),
                   nameof(EnterBlock)) &&
               Around(harmony, TessSeams.Method(typeof(ChunkTesselator), "BuildDecorPolygons", i, i, i,
                   typeof(Dictionary<int, Block>), typeof(bool)), nameof(EnterDecor)) &&
               Around(harmony, TessSeams.Method(typeof(ShapeTesselator), nameof(ShapeTesselator.TesselateShape), shape),
                   nameof(EnterShape)) &&
               Around(harmony,
                   TessSeams.Method(manager, nameof(ShapeTesselatorManager.GetCachedShape), typeof(AssetLocation)),
                   nameof(EnterShape)) &&
               Around(harmony,
                   TessSeams.Method(manager, nameof(ShapeTesselatorManager.GetDefaultBlockMesh), typeof(Block)),
                   nameof(EnterMesh)) &&
               Around(harmony, TessSeams.Method(typeof(ClientWorldMap),
                   nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread), i, i), nameof(EnterClimate)) &&
               Around(harmony, TessSeams.Method(typeof(TerrainIlluminator), nameof(TerrainIlluminator.SunRelightChunk),
                   typeof(ClientChunk), typeof(ChunkPos)), nameof(EnterRelight)) &&
               Around(harmony, TessSeams.Method(recycler, nameof(MeshDataRecycler.GetOrCreateMesh), i),
                   nameof(EnterRecycle)) &&
               Around(harmony, TessSeams.Method(recycler, nameof(MeshDataRecycler.DoRecycling)),
                   nameof(TryRecycle)) &&
               Around(harmony, TessSeams.Method(atlas, nameof(TextureAtlasManager.AllocateTextureSpace), allocate),
                   nameof(EnterTexture)) &&
               Around(harmony, TessSeams.Method(atlas, nameof(TextureAtlasManager.GetOrInsertTexture), insert),
                   nameof(EnterTexture)) &&
               Around(harmony,
                   TessSeams.Method(atlas, "runtimeUpdateTexture", typeof(IBitmap), position, typeof(float)),
                   nameof(EnterTexture)) &&
               Around(harmony, TessSeams.Method(typeof(BlendedTextureManager),
                   nameof(BlendedTextureManager.GetOrCreateBlendedTexture), blend), nameof(EnterTexture));
    }

    // Transpiles; records the sites rewritten (0 when another count leaves the IL alone)
    private static bool Transpile(Harmony harmony, MethodInfo? target)
    {
        var patch = AccessTools.Method(typeof(TessSafety), nameof(SitesIl));
        if (!NotNull(target) || !NotNull(patch)) return false;

        _sites = 0;
        _ = harmony.Patch(target, transpiler: new HarmonyMethod(patch));
        lock (TableGate)
        {
            Patched.Add((target, patch));
        }

        return Assert(Volatile.Read(ref _sites) > 0);
    }

    // A lock around a method: the prefix enters, the finalizer leaves whatever happened in between
    private static bool Around(Harmony harmony, MethodInfo? target, string prefix)
    {
        var (enter, leave) = (AccessTools.Method(typeof(TessSafety), prefix),
            AccessTools.Method(typeof(TessSafety), nameof(Leave)));
        if (!NotNull(target) || !NotNull(enter) || !NotNull(leave) || !Assert(Il.Binds(target, enter))) return false;

        _ = harmony.Patch(target, new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave));
        lock (TableGate)
        {
            Patched.AddRange([(target, enter), (target, leave)]);
        }

        return true;
    }

    // Per site kind; rerun on each new patch: a count that no longer fits keeps the IL, parks workers (Broken)
    private static List<CodeInstruction> SitesIl(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code))) return code;

        int sites;
        if (original.DeclaringType == typeof(CrossTesselator))
        {
            sites = Rewrite(code, CrossSites, StartField, nameof(StartRot), EndField, nameof(EndRot), true);
        }
        else if (original.Name == "BuildFastBlockAccessArray")
        {
            sites = Rewrite(code, PaletteBuildSites, PaletteField, nameof(GetPalette), PaletteField, nameof(SetPalette),
                false);
        }
        else if (original.DeclaringType == typeof(BlockChunkDataLayer))
        {
            sites = Rewrite(code, DecoderSites, PaletteField, nameof(GetPalette), null, null, false);
        }
        else if (original.Name is "doMesh" or "DoMeshWithBleeding")
        {
            var own = AccessTools.Method(typeof(TessSafety), nameof(Json));
            var at = NotNull(JsonHook) ? Il.Single(code, c => c.Calls(JsonHook)) : -1;
            sites = at >= 0 && NotNull(own) && Il.Substitute(code, at, own) ? JsonSites : 0;
        }
        else
        {
            // EntitySites is 1: the single call, as Il.Single finds it
            var locked = AccessTools.Method(typeof(TessSafety), nameof(EntityTesselation));
            var at = NotNull(EntityCall) ? Il.Single(code, c => c.Calls(EntityCall)) : -1;
            sites = at >= 0 && NotNull(locked) && Il.Substitute(code, at, locked) ? EntitySites : 0;
        }

        Volatile.Write(ref _sites, sites);
        if (sites == 0 && Installed) Broken = true;

        return code;
    }

    // Loads of `first` (and loads, or with loads false stores, of `second`) become helper calls, all or none; the count
    private static int Rewrite(List<CodeInstruction> code, int expected, FieldInfo? first, string firstCall,
        FieldInfo? second, string? secondCall, bool loads)
    {
        var one = AccessTools.Method(typeof(TessSafety), firstCall);
        var two = secondCall is null ? null : AccessTools.Method(typeof(TessSafety), secondCall);
        var op = loads ? OpCodes.Ldsfld : OpCodes.Stsfld;
        if (!NotNull(first) || !NotNull(one)) return 0;

        if (Il.Count(code, c => (c.opcode == OpCodes.Ldsfld && Equals(c.operand, first)) ||
                                (c.opcode == op && Equals(c.operand, second))) != expected)
        {
            return 0;
        }

        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
        {
            if (code[i].opcode == OpCodes.Ldsfld && Equals(code[i].operand, first) &&
                Il.Substitute(code, i, one))
            {
                sites++;
            }
            else if (two is not null && code[i].opcode == op && Equals(code[i].operand, second))
            {
                sites += Il.Substitute(code, i, two) ? 1 : 0;
            }
        }

        return Assert(sites == expected) ? sites : 0;
    }

    // A decoder's table: this thread's, or the engine's once a rewrite is undone (the build may write only that one)
    private static Block[]? GetPalette() => Broken ? BlockChunkDataLayer.blocksByPaletteIndex : _palette;

    // The build's table, this thread's and the engine's: a decoder whose rewrite another mod's transpiler undoes reads
    // the engine's field (Broken parks the workers, so one thread writes it)
    private static void SetPalette(Block[]? table) =>
        (_palette, BlockChunkDataLayer.blocksByPaletteIndex) = (table, table);

    private static Vec3f StartRot() => _start ??= new Vec3f();

    private static Vec3f EndRot() => _end ??= new Vec3f();

    // Replaces the virtual call: same dispatch (null throws as the callvirt did), one block entity at a time
    private static bool EntityTesselation(BlockEntity entity, ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        if (!_guarded) return entity.OnTesselation(mesher, tesselator);

        _ = Assert(!Monitor.IsEntered(ShapeGate) && LeavesHeld == 0); // lock order
        lock (EntityGate)
        {
            return entity.OnTesselation(mesher, tesselator);
        }
    }

    // After the lock is taken, each checks the lock order (a leaf finds its own gate held, and no other leaf's)
    private static void EnterBlock(TCTCache? vars, out object? __state)
    {
        __state = _guarded && vars?.block is { } block && GateOf(block) is { } gate && !ReferenceEquals(gate, OwnFlags)
            ? TakeBlock(gate, block)
            : null;
        _ = Assert(__state is null ||
                   (!EntityGate.IsHeldByCurrentThread && !Monitor.IsEntered(ShapeGate) && LeavesHeld == 0));
    }

    private static void EnterDecor(out object? __state)
    {
        __state = _guarded ? Take(DecorGate) : null;
        _ = Assert(__state is null ||
                   (!EntityGate.IsHeldByCurrentThread && !Monitor.IsEntered(ShapeGate) && LeavesHeld == 0));
    }

    private static void EnterShape(out object? __state)
    {
        __state = _guarded ? Take(ShapeGate) : null;
        _ = Assert(__state is null || LeavesHeld == 0);
    }

    // Only while the mesh is missing: the engine rechecks under the lock, so a second thread finds the first's mesh
    private static void EnterMesh(ShapeTesselatorManager? __instance, Block? block, out object? __state)
    {
        var meshes = __instance?.blockModelDatas;
        var missing = meshes is null || block is null || (uint)block.BlockId >= (uint)meshes.Length ||
                      meshes[block.BlockId] is null;
        __state = _guarded && missing ? Take(ShapeGate) : null;
        _ = Assert(__state is null || LeavesHeld == 0);
    }

    // A map in the engine's cache is filled: returned without method or gate; only a miss (may publish early) locks
    private static bool EnterClimate(ClientWorldMap __instance, int chunkX, int chunkZ, ref int[] __result,
        out object? __state)
    {
        __state = null;
        if (_guarded && __instance.TryLoadLerpedClimateMap(chunkX, chunkZ) is { } known)
        {
            __result = known;
            return false;
        }

        __state = Leaf(ClimateGate);
        return true;
    }

    private static void EnterRelight(out object? __state) => __state = Leaf(RelightGate);

    // Locked on every thread, guarded or not: the engine's thread runs unguarded while Parallel is off and a finishing
    // worker would race the lists: two threads inside DoRecycling crash the game. A brief leaf, and
    // unguarded threads hold no other gate.
    private static void EnterRecycle(out object? __state)
    {
        _ = Assert(LeavesHeld == 0) && Assert(Installed);
        Monitor.Enter(RecycleGate);
        __state = RecycleGate;
    }

    // DoRecycling sorts returned meshes into size lists (housekeeping the next call redoes). Nothing queued: answered
    // lock-free, as the engine returns. A guarded thread finding it busy skips (its ConcurrentQueue keeps the items).
    private static bool TryRecycle(MeshDataRecycler __instance, out object? __state)
    {
        __state = null;
        _ = Assert(Installed) && Assert(LeavesHeld == 0); // lock order: a leaf, entered with no other
        if (!Disposed(__instance) && Queued(__instance) is { IsEmpty: true }) return false;

        if (!Monitor.TryEnter(RecycleGate)) return false;

        __state = RecycleGate;
        return true;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "forRecycling")]
    private static extern ref ConcurrentQueue<MeshData>? Queued(MeshDataRecycler recycler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "disposed")]
    private static extern ref bool Disposed(MeshDataRecycler recycler);

    private static void EnterTexture(out object? __state) => __state = Leaf(TextureGate);

    private static object? Leaf(object gate)
    {
        var state = _guarded ? Take(gate) : null;
        _ = Assert(state is null || LeavesHeld == 1);
        return state;
    }

    private static Exception? Leave(Exception? __exception, object? __state)
    {
        if (__state is not null) Monitor.Exit(__state);

        return __exception;
    }

    // A block's lock; a busy one is counted for its block type before the thread waits
    private static object TakeBlock(object gate, Block block)
    {
        if (Monitor.TryEnter(gate)) return gate;

        Waited(ReferenceEquals(gate, FenceGate) ? "fence family" : block.GetType().Name);
        return Take(gate);
    }

    private static void Waited(string name)
    {
        long count;
        lock (WaitGate)
        {
            if (!Waits.TryGetValue(name, out count) && Waits.Count >= MaxWaitTypes) return;

            Waits[name] = ++count;
        }

        if (count >= WaitReport && (count & (count - 1)) == 0 && (BitOperations.TrailingZeroCount(count) & 1) == 0)
        {
            _logger?.Notification("Komet TessSafety: blocks of type {0} found their lock busy {1} times", name, count);
        }
    }

    private static object Take(object gate)
    {
        Monitor.Enter(gate);
        return gate;
    }
}
