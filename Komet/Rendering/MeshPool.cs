using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// MeshDataPool's edits without its passes over every location. InsertAt rebases indices and xyz a vector wide and skips the index
// rebase for a mesh RenderAPIBase.UpdateChunkMesh uploads as SSBO, which never reads Indices. RemoveLocation (List.Remove, then
// CalcFragmentation) becomes a bisection plus RemoveAt. Both keep CalcFragmentation's two sums as exact ints updated from the moved
// location's neighbours; int sums wrap the same in any order, so UsedVertices and CurrentFragmentation are bit for bit the engine's.
// TrySqueezeInbetween answers null where upper bounds on the widest gaps prove a miss. Same-process: 1000 removals over 40 pools
// 28 ms -> 0.8 ms; streaming adds (30 per frame) median 1.46 ms -> 0.14 ms with the skip.
// Invariant: the list changes only through Insert, Add and RemoveAt, so List._version moves on every change (FrustumSweep relies on
// it), and a change Komet did not make is caught by _version and costs one Recount, never a wrong value.
internal static class MeshPool
{
    private const int MaxLocations = 65536, MaxSteps = 32, MaxVertices = 1 << 21, MaxIndices = 1 << 22, MaxLanes = 32;

    // Not by pool id: the decal pool shares id 0
    private static readonly ConditionalWeakTable<MeshDataPool, State> States = [];

    public static bool Enabled { get; set; } = true;
    public static long Models { get; private set; } // totals while Counting.Hud, main thread
    public static long Vertices { get; private set; }
    public static long Skipped { get; private set; } // indices left alone on the SSBO path
    public static long Ticks { get; private set; }
    public static long Removed { get; private set; } // RemoveLocation calls, engine path included
    public static long Squeezes { get; private set; }
    public static long Skips { get; private set; } // squeezes answered by the bounds without a walk
    public static long Recounts { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    internal static extern ref List<ModelDataPoolLocation> Locations(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolId")]
    internal static extern ref int PoolId(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolOrigin")]
    internal static extern ref Vec3i? Origin(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelRef")]
    private static extern ref MeshRef Model(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    internal static extern ref int Version(List<ModelDataPoolLocation> list);

    // The fields of the accessors FrustumSweep shares (modelRef is MeshPool's alone); an accessor on a missing field throws at first use
    internal static bool Seams()
    {
        var pool = typeof(MeshDataPool);
        return Assert(AccessTools.Field(pool, "poolLocations") != null) &&
               Assert(AccessTools.Field(pool, "poolId") != null) &&
               Assert(AccessTools.Field(pool, "poolOrigin") != null) &&
               Assert(AccessTools.Field(typeof(List<ModelDataPoolLocation>), "_version") != null);
    }

    public static void Install(Harmony harmony)
    {
        var insert = AccessTools.Method(typeof(MeshDataPool), "InsertAt");
        var remove = AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.RemoveLocation));
        var squeeze = AccessTools.Method(typeof(MeshDataPool), "TrySqueezeInbetween");
        HarmonyMethod onInsert = new(typeof(MeshPool), nameof(InsertAt)),
            onRemove = new(typeof(MeshPool), nameof(RemoveLocation)),
            onSqueeze = new(typeof(MeshPool), nameof(TrySqueezeInbetween));
        if (!NotNull(harmony) || !NotNull(insert) || !NotNull(remove) || !NotNull(squeeze) || !Seams() ||
            !Assert(AccessTools.Field(typeof(MeshDataPool), "modelRef") != null) ||
            !Assert(Vector<float>.Count is >= 4 and <= MaxLanes) || !Assert(Il.Binds(insert, onInsert.method)) ||
            !Assert(Il.Binds(remove, onRemove.method)) || !Assert(Il.Binds(squeeze, onSqueeze.method))) return;
        _ = NotNull(harmony.Patch(insert, onInsert));
        _ = NotNull(harmony.Patch(remove, onRemove));
        _ = NotNull(harmony.Patch(squeeze, onSqueeze));
    }

    private static bool RemoveLocation(MeshDataPool __instance, ModelDataPoolLocation location)
    {
        if (Counting.Hud) Removed++;
        return !Enabled || !NotNull(__instance) || !Remove(__instance, location);
    }

    private static bool TrySqueezeInbetween(MeshDataPool __instance, MeshData modeldata,
        ref ModelDataPoolLocation? __result)
    {
        if (Counting.Hud) Squeezes++;
        if (!Enabled || !NotNull(__instance) || !Misses(__instance, modeldata)) return true;
        __result = null;
        return false;
    }

    // MeshDataPool.InsertAt in its order, with the rebase a vector wide and the fragmentation from the sums
    private static bool InsertAt(MeshDataPool __instance, ICoreClientAPI capi, MeshData modeldata, Vec3i modelOrigin,
        Sphere frustumCullSphere, int indexPosition, int vertexPosition, int listPosition,
        ref ModelDataPoolLocation __result)
    {
        if (!Enabled || !NotNull(capi) || !NotNull(modeldata) || !NotNull(__instance)) return true;
        var (locations, model) = (Locations(__instance), Model(__instance));
        if (!NotNull(locations) || !NotNull(model) || !NotNull(modeldata.xyz) ||
            !NotNull(modeldata.Indices)) return true;
        if (!Assert(indexPosition >= 0) || !Assert(vertexPosition >= 0) ||
            !Assert(listPosition == -1 || Index(listPosition, locations.Count + 1)) || !Fits(modeldata)) return true;
        var start = Counting.Hud ? Stopwatch.GetTimestamp() : 0;
        Rebase(capi, __instance, modeldata, modelOrigin, vertexPosition);
        Offsets(modeldata, indexPosition, vertexPosition);
        capi.Render.UpdateChunkMesh(model, modeldata);
        var location = new ModelDataPoolLocation
        {
            IndicesStart = indexPosition, IndicesEnd = indexPosition + modeldata.IndicesCount,
            VerticesStart = vertexPosition, VerticesEnd = vertexPosition + modeldata.VerticesCount,
            PoolId = PoolId(__instance), FrustumCullSphere = frustumCullSphere
        };
        var state = Current(__instance, locations); // as of the list one mutation before this insert
        if (listPosition == -1) locations.Add(location);
        else locations.Insert(listPosition, location);
        if (!Inserted(__instance, locations, state, listPosition == -1 ? locations.Count - 1 : listPosition))
            __instance.CalcFragmentation();
        __result = location;
        if (start != 0)
            (Models, Vertices, Ticks) = (Models + 1, Vertices + modeldata.VerticesCount,
                Ticks + Stopwatch.GetTimestamp() - start);
        return false;
    }

    // Nothing may fail once the rebase has begun
    private static bool Fits(MeshData modeldata)
    {
        return Assert(modeldata.VerticesCount is >= 0 and <= MaxVertices) &&
               Assert(modeldata.IndicesCount is >= 0 and <= MaxIndices) &&
               Assert(3 * modeldata.VerticesCount <= modeldata.xyz.Length) &&
               Assert(modeldata.IndicesCount <= modeldata.Indices.Length);
    }

    // The engine rebases the indices only for a mesh that does not start at zero, and the vertices only for a pool that has an origin
    private static void Rebase(ICoreClientAPI capi, MeshDataPool pool, MeshData modeldata, Vec3i modelOrigin,
        int vertexPosition)
    {
        var origin = Origin(pool);
        if (vertexPosition > 0)
        {
            if (KeepsIndices(capi, modeldata)) Shift(modeldata.Indices, modeldata.IndicesCount, vertexPosition);
            else if (Counting.Hud) Skipped += modeldata.IndicesCount;
        }

        if (origin is null || !NotNull(modelOrigin) || !Assert(modeldata.VerticesCount >= 0)) return;
        Shift(modeldata.xyz, modeldata.VerticesCount, modelOrigin.X - origin.X, modelOrigin.Y - origin.Y,
            modelOrigin.Z - origin.Z);
    }

    // RenderAPIBase.UpdateChunkMesh sends everything but wide custom ints down the SSBO path, which never uploads the index values
    private static bool KeepsIndices(ICoreClientAPI capi, MeshData modeldata)
    {
        if (!NotNull(capi.Render) || !Assert(modeldata.IndicesCount >= 0)) return true;
        return !capi.Render.UseSSBOs || modeldata.CustomInts is { InterleaveStride: >= 8 };
    }

    private static void Offsets(MeshData modeldata, int indexPosition, int vertexPosition)
    {
        if (!Assert(vertexPosition >= 0) || !Assert(indexPosition >= 0)) return;
        (modeldata.XyzOffset, modeldata.NormalsOffset, modeldata.RgbaOffset, modeldata.Rgba2Offset) =
            (vertexPosition * 12, vertexPosition * 4, vertexPosition * 4, vertexPosition * 4);
        (modeldata.UvOffset, modeldata.FlagsOffset, modeldata.IndicesOffset) =
            (vertexPosition * 8, vertexPosition * 4, indexPosition * 4);
        if (modeldata.CustomFloats != null)
            modeldata.CustomFloats.BaseOffset = vertexPosition * modeldata.CustomFloats.InterleaveStride;
        if (modeldata.CustomShorts != null)
            modeldata.CustomShorts.BaseOffset = vertexPosition * modeldata.CustomShorts.InterleaveStride;
        if (modeldata.CustomBytes != null)
            modeldata.CustomBytes.BaseOffset = vertexPosition * modeldata.CustomBytes.InterleaveStride;
        if (modeldata.CustomInts != null)
            modeldata.CustomInts.BaseOffset = vertexPosition * modeldata.CustomInts.InterleaveStride;
    }

    internal static void Shift(int[] values, int count, int delta)
    {
        var w = Vector<int>.Count;
        if (!NotNull(values) || !Assert(count >= 0 && count <= values.Length) ||
            !Assert(w is >= 4 and <= MaxLanes)) return;
        var blocks = count / w;
        var d = new Vector<int>(delta);
        for (var b = 0; b < Math.Min(blocks, MaxIndices); b++)
            (new Vector<int>(values, b * w) + d).CopyTo(values, b * w);
        for (var i = blocks * w; i < Math.Min(count, MaxIndices); i++) values[i] += delta;
    }

    // Three vectors carry dx, dy, dz repeated, so a block of three covers 3w floats and always ends on a vertex boundary
    internal static void Shift(float[] xyz, int count, int dx, int dy, int dz)
    {
        var w = Vector<float>.Count;
        var floats = 3 * count;
        if (!NotNull(xyz) || !Assert(count >= 0 && floats <= xyz.Length) || !Assert(w is >= 4 and <= MaxLanes)) return;
        Span<float> pattern = stackalloc float[3 * MaxLanes];
        for (var k = 0; k < Math.Min(3 * w, 3 * MaxLanes); k += 3)
            (pattern[k], pattern[k + 1], pattern[k + 2]) = (dx, dy, dz);
        Vector<float> p0 = new(pattern), p1 = new(pattern[w..]), p2 = new(pattern[(2 * w)..]);
        var blocks = floats / (3 * w);
        for (var b = 0; b < Math.Min(blocks, MaxVertices); b++)
        {
            var i = b * 3 * w;
            (new Vector<float>(xyz, i) + p0).CopyTo(xyz, i);
            (new Vector<float>(xyz, i + w) + p1).CopyTo(xyz, i + w);
            (new Vector<float>(xyz, i + 2 * w) + p2).CopyTo(xyz, i + 2 * w);
        }

        for (var i = blocks * 3 * w; i < Math.Min(floats, 3 * MaxVertices); i += 3)
            (xyz[i], xyz[i + 1], xyz[i + 2]) = (xyz[i] + dx, xyz[i + 1] + dy, xyz[i + 2] + dz);
    }

    // RemoveLocation without the pass; false leaves the call to the engine, which throws its own exception for a location it does not
    // hold. Only a plain ModelDataPoolLocation in a list of nothing else: List.Remove's Equals is then reference equality.
    private static bool Remove(MeshDataPool pool, ModelDataPoolLocation? location)
    {
        if (!NotNull(pool) || location is null || location.GetType() != typeof(ModelDataPoolLocation) ||
            location.PoolId != PoolId(pool)) return false;
        var list = Locations(pool);
        var state = Current(pool, list);
        if (state is null) return false;
        var span = CollectionsMarshal.AsSpan(list);
        var i = Find(span, location, state.Sorted);
        if (i < 0 || !Assert(ReferenceEquals(span[i], location)) || !Assert(state.Count == span.Length)) return false;
        var (prevI, prevV) = i > 0 ? (span[i - 1].IndicesEnd, span[i - 1].VerticesEnd) : (0, 0);
        state.Gaps -= Math.Max(0, location.VerticesStart - prevV);
        if (i + 1 < span.Length)
        {
            var next = span[i + 1];
            state.Gaps += Math.Max(0, next.VerticesStart - prevV) -
                          Math.Max(0, next.VerticesStart - location.VerticesEnd);
            (state.MaxIndexGap, state.MaxVertexGap) = (Math.Max(state.MaxIndexGap, next.IndicesStart - prevI),
                Math.Max(state.MaxVertexGap, next.VerticesStart - prevV)); // the two gaps around it merge
        }

        state.Used -= location.VerticesEnd - location.VerticesStart;
        list.RemoveAt(i);
        if (list.Count == 0)
            (pool.indicesPosition, pool.verticesPosition) = (0, 0); // the engine's position rule, verbatim
        else if (location.IndicesEnd == pool.indicesPosition && location.VerticesEnd == pool.verticesPosition)
            (pool.indicesPosition, pool.verticesPosition) = (list[^1].IndicesEnd, list[^1].VerticesEnd);
        Publish(pool, list, state);
        return true;
    }

    // The CalcFragmentation that ends InsertAt, for the location just put at index. It publishes with verticesPosition as it stands,
    // which for an append is still the old one: the engine calculates before TryAppend advances it, so the first mesh in an empty pool
    // leaves UsedVertices at 0 until the next change, here too. False hands the pass back to the engine.
    private static bool Inserted(MeshDataPool pool, List<ModelDataPoolLocation> list, State? state, int index)
    {
        if (state is null) return false;
        if (!Assert(state.Plain) || !Assert(ReferenceEquals(state.Owner, list) && list.Count == state.Count + 1) ||
            !Index(index, list.Count) || list[index]?.GetType() != typeof(ModelDataPoolLocation))
        {
            state.Owner = null; // whatever happened, the next use counts again
            return false;
        }

        var span = CollectionsMarshal.AsSpan(list);
        var location = span[index];
        var (prevI, prevV) = index > 0 ? (span[index - 1].IndicesEnd, span[index - 1].VerticesEnd) : (0, 0);
        if (index + 1 < span.Length)
        {
            var next = span[index + 1];
            state.Gaps += Math.Max(0, next.VerticesStart - location.VerticesEnd) -
                          Math.Max(0, next.VerticesStart - prevV);
            (state.MaxIndexGap, state.MaxVertexGap) = (
                Math.Max(state.MaxIndexGap, next.IndicesStart - location.IndicesEnd),
                Math.Max(state.MaxVertexGap, next.VerticesStart - location.VerticesEnd));
        }

        state.Gaps += Math.Max(0, location.VerticesStart - prevV);
        (state.MaxIndexGap, state.MaxVertexGap) = (Math.Max(state.MaxIndexGap, location.IndicesStart - prevI),
            Math.Max(state.MaxVertexGap, location.VerticesStart - prevV));
        state.Used += location.VerticesEnd - location.VerticesStart;
        state.Sorted &= (index == 0 || span[index - 1].IndicesStart <= location.IndicesStart) &&
                        (index + 1 == span.Length || location.IndicesStart <= span[index + 1].IndicesStart);
        Publish(pool, list, state);
        return true;
    }

    // True only where TrySqueezeInbetween returns null: no gap wider than the mesh on both axes. The bounds answer when the mesh is at
    // least the widest gap on either axis; otherwise the engine's walk runs here with its comparisons, and a miss sets the bounds to the
    // exact widest gaps. Squeezes only narrow gaps, so bounds that are only raised between full passes stay bounds.
    internal static bool Misses(MeshDataPool pool, MeshData? modeldata)
    {
        if (!NotNull(pool) || modeldata is null) return false;
        var list = Locations(pool);
        var state = Current(pool, list);
        if (state is null) return false;
        var (indices, vertices) = (modeldata.IndicesCount, modeldata.VerticesCount);
        if (indices >= state.MaxIndexGap || vertices >= state.MaxVertexGap)
        {
            if (Counting.Hud) Skips++;
            return true;
        }

        var span = CollectionsMarshal.AsSpan(list);
        if (!Assert(state.Count == span.Length)) return false;
        int prevI = 0, prevV = 0, widestI = int.MinValue, widestV = int.MinValue;
        for (var i = 0; i < Math.Min(span.Length, MaxLocations); i++)
        {
            var location = span[i];
            int gapI = location.IndicesStart - prevI, gapV = location.VerticesStart - prevV;
            if (gapI > indices && gapV > vertices) return false;
            (widestI, widestV) = (Math.Max(widestI, gapI), Math.Max(widestV, gapV));
            (prevI, prevV) = (location.IndicesEnd, location.VerticesEnd);
        }

        (state.MaxIndexGap, state.MaxVertexGap) = (widestI, widestV);
        return Assert(span.Length <= MaxLocations);
    }

    // The pool's state, counted again when its list changed behind it; null when the pool is the engine's alone
    private static State? Current(MeshDataPool pool, List<ModelDataPoolLocation>? list)
    {
        if (!NotNull(list) || list.Count > MaxLocations) return null;
        var state = States.GetValue(pool, static _ => new State());
        if (!ReferenceEquals(state.Owner, list) || state.ListVersion != Version(list) || state.Count != list.Count)
            Recount(state, list);
        return Assert(state.Count == list.Count) && state.Plain ? state : null;
    }

    // CalcFragmentation's pass plus the widest gaps. A null or derived entry leaves the pool to the engine: its pass would throw on the
    // one, and List.Remove would call the other's own Equals.
    private static void Recount(State state, List<ModelDataPoolLocation> list)
    {
        var span = CollectionsMarshal.AsSpan(list);
        if (!Assert(span.Length <= MaxLocations)) return;
        (state.Gaps, state.Used, state.MaxIndexGap, state.MaxVertexGap, state.Plain, state.Sorted) =
            (0, 0, int.MinValue, int.MinValue, true, true);
        int prevI = 0, prevV = 0;
        for (var i = 0; i < Math.Min(span.Length, MaxLocations); i++)
        {
            var location = span[i];
            if (location?.GetType() != typeof(ModelDataPoolLocation))
            {
                state.Plain = false;
                break;
            }

            state.Sorted &= i == 0 || span[i - 1].IndicesStart <= location.IndicesStart;
            state.Gaps += Math.Max(0, location.VerticesStart - prevV);
            state.Used += location.VerticesEnd - location.VerticesStart;
            (state.MaxIndexGap, state.MaxVertexGap) = (Math.Max(state.MaxIndexGap, location.IndicesStart - prevI),
                Math.Max(state.MaxVertexGap, location.VerticesStart - prevV));
            (prevI, prevV) = (location.IndicesEnd, location.VerticesEnd);
        }

        (state.Owner, state.ListVersion, state.Count) = (list, Version(list), list.Count);
        if (Counting.Hud) Recounts++;
    }

    // The engine keeps poolLocations ordered by IndicesStart. Zero-size meshes share their start with a neighbour, so the search takes
    // the first of equal starts and walks them: in a sorted list every copy of a location sits in that run, and the first found is the
    // one List.Remove takes. An unsorted list goes straight to the walk over everything, which finds the first copy too.
    private static int Find(ReadOnlySpan<ModelDataPoolLocation> span, ModelDataPoolLocation location, bool sorted)
    {
        if (!Assert(span.Length <= MaxLocations) || !NotNull(location)) return -1;
        var (lo, hi, key) = (sorted ? 0 : span.Length, span.Length - 1, location.IndicesStart);
        for (var step = 0; step < MaxSteps && lo <= hi; step++)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (span[mid].IndicesStart < key) lo = mid + 1;
            else hi = mid - 1;
        }

        for (var i = lo; i < Math.Min(span.Length, MaxLocations) && span[i].IndicesStart == key; i++)
            if (ReferenceEquals(span[i], location))
                return i;
        for (var i = 0; i < Math.Min(span.Length, MaxLocations); i++)
            if (ReferenceEquals(span[i], location))
                return i;
        return -1;
    }

    // What CalcFragmentation would write, from the sums instead of a pass
    private static void Publish(MeshDataPool pool, List<ModelDataPoolLocation> list, State state)
    {
        if (pool.verticesPosition == 0) (pool.UsedVertices, pool.CurrentFragmentation) = (0, 0f);
        else (pool.UsedVertices, pool.CurrentFragmentation) = (state.Used, (float)state.Gaps / pool.verticesPosition);
        (state.ListVersion, state.Count) = (Version(list), list.Count);
        _ = Assert(ReferenceEquals(state.Owner, list)) && Assert(state.Count <= MaxLocations);
    }

    private sealed class State
    {
        public int ListVersion, Count, Gaps, Used; // the engine sums in int, so these wrap where its pass would

        // Upper bounds, exact after a full pass or a missed walk
        public int MaxIndexGap = int.MinValue, MaxVertexGap = int.MinValue;

        public List<ModelDataPoolLocation>? Owner;
        public bool Plain; // nothing but plain locations, none null
        public bool Sorted; // IndicesStart never falls from one location to the next, so Find may bisect
    }
}
