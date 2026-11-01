using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Opaque terrain culled from GPU-kept rows (rows.comp), one per location, instead of CPU frustum culling. Lists change
// only via MeshDataPool.TryAdd/RemoveLocation (one Insert, Add or RemoveAt). An added location waits
// until its pool draws (AddModelAndStoreLocation sets LOD and chunk after TryAdd); a removed one gives its row to the
// region's last. Other version moves rewrite the list. Hide arrives via its three engine writers; chunks' CullVisible
// bits (culler thread) are read at the frame's first call and when the visible buffer swaps.
internal static partial class OcclusionCulling
{
    public const int MaxRows = 1 << 19;
    private const int MaxChunks = 1 << 18, RowUints = 12 + 6 * FaceSorting.Slabs, RowBytes = 4 * RowUints, MaxRuns = 4096, RereadFrames = 30;
    private const int MaxPoolRows = 65536, MaxChunkLocations = 4096, MaxBlocks = 8192;
    private const int RowsBinding = 9, ChunksBinding = 10, PoolsBinding = 11, FrustumBinding = 15, FrustumDoubles = 68;
    private const int KeysBinding = 12, SortedBinding = 13, Casters = 44; // the camera's planes from this double of the block on
    private const int FirstRows = 1 << 16; // the row arrays' least size at ReadyRows, doubled up to MaxRows (Grown)
    private const int Runs = 4; // a sorted row's commands at most, back faces culled

    private static readonly uint[] PoolRows = new uint[4 * MaxDraws]; // per pool draw: first row, row count
    private static int _rowsProgram, _rowBuffer, _chunkBuffer, _poolBuffer, _frustumBuffer, _sortProgram, _keys, _sorted;
    private static int _rowsUsed, _chunksUsed = 1, _chunksFrom = int.MaxValue, _chunksBefore, _chunkFrame = -1, _chunkIndex;
    private static int _callMaxRows, _surfaceMaxRows, _mainThread;
    private static bool _rowsReady, _rowsFrame, _rowsCall, _rescanHide, _surfaceRows; // the surfaces were culled from rows
    private static bool _relayout; // a region found no room: the next frame lays every region out anew
    private static bool _relaidOut, _fullLogged; // this frame laid every region out anew; the full table was logged
    private static long _fullUntil; // a relayout found no room either: no region is written anew before then
    private static FrustumCulling? _rowsCuller; // the last call's, for the surfaces' second pass

    // Call culls back faces: sorted rows skip away-facing directions (up to Runs commands each)
    private static bool _backFaces, _surfaceBackFaces = true;

    public static bool RowsEnabled { get; set; } = true;

    // Only while Vulkan draws the terrain: under OpenGL the rows cost more than they hide, so OpenGL keeps the engine's
    // culling
    private static bool VulkanDraws => Drawer is not null;

    private static bool RowsWanted => RowsEnabled && VulkanDraws;

    // Switched on and programs built (needs compute doubles)
    internal static bool RowsActive => Active && RowsWanted && _rowsReady;

    // Rows the regions take, let go ones included
    public static int RowsUsed => _rowsUsed;

    // Regions written whole (first draw, or a change past TryAdd/RemoveLocation)
    public static long Rewrites { get; private set; }

    public static long Resyncs { get; private set; }

    // The rows or chunk table is full even when laid out anew: while latched, any call with a pool that needs new rows
    // is culled by the engine
    public static bool RowsFull => Environment.TickCount64 < _fullUntil;

    public static long SortedDraws { get; private set; }

    // Tests and benches: sort regardless of GL state
    internal static bool SortAlways { get; set; }

    // Current Render call is culled from rows: FrustumCull is the GPU's
    internal static bool Bypassing => _rowsCall;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "data")]
    private static extern ref int Bits(Bools bools);

    private static void InstallRows(Harmony harmony)
    {
        _mainThread = Environment.CurrentManagedThreadId;
        var self = typeof(OcclusionCulling);
        var cull = AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull));
        var shown = AccessTools.DeclaredMethod(typeof(ClientChunk), nameof(ClientChunk.SetVisibility));
        var set = AccessTools.DeclaredMethod(typeof(ClientChunk), nameof(ClientChunk.SetPoolLocations));
        var removed = AccessTools.DeclaredMethod(typeof(MeshDataPoolMasterManager),
            nameof(MeshDataPoolMasterManager.RemoveDataPoolLocations));
        if (!NotNull(harmony) || !NotNull(cull) || !NotNull(shown) || !NotNull(set) || !NotNull(removed) ||
            !Assert(AccessTools.Field(typeof(ClientChunk), "centerModelPoolLocations") != null) ||
            !Assert(AccessTools.Field(typeof(Bools), "data")?.FieldType == typeof(int))) return;
        _ = harmony.Patch(cull,
            new HarmonyMethod(AccessTools.Method(self, nameof(Bypassed))) { priority = Priority.First });
        _ = harmony.Patch(shown, postfix: new HarmonyMethod(AccessTools.Method(self, nameof(ChunkShown))));
        var located = new HarmonyMethod(AccessTools.Method(self, nameof(Located)));
        _ = harmony.Patch(set, postfix: located);
        _ = harmony.Patch(removed, postfix: located);
        var (add, remove) = (AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd)),
            AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation)));
        if (!NotNull(add) || !NotNull(remove)) return;
        var before = new HarmonyMethod(AccessTools.Method(self, nameof(Changing))) { priority = Priority.First };
        _ = harmony.Patch(add, before, new HarmonyMethod(AccessTools.Method(self, nameof(Added))));
        _ = harmony.Patch(remove, before, new HarmonyMethod(AccessTools.Method(self, nameof(Removed))));
    }

    // Before every other prefix: version before the change
    internal static void Changing(MeshDataPool __instance, out int __state)
    {
        __state = -1;
        if (NotNull(__instance) && Locations(__instance) is { } list) __state = Version(list);
    }

    internal static void Added(MeshDataPool __instance, ModelDataPoolLocation? __result, int __state)
    {
        if (__result is not null && Current(__instance, __state) is { } region) region.Waiting.Add(__result);
    }

    internal static void Removed(MeshDataPool __instance, ModelDataPoolLocation location, int __state)
    {
        if (location is null || Current(__instance, __state) is not { } region) return;
        if (RowOf.TryGetValue(location, out var row) && row >= region.Base && row < region.Base + region.Count)
            Unrow(region, row);
        else _ = region.Waiting.Remove(location);
    }

    // The pool's region when it was current before the change (then after too, at the new version). Main thread only: a
    // change on another thread leaves it stale (written anew when drawn) and does not read Regions, which the main
    // thread changes.
    private static Region? Current(MeshDataPool pool, int before)
    {
        if (Environment.CurrentManagedThreadId != _mainThread || !Regions.TryGetValue(pool, out var region) ||
            !ReferenceEquals(region.Owner, Locations(pool)) || region.Version != before || region.Owner is not { } list)
            return null;
        region.Version = Version(list);
        return Assert(list.Count <= MaxPoolRows) ? region : null;
    }

    private static void ReadyRows()
    {
        ForgetRows();
        _rowsReady = false;
        if (_logger is null || !Gpu.Doubles()) return;
        (_rowsProgram, _sortProgram) = (Gpu.Program(GpuShaders.Rows, _logger), Gpu.Program(GpuShaders.Sort, _logger));
        (_rowBuffer, _chunkBuffer, _poolBuffer, _frustumBuffer) = (Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer());
        (_keys, _sorted) = (Gpu.Buffer(), Gpu.Buffer());
        Gpu.Upload<uint>(_rowBuffer, [], MaxRows * RowBytes);
        Gpu.Upload<uint>(_chunkBuffer, [], MaxChunks * 4);
        Gpu.Upload<uint>(_poolBuffer, [], 2 * MaxDraws * 8);
        Gpu.Upload<uint>(_keys, [], MaxRanges * 4); // the camera's first pass's commands only
        Gpu.Upload<uint>(_sorted, [], MaxRanges * CommandBytes);
        var size = Math.Clamp(_peakRows, FirstRows, MaxRows);
        (RowData, RowLocations, Marks) = (new uint[size * RowUints], new ModelDataPoolLocation?[size], new int[size]);
        (ChunkBools, ChunkRefs, ChunkBits) = (new Bools?[MaxChunks], new int[MaxChunks], new uint[MaxChunks]);
        _rowsReady = _rowsProgram != 0 && Assert(_keys != 0);
        _ = Assert(_rowBuffer != 0) && Assert(_chunkBuffer != 0);
    }

    private static void ReleaseRows()
    {
        Gpu.DeleteProgram(ref _rowsProgram);
        Gpu.DeleteProgram(ref _sortProgram);
        Gpu.DeleteBuffer(ref _keys);
        Gpu.DeleteBuffer(ref _sorted);
        Gpu.DeleteBuffer(ref _rowBuffer);
        Gpu.DeleteBuffer(ref _chunkBuffer);
        Gpu.DeleteBuffer(ref _poolBuffer);
        Gpu.DeleteBuffer(ref _frustumBuffer);
        _rowsReady = false;
        ForgetRows();
        (RowData, RowLocations, Marks) = ([], [], []);
        (ChunkBools, ChunkRefs, ChunkBits) = ([], [], []);
        (_relayout, _relaidOut, _fullUntil, _fullLogged) = (false, false, 0, false);
    }

    // TerrainRebuild: old pools went with their renderer; new ones get rows from the start, in an empty table
    internal static void Renewed()
    {
        ForgetRows();
        (_relayout, _relaidOut, _fullUntil) = (false, false, 0);
    }

    // PoolTrim deleted the pool: its rows and chunk entries go and its block serves the next region that grows
    internal static void Deleted(MeshDataPool pool)
    {
        if (!NotNull(pool) || !Regions.Remove(pool, out var region)) return;
        Unrow(region);
        region.Waiting.Clear();
        if (region.Capacity > 0 && FreeBlocks.Count < MaxBlocks) FreeBlocks.Add((region.Base, region.Capacity));
    }

    private static void ForgetRows()
    {
        Array.Clear(RowLocations, 0, Math.Min(_rowsUsed, RowLocations.Length));
        Array.Clear(ChunkBools, 0, Math.Min(_chunksUsed, ChunkBools.Length));
        Array.Clear(ChunkRefs, 0, Math.Min(_chunksUsed, ChunkRefs.Length));
        RowOf.Clear();
        Regions.Clear();
        FreeBlocks.Clear();
        ChunkOf.Clear();
        FreeChunks.Clear();
        Dirty.Clear();
        (_rowsUsed, _chunksUsed, _chunkFrame, _chunksFrom, _chunksBefore) = (0, 1, -1, int.MaxValue, 0);
        (_rowsCall, _rescanHide, _surfaceMaxRows, _shadowCall, _shadowFrame) = (false, false, 0, false, -1);
    }

    // engineless: GPU tests, rows whatever the backend
    private static void BeginRows(bool engineless)
    {
        _rowsFrame = (RowsWanted || engineless && RowsEnabled) && _rowsReady;
        (_surfaceMaxRows, _surfaceRows, _surfaceBackFaces) = (0, false, true);
        if (!_rowsFrame) return;
        _relaidOut = _relayout;
        if (_relayout) ForgetRows();
        _relayout = false;
        if (_rescanHide) Rescan();
    }

    // False (call not held) when a pool lacks room or the culler is not ready (no player: engine culling would throw)
    private static bool Rowed(FrustumCulling? culler, List<MeshDataPool> pools, bool on)
    {
        _callMaxRows = 0;
        if (!on || culler is null || PlayerPos(culler) is null || Planes(culler) is not { Length: >= 6 }) return false;
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var pool = pools[i];
            if (Dimension(pool) != 0) continue;
            if (RegionOf(pool) is not { } region) return false;
            _callMaxRows = Math.Max(_callMaxRows, region.Count);
        }

        return Assert(_callMaxRows <= MaxPoolRows);
    }

    // Postfixes on the engine methods that write Hide
    private static void ChunkShown(ClientChunk __instance)
    {
        if (!NotNull(__instance) || RowOf.Count == 0) return;
        Rehide(Center(__instance));
        Rehide(Edge(__instance));
    }

    // First argument, named differently per seam: Harmony passes it by position
    private static void Located(ModelDataPoolLocation[] __0) => Rehide(__0);

    internal static void Rehide(ModelDataPoolLocation[]? locations)
    {
        if (locations is null || RowOf.Count == 0) return;
        if (Environment.CurrentManagedThreadId != _mainThread)
        {
            _rescanHide = true; // every location is read again at the next frame's start
            return;
        }

        foreach (var location in locations.Bounded(MaxChunkLocations))
            if (location is not null && RowOf.TryGetValue(location, out var row))
                Hide(row, location.Hide);
    }

    private static void Hide(int row, bool hide)
    {
        if (!Index(row, MaxRows) || !Assert(row < _rowsUsed)) return;
        var at = row * RowUints + 8;
        var flags = (RowData[at] & ~1u) | (hide ? 1u : 0u);
        if (flags == RowData[at]) return;
        RowData[at] = flags;
        Changed(row, 1);
    }

    private static void Rescan()
    {
        _rescanHide = false;
        for (var row = 0; row < Math.Min(_rowsUsed, MaxRows); row++)
            if (RowLocations[row] is { } location)
                Hide(row, location.Hide);
    }

    // First FrustumCull prefix: in a rows call a pool with locations always draws, triangles counted as allocated
    private static bool Bypassed(MeshDataPool __instance)
    {
        if (!_rowsCall || !NotNull(__instance) || Dimension(__instance) != 0) return true;
        var has = Locations(__instance) is { Count: > 0 };
        __instance.indicesGroupsCount = has ? 1 : 0;
        if (has && __instance.indicesSizes is { Length: > 0 } sizes) sizes[0] = 0; // the engine would draw nothing
        var triangles = Regions.TryGetValue(__instance, out var region) ? region.Triangles : 0;
        (__instance.RenderedTriangles, __instance.AllocatedTris) = (triangles, triangles);
        return false;
    }

    private static void UploadRows((int First, int Count) draws, FrustumCulling culler, Vec3d camera,
        (int Mode, bool BackFaces) how)
    {
        Dirty.Sort();
        var refused = Gpu.Refused;
        var (start, end) = (-1, -1);
        foreach (var (from, count) in Dirty.Bounded(MaxRuns))
        {
            if (from <= end)
            {
                end = Math.Max(end, from + count);
                continue;
            }

            UploadRun(start, end);
            (start, end) = (from, from + count);
        }

        UploadRun(start, end);
        // a run the frame had no staging for stays dirty: its rows on the GPU are zeros (hidden) until sent
        if (Gpu.Refused == refused) Dirty.Clear();
        Gpu.UploadAt<uint>(_poolBuffer, draws.First * 8, PoolRows.AsSpan(2 * draws.First, 2 * draws.Count));
        UploadChunks();
        Frustum(culler, camera, how);
    }

    private static void UploadRun(int start, int end)
    {
        if (start < 0 || !Assert(end <= MaxRows) || end <= start || !Assert(end <= _rowsUsed)) return;
        Gpu.UploadAt<uint>(_rowBuffer, start * RowBytes, RowData.AsSpan(start * RowUints, (end - start) * RowUints));
    }

    // All chunk bits reread on a visible-buffer swap and every RereadFrames (the engine's culler sets bits without a swap
    // with few chunks loaded, as after a join or teleport: they stayed hidden), else only new entries
    private static void UploadChunks()
    {
        var index = ModelDataPoolLocation.VisibleBufIndex;
        var (from, before) = (_chunksFrom, _chunksBefore);
        if (_chunkIndex != index || _chunkFrame < 0 || _frame - _chunkFrame >= RereadFrames)
        {
            (_chunkFrame, _chunkIndex, from, before) = (_frame, index, 1, _chunksUsed);
            for (var c = 1; c < Math.Min(Math.Min(_chunksUsed, ChunkBits.Length), MaxChunks); c++)
                ChunkBits[c] = ChunkBools[c] is { } bools ? (uint)Bits(bools) : 0;
        }

        (_chunksFrom, _chunksBefore) = (int.MaxValue, 0);
        if (from >= before || !Assert(before <= ChunkBits.Length)) return;
        Gpu.UploadAt<uint>(_chunkBuffer, from * 4, ChunkBits.AsSpan(from, before - from));
    }

    // rows.comp Frustum block as IsVisible reads it; mode 0 CullNormal, 1/2 near/far shadow
    private static void Frustum(FrustumCulling culler, Vec3d camera, (int Mode, bool BackFaces) how)
    {
        var mode = how.Mode;
        var (planes, player) = (Planes(culler), PlayerPos(culler));
        if (!NotNull(planes) || !NotNull(player) || !Assert(planes.Length >= 6)) return;
        Span<double> block = stackalloc double[FrustumDoubles];
        block.Clear();
        for (var p = 0; p < 6; p++)
            (block[4 * p], block[4 * p + 1], block[4 * p + 2], block[4 * p + 3]) =
                (planes[p].normalX, planes[p].normalY, planes[p].normalZ, planes[p].D);
        (block[24], block[25], block[26]) = (camera.X, camera.Y, camera.Z);
        (block[28], block[29]) = (player.X, player.Z);
        (block[32], block[33], block[34]) = ((double)(culler.lod0BiasSq + 1024f), culler.ViewDistanceSq, culler.lod2BiasSq);
        block[35] = culler.lod0BiasSq > 0f ? 1 : 0;
        (block[36], block[37]) = (culler.shadowRangeX, culler.shadowRangeZ);
        var casting = mode is 1 or 2 && _casting;
        if (casting) ShadowView.Moved(block[Casters..]);
        (block[64], block[65], block[66]) = (camera.X + _view.X, camera.Y + _view.Y, camera.Z + _view.Z);
        var ints = MemoryMarshal.Cast<double, int>(block);
        // the visible buffer the chunk bits were read for (UploadChunks), LateRows too; 2: each slab tested
        (ints[80], ints[81], ints[82], ints[83]) = (_chunkIndex, mode, how.BackFaces ? 2 : 0, casting ? 1 : 0);
        Gpu.Upload<double>(_frustumBuffer, block);
        Gpu.Uniforms(FrustumBinding, _frustumBuffer);
    }

    private static void BindRows(int counters)
    {
        Bind(counters);
        Gpu.Storage(RowsBinding, _rowBuffer);
        Gpu.Storage(ChunksBinding, _chunkBuffer);
        Gpu.Storage(PoolsBinding, _poolBuffer);
        Gpu.Storage(KeysBinding, _keys);
    }

    // After a camera rows pass (base 0, params bound): copies each draw's commands near to far into the sorted buffer,
    // false without the program. Shadows unsorted: light-first was no faster (TerrainBenchGpuTests).
    private static bool Sorted((int First, int Count) draws)
    {
        if (_sortProgram == 0 || !Assert(_sorted != 0)) return false;
        Gpu.Storage(SortedBinding, _sorted);
        Gpu.Dispatch(_sortProgram, 1, draws.Count);
        Gpu.Barrier(GpuBarrier.Indirect | GpuBarrier.Storage);
        SortedDraws += draws.Count;
        return true;
    }

    // Depth LESS/LEQUAL+write, no blending (GL state as Vulkan taps it): nearest wins in any order; untapped: unknown
    internal static bool Orderless()
    {
        if (SortAlways) return true;
        if (Links.TapState() is not { } state) return false;
        if (!state.DepthTest || state.DepthCompare is not (1 or 3) || !state.DepthWrite) return false;
        return !state.Blending;
    }

    // Each draw's commands region is its pool's row count
    private static void IssuingRows(FrustumCulling culler, List<MeshDataPool> pools, Vec3d camera)
    {
        var (first, cursor) = (_frameRanges, _frameRanges);
        if (!NotNull(culler)) return;
        var draws = Lay(pools, camera, ref cursor, (_frameDraws, MaxDraws), _backFaces ? Runs : 1);
        _rowsCuller = culler;
        if (draws > _frameDraws) Cull((first, cursor - first), (_frameDraws, draws - _frameDraws), (culler, camera));
        if (_lastSurface && _late) FinishSurfaces();
    }

    // Held pools' draws from draws.First (before End), commands from cursor (factor a row); returns the next draw
    private static int Lay(List<MeshDataPool> pools, Vec3d camera, ref int cursor, (int First, int End) draws, int factor)
    {
        var (next, held) = (draws.First, 0);
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var pool = pools[i];
            if (Dimension(pool) != 0 || !Taken(ModelRef(pool), ref held) || !Index(next, draws.End) ||
                !Regions.TryGetValue(pool, out var region)) continue;
            var count = Math.Min(region.Count, (MaxRanges - cursor) / factor);
            (PoolRows[2 * next], PoolRows[2 * next + 1]) = ((uint)region.Base, (uint)count);
            Drawn(next++, pool, camera, cursor, factor * count);
            cursor += factor * count;
        }

        Held.Clear();
        return next;
    }

    // One non-surface rows call, engine-free (GPU tests): pools need locations, dimension 0, own VAO; draws nothing
    internal static bool RunRows(List<MeshDataPool> pools, FrustumCulling culler, Vec3d camera, float[] viewProjection,
        bool backFaces = false)
    {
        if (!NotNull(pools) || !NotNull(culler) || !Assert(viewProjection.Length == 16) || !Ready()) return false;
        BeginFrame(true);
        viewProjection.CopyTo(ViewProjection, 0);
        (_captured, _camera, _surface, _lastSurface, _builtThisFrame) = (true, camera, false, false, true);
        if (!Rowed(culler, pools, _rowsFrame))
        {
            EndFrame();
            return false;
        }

        Held.Clear();
        foreach (var pool in pools.Bounded(MaxPools))
            if (ModelRef(pool) is { } model)
                Held.Add(model);
        _backFaces = backFaces;
        IssuingRows(culler, pools, camera);
        _backFaces = false;
        EndFrame();
        return true;
    }

    // Back faces culled and no face moved by the VS (GL state as Vulkan taps it, else RenderOpaque's; no world warp)
    private static bool BackFacesCulled()
    {
        if (!RowsEnabled || !NotNull(_api) || !ShadowView.Unwarped(_api)) return false;
        if (Links.TapState() is not { } state) return true;
        return state.Culls == 2 && state.CounterClockwise && Assert(state.Depth < 128);
    }

    // Surfaces' second pass over their rows, under the last call's frustum block
    private static void LateRows(int counters, bool again)
    {
        if (_rowsCuller is not { } culler || _camera is not { } camera) return;
        Frustum(culler, camera, (0, _surfaceBackFaces));
        BindRows(counters);
        RowsPass(ViewProjection, (0, 0, 0), (1, again ? Again : KeepAgain, StatUints + MaxDraws, MaxRanges),
            (0, _surfaceDraws), _surfaceMaxRows);
    }

    // One work-group row per pool draw, x as wide as the largest pool needs
    private static void RowsPass(float[] matrix, (double X, double Y, double Z) shift, (int, int, int, int) flags,
        (int First, int Count) draws, int rows)
    {
        if (rows > 0) Dispatch(_rowsProgram, (matrix, shift, flags), ((0, 0), draws), ((rows + Group - 1) / Group, draws.Count));
    }
}
