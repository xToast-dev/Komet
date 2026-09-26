using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using Vintagestory.Common.Database;
using Monitor = System.Threading.Monitor;

namespace Komet.Tessellation;

// What the engine's tessellation shares between passes, made safe for passes on several threads (TessWorkers on Komet's worker
// threads); two threads never tessellate the same chunk (TessQueue). Audited against the 1.22.7 engine and the vanilla mods, each
// hazard gets the smallest change that makes two passes at once behave as two passes one after the other:
// - BlockChunkDataLayer.blocksByPaletteIndex, one static table (palette index -> block) that ClientChunkData.BuildFastBlockAccessArray
//   fills for the chunk being tessellated (3 reads, 1 write) and the getBlockOne..Five/GeneralCase decoders read (1 read each): one
//   per thread (transpilers), so a thread decodes its chunk with the table its chunk built. ExtendedRows reads the same (Palette).
// - CrossTesselator.startRot/endRot, static Vec3f whose contents DrawCross overwrites for randomly rotated crosses: one pair per thread.
// - BlockEntity.OnTesselation (JsonTesselator.Tesselate, BleedingJsonTesselator.Tesselate, one call each): the vanilla block entities
//   build and cache meshes in api.ObjectCache (a plain Dictionary), in block scratch fields their texture sources read, in lazily
//   published arrays and through the animation cache. One lock for all, so they run one at a time as on the one thread.
// - Block.OnJsonTesselation, which JsonTesselator.doMesh and BleedingJsonTesselator.DoMeshWithBleeding call on the block's shared
//   mesh: the overrides write per-position flags into it or fill block fields lazily, so a block with an override is meshed by one
//   thread at a time (a lock on the block). BlockFenceStackAware shares its mesh cache and swaps a texture id across the fence family:
//   one lock for all of those. Block's own method ORs the same wind bits every time and takes no lock.
// - BuildDecorPolygons raises a shared block's VertexFlags.ZOffset around a call and puts it back: one lock.
// - Lazy shapes and meshes: UnloadableShape.Load marks a shape loaded before it parses it (shapes are unloaded when
//   ClientSettings.OptimizeRamMode is 2), and a block's default mesh is built on first use: ShapeTesselator.TesselateShape (the
//   CompositeShape overload, which loads), ShapeTesselatorManager.GetCachedShape, and GetDefaultBlockMesh while the mesh is missing
//   run under one lock.
// - TerrainIlluminator.SunRelightChunk (one ChunkIlluminator and its scratch, light written into neighbour columns), the
//   MeshDataRecycler (unsynchronised sorted lists), the texture atlas and BlendedTextureManager (unsynchronised dictionaries, a shared
//   Random) and LoadOrCreateLerpedClimateMapOffthread (the placeholder map is published before it is filled): one lock each.
// The locks are taken only by threads whose passes are guarded (Guard): a worker thread always, the tessellation thread while Parallel is
// on. The main thread keeps the races with the tessellation thread it always had. Lock order: decor, block (or fence family), block
// entity, shape, then the leaves climate, relight, recycler, textures - no thread takes an earlier one while holding a later one.
internal static class TessSafety
{
    private const int PaletteBuildSites = 4, DecoderSites = 1, CrossSites = 8, EntitySites = 1, MaxPatches = 64;
    private const int MaxBlocks = 1 << 20, MaxDepth = 32; // MaxDepth: of a block's class hierarchy
    private const string SharedFamily = "Vintagestory.GameContent.BlockFenceStackAware";

    private static readonly Lock EntityGate = new(), TableGate = new();
    private static readonly object FenceGate = new(), DecorGate = new(), ShapeGate = new(), ClimateGate = new();
    private static readonly object RelightGate = new(), RecycleGate = new(), TextureGate = new();

    private static readonly FieldInfo? PaletteField =
        AccessTools.DeclaredField(typeof(BlockChunkDataLayer), "blocksByPaletteIndex");

    private static readonly FieldInfo? StartField = AccessTools.DeclaredField(typeof(CrossTesselator), "startRot");
    private static readonly FieldInfo? EndField = AccessTools.DeclaredField(typeof(CrossTesselator), "endRot");

    private static readonly MethodInfo? EntityCall = AccessTools.DeclaredMethod(typeof(BlockEntity),
        nameof(BlockEntity.OnTesselation), [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);

    // Block.OnJsonTesselation's parameters, before JsonHook: static fields are initialised in order
    private static readonly Type[] JsonArgs =
    [
        typeof(MeshData).MakeByRefType(), typeof(int[]).MakeByRefType(), typeof(BlockPos), typeof(Block[]), typeof(int)
    ];

    private static readonly MethodInfo? JsonHook =
        AccessTools.DeclaredMethod(typeof(Block), nameof(Block.OnJsonTesselation), JsonArgs);

    [ThreadStatic] private static Block[]? _palette;
    [ThreadStatic] private static Vec3f? _start, _end;
    [ThreadStatic] private static bool _guarded; // this thread's passes take the locks
    private static readonly List<(MethodBase Target, MethodInfo Patch)> Patched = [];
    private static BlockGate?[] _gates = [];
    private static int _parallel;

    private static int _sites;

    // Every patch below is in: the only state in which TessWorkers runs passes
    public static bool Installed { get; private set; }

    // A rewrite found another count when Harmony ran it again: no workers
    public static bool Broken { get; private set; }

    public static bool Parallel => Volatile.Read(ref _parallel) != 0; // workers may pass

    // The table BuildFastBlockAccessArray built on this thread, or the engine's while TessSafety is not installed
    public static Block[]? Palette()
    {
        return Installed ? _palette : BlockChunkDataLayer.blocksByPaletteIndex;
    }

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

    // All or nothing: a transpiler that does not find exactly its sites leaves the IL alone, and then every patch made so far goes
    // again (half of the palette sites moved would leave a decoder reading a table nobody fills)
    public static void Install(Harmony harmony, ILogger? logger = null)
    {
        Clear();
        if (!NotNull(harmony) || !NotNull(PaletteField) || !NotNull(StartField) || !NotNull(EndField) ||
            !NotNull(EntityCall) || !NotNull(JsonHook)) return;
        var ok = Transpilers(harmony) && Locks(harmony) && Leaves(harmony);
        if (ok)
        {
            Installed = true;
            return;
        }

        lock (TableGate)
        {
            foreach (var (target, patch) in Patched.Bounded(MaxPatches)) harmony.Unpatch(target, patch);
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
            ok &= Transpile(harmony, TessSeams.Method(layer, name, typeof(int)));
        ok &= Transpile(harmony,
            TessSeams.Method(ExtendedRows.ClientType, "BuildFastBlockAccessArray", typeof(Block[])));
        ok &= Transpile(harmony,
            TessSeams.Method(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross), vars, typeof(float)));
        ok &= Transpile(harmony, TessSeams.Method(typeof(JsonTesselator), nameof(JsonTesselator.Tesselate), vars));
        ok &= Transpile(harmony,
            TessSeams.Method(typeof(BleedingJsonTesselator), nameof(BleedingJsonTesselator.Tesselate), vars));
        return ok;
    }

    private static bool Locks(Harmony harmony)
    {
        var (vars, manager, i) = (typeof(TCTCache), typeof(ShapeTesselatorManager), typeof(int));
        Type[] shape =
        [
            typeof(string), typeof(AssetLocation), typeof(CompositeShape), typeof(MeshData).MakeByRefType(),
            typeof(ITexPositionSource), i, typeof(byte), typeof(byte), typeof(int?), typeof(string[])
        ];
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
               Assert(Patched.Count <= MaxPatches);
    }

    private static bool Leaves(Harmony harmony)
    {
        var (atlas, i, position) = (typeof(TextureAtlasManager), typeof(int), typeof(TextureAtlasPosition));
        Type[] insert =
        [
            typeof(AssetLocationAndSource), i.MakeByRefType(), position.MakeByRefType(), typeof(CreateTextureDelegate),
            typeof(float)
        ];
        Type[] allocate = [i, i, i.MakeByRefType(), position.MakeByRefType(), typeof(AssetLocationAndSource)];
        Type[] blend = [i, typeof(int[]), typeof(int[]), typeof(int[]), atlas, i];
        var recycler = typeof(MeshDataRecycler);
        return Around(harmony, TessSeams.Method(typeof(ClientWorldMap),
                       nameof(ClientWorldMap.LoadOrCreateLerpedClimateMapOffthread), i, i), nameof(EnterClimate)) &&
               Around(harmony, TessSeams.Method(typeof(TerrainIlluminator), nameof(TerrainIlluminator.SunRelightChunk),
                   typeof(ClientChunk), typeof(ChunkPos)), nameof(EnterRelight)) &&
               Around(harmony, TessSeams.Method(recycler, nameof(MeshDataRecycler.GetOrCreateMesh), i),
                   nameof(EnterRecycle)) &&
               Around(harmony, TessSeams.Method(recycler, nameof(MeshDataRecycler.DoRecycling)),
                   nameof(EnterRecycle)) &&
               Around(harmony, TessSeams.Method(atlas, nameof(TextureAtlasManager.AllocateTextureSpace), allocate),
                   nameof(EnterTexture)) &&
               Around(harmony, TessSeams.Method(atlas, nameof(TextureAtlasManager.GetOrInsertTexture), insert),
                   nameof(EnterTexture)) &&
               Around(harmony,
                   TessSeams.Method(atlas, "runtimeUpdateTexture", typeof(IBitmap), position, typeof(float)),
                   nameof(EnterTexture)) &&
               Around(harmony, TessSeams.Method(typeof(BlendedTextureManager),
                       nameof(BlendedTextureManager.GetOrCreateBlendedTexture), blend),
                   nameof(EnterTexture)) && Assert(Patched.Count <= MaxPatches); // the unpatch loop takes MaxPatches
    }

    // A transpiler that finds exactly its sites (it records the count it rewrote, 0 when another count leaves the IL alone)
    private static bool Transpile(Harmony harmony, MethodInfo? target)
    {
        var patch = AccessTools.Method(typeof(TessSafety), nameof(SitesIl));
        if (!NotNull(target) || !NotNull(patch)) return false;
        _sites = 0;
        _ = NotNull(harmony.Patch(target, transpiler: new HarmonyMethod(patch)));
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
        _ = NotNull(harmony.Patch(target, new HarmonyMethod(enter), finalizer: new HarmonyMethod(leave)));
        lock (TableGate)
        {
            Patched.Add((target, enter));
            Patched.Add((target, leave));
        }

        return true;
    }

    // One for every site kind, by the method it rewrites. Harmony runs it again whenever another patch lands on the method: a count
    // that no longer fits then leaves that method's IL alone, and the workers stay parked for good (Broken).
    private static List<CodeInstruction> SitesIl(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        if (!Assert(Il.Take(instructions, Il.MaxInstructions, out var code)) || !NotNull(original)) return code;
        int sites;
        if (original.DeclaringType == typeof(CrossTesselator))
            sites = Rewrite(code, CrossSites, StartField, nameof(StartRot), EndField, nameof(EndRot), true);
        else if (original.Name == "BuildFastBlockAccessArray")
            sites = Rewrite(code, PaletteBuildSites, PaletteField, nameof(GetPalette), PaletteField, nameof(SetPalette),
                false);
        else if (original.DeclaringType == typeof(BlockChunkDataLayer))
            sites = Rewrite(code, DecoderSites, PaletteField, nameof(GetPalette), null, null, false);
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

    // Loads of `first` (and loads or, with loads false, stores of `second`) become calls of the named helpers - all of them when there
    // are exactly `expected`, else none; the count rewritten
    private static int Rewrite(List<CodeInstruction> code, int expected, FieldInfo? first, string firstCall,
        FieldInfo? second, string? secondCall, bool loads)
    {
        var one = AccessTools.Method(typeof(TessSafety), firstCall);
        var two = secondCall is null ? null : AccessTools.Method(typeof(TessSafety), secondCall);
        var op = loads ? OpCodes.Ldsfld : OpCodes.Stsfld;
        if (!NotNull(first) || !NotNull(one)) return 0;
        if (Il.Count(code, c => (c.opcode == OpCodes.Ldsfld && Equals(c.operand, first)) ||
                                (c.opcode == op && Equals(c.operand, second))) != expected) return 0;
        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
            if (code[i].opcode == OpCodes.Ldsfld && Equals(code[i].operand, first) &&
                Il.Substitute(code, i, one)) sites++;
            else if (two is not null && code[i].opcode == op && Equals(code[i].operand, second) &&
                     Il.Substitute(code, i, two)) sites++;

        return Assert(sites == expected) ? sites : 0;
    }

    private static Block[]? GetPalette()
    {
        return _palette;
    }

    private static void SetPalette(Block[]? table)
    {
        _ = Assert(table is null || table.Length > 0);
        _palette = table;
    }

    private static Vec3f StartRot()
    {
        return _start ??= new Vec3f();
    }

    private static Vec3f EndRot()
    {
        return _end ??= new Vec3f();
    }

    // In place of the virtual call: the same dispatch (a null entity throws as the callvirt did), one block entity at a time
    private static bool EntityTesselation(BlockEntity entity, ITerrainMeshPool mesher, ITesselatorAPI tesselator)
    {
        if (!_guarded) return entity.OnTesselation(mesher, tesselator);
        _ = Assert(!Monitor.IsEntered(ShapeGate) && LeavesHeld() == 0); // lock order
        lock (EntityGate)
        {
            return entity.OnTesselation(mesher, tesselator);
        }
    }

    // After the lock is taken, each checks the lock order (a leaf finds its own gate held, and no other leaf's)
    private static void EnterBlock(TCTCache? vars, out object? __state)
    {
        __state = _guarded && vars?.block is { } block && GateOf(block) is { } gate ? Take(gate) : null;
        _ = Assert(__state is null ||
                   (!EntityGate.IsHeldByCurrentThread && !Monitor.IsEntered(ShapeGate) && LeavesHeld() == 0));
    }

    private static void EnterDecor(out object? __state)
    {
        __state = _guarded ? Take(DecorGate) : null;
        _ = Assert(__state is null ||
                   (!EntityGate.IsHeldByCurrentThread && !Monitor.IsEntered(ShapeGate) && LeavesHeld() == 0));
    }

    private static void EnterShape(out object? __state)
    {
        __state = _guarded ? Take(ShapeGate) : null;
        _ = Assert(__state is null || LeavesHeld() == 0);
    }

    // Only while the mesh is missing: the engine checks again under the lock, so a second thread finds the first one's mesh
    private static void EnterMesh(ShapeTesselatorManager? __instance, Block? block, out object? __state)
    {
        var meshes = __instance?.blockModelDatas;
        var missing = meshes is null || block is null || (uint)block.BlockId >= (uint)meshes.Length ||
                      meshes[block.BlockId] is null;
        __state = _guarded && missing ? Take(ShapeGate) : null;
        _ = Assert(__state is null || LeavesHeld() == 0);
    }

    private static void EnterClimate(out object? __state)
    {
        __state = _guarded ? Take(ClimateGate) : null;
        _ = Assert(__state is null || LeavesHeld() == 1);
    }

    private static void EnterRelight(out object? __state)
    {
        __state = _guarded ? Take(RelightGate) : null;
        _ = Assert(__state is null || LeavesHeld() == 1);
    }

    private static void EnterRecycle(out object? __state)
    {
        __state = _guarded ? Take(RecycleGate) : null;
        _ = Assert(__state is null || LeavesHeld() == 1);
    }

    private static void EnterTexture(out object? __state)
    {
        __state = _guarded ? Take(TextureGate) : null;
        _ = Assert(__state is null || LeavesHeld() == 1);
    }

    private static Exception? Leave(Exception? __exception, object? __state)
    {
        if (__state is null || !Assert(Monitor.IsEntered(__state))) return __exception;
        Monitor.Exit(__state);
        return __exception;
    }

    private static object Take(object gate)
    {
        _ = Assert(_guarded && Installed);
        Monitor.Enter(gate);
        return gate;
    }

    // The leaf locks this thread holds
    private static int LeavesHeld()
    {
        return (Monitor.IsEntered(ClimateGate) ? 1 : 0) + (Monitor.IsEntered(RelightGate) ? 1 : 0) +
               (Monitor.IsEntered(RecycleGate) ? 1 : 0) + (Monitor.IsEntered(TextureGate) ? 1 : 0);
    }

    // The lock a block is meshed under: none for Block's own OnJsonTesselation (unless another mod patches it), the fence family's,
    // or the block itself; worked out once per block and kept by BlockId
    private static object? GateOf(Block block)
    {
        var (gates, id) = (Volatile.Read(ref _gates), block.BlockId);
        if ((uint)id < (uint)gates.Length && gates[id] is { } known && ReferenceEquals(known.Owner, block))
            return known.Monitor;
        var gate = new BlockGate(block, Pick(block));
        if (!Index(id, MaxBlocks)) return gate.Monitor;
        lock (TableGate)
        {
            var table = _gates;
            if (id >= table.Length) Array.Resize(ref table, Math.Min(Math.Max(id + 1, 2 * table.Length), MaxBlocks));
            table[id] = gate;
            Volatile.Write(ref _gates, table);
        }

        return gate.Monitor;
    }

    private static object? Pick(Block block)
    {
        if (!NotNull(block)) return FenceGate;
        try
        {
            var type = block.GetType();
            var family = type;
            for (var depth = 0; depth < MaxDepth && family is not null; depth++)
            {
                if (family.FullName == SharedFamily) return FenceGate;
                family = family.BaseType;
            }

            var hook = type.GetMethod(nameof(Block.OnJsonTesselation), BindingFlags.Public | BindingFlags.Instance,
                JsonArgs);
            var own = hook?.DeclaringType == typeof(Block) &&
                      !EngineShape.Foreign([JsonHook], EngineShape.Kinds.All, null);
            return own ? null : block;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return block;
        }
    }

    private sealed record BlockGate(Block Owner, object? Monitor);
}
