using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Komet.Rendering;

// MeshData.CloneUsingRecycler hands a tesselated chunk a mesh with its basic arrays kept, but CloneExtraData clones the extra data from
// scratch and DisposeExtraData drops it: two thirds of the client's allocations. A recyclable mesh keeps those arrays as capacity for the
// next clone, and custom parts copy Count values instead of the whole tesselator buffer SetFrom clones. Fields the source lacks get the
// state of a fresh mesh: null, TextureIds empty.
internal static class MeshRecycle
{
    private static readonly DataConversion ShortConversion = new CustomMeshDataPartShort().Conversion;
    private static readonly DataConversion IntConversion = new CustomMeshDataPartInt().Conversion;

    // A mesh in the recycler carries buffers, so the clone has to keep clearing what a source lacks
    private static bool _kept;

    // Totals while Counting.Hud; Interlocked: the tesselation and the main thread clone
    private static long _clones, _reused, _saved;

    public static bool Enabled { get; set; } = true;
    public static long Clones => Interlocked.Read(ref _clones);
    public static long Reused => Interlocked.Read(ref _reused);
    public static long Saved => Interlocked.Read(ref _saved);

    private static void Reuse(long values)
    {
        _ = Interlocked.Increment(ref _reused);
        _ = Interlocked.Add(ref _saved, values);
    }

    public static void Install(Harmony harmony)
    {
        _kept = false; // the previous world's recycler is gone
        var clone = AccessTools.Method(typeof(MeshData), "CloneExtraData");
        var dispose = AccessTools.Method(typeof(MeshData), "DisposeExtraData");
        var part = typeof(CustomMeshDataPart<float>);
        var prefix = new HarmonyMethod(CloneExtraData);
        if (!NotNull(harmony) || !NotNull(clone) || !NotNull(dispose) || !Assert(Il.Binds(clone, prefix.method)) ||
            !Assert(AccessTools.Field(part, "customAllocationSize") != null) ||
            !Assert(AccessTools.Field(part, "allocationSize") != null))
            return; // the Allocation accessors would throw at first use
        _ = NotNull(harmony.Patch(clone, prefix));
        _ = NotNull(harmony.Patch(dispose, new HarmonyMethod(DisposeExtraData)));
    }

    // The engine copies TextureIds only along with TextureIndices, and throws when those come without ids: that case stays its own
    internal static bool CloneExtraData(MeshData __instance, MeshData dest)
    {
        if ((!Enabled && !_kept) || !NotNull(__instance) || !NotNull(dest) || ReferenceEquals(__instance, dest))
            return true;
        var source = __instance;
        if (source.TextureIndices != null && source.TextureIds == null) return true;
        dest.Normals = Exact(dest.Normals, source.Normals, source.NormalsCount);
        dest.XyzFaces = Exact(dest.XyzFaces, source.XyzFaces, source.XyzFacesCount);
        dest.TextureIndices = Exact(dest.TextureIndices, source.TextureIndices, source.TextureIndicesCount);
        dest.TextureIds =
            source.TextureIndices is null ? [] : Exact(dest.TextureIds, source.TextureIds, source.TextureIds!.Length);
        dest.ClimateColorMapIds = Exact(dest.ClimateColorMapIds, source.ClimateColorMapIds, source.ColorMapIdsCount);
        dest.SeasonColorMapIds = Exact(dest.SeasonColorMapIds, source.SeasonColorMapIds, source.ColorMapIdsCount);
        dest.RenderPassesAndExtraBits = Exact(dest.RenderPassesAndExtraBits, source.RenderPassesAndExtraBits,
            source.RenderPassCount);
        Counts(source, dest);
        dest.CustomFloats = Part<CustomMeshDataPartFloat, float>(dest.CustomFloats, source.CustomFloats);
        dest.CustomShorts = Part<CustomMeshDataPartShort, short>(dest.CustomShorts, source.CustomShorts);
        dest.CustomBytes = Part<CustomMeshDataPartByte, byte>(dest.CustomBytes, source.CustomBytes);
        dest.CustomInts = Part<CustomMeshDataPartInt, int>(dest.CustomInts, source.CustomInts);
        if (Counting.Hud) _ = Interlocked.Increment(ref _clones);
        return false;
    }

    // Only a mesh on its way back to the recycler keeps anything; every other dispose is the engine's
    internal static bool DisposeExtraData(MeshData __instance)
    {
        if (!Enabled || !NotNull(__instance) || !__instance.Recyclable) return true;
        var mesh = __instance;
        if (!Assert(mesh.XyzFacesCount >= 0) || !Assert(mesh.RenderPassCount >= 0)) return true;
        _kept = true;
        (mesh.NormalsCount, mesh.XyzFacesCount, mesh.TextureIndicesCount) = (0, 0, 0);
        (mesh.ColorMapIdsCount, mesh.RenderPassCount) = (0, 0);
        if (mesh.CustomFloats is { } floats) floats.Count = 0;
        if (mesh.CustomShorts is { } shorts) shorts.Count = 0;
        if (mesh.CustomBytes is { } bytes) bytes.Count = 0;
        if (mesh.CustomInts is { } ints) ints.Count = 0;
        return false;
    }

    // The counts CloneExtraData writes, exactly the ones it writes: Normals carries none, the two colour maps share theirs
    private static void Counts(MeshData source, MeshData dest)
    {
        if (!Assert(source.XyzFacesCount >= 0) || !Assert(source.RenderPassCount >= 0)) return;
        if (source.XyzFaces != null) dest.XyzFacesCount = source.XyzFacesCount;
        if (source.TextureIndices != null) dest.TextureIndicesCount = source.TextureIndicesCount;
        if (source.ClimateColorMapIds != null || source.SeasonColorMapIds != null)
            dest.ColorMapIdsCount = source.ColorMapIdsCount;
        if (source.RenderPassesAndExtraBits != null) dest.RenderPassCount = source.RenderPassCount;
    }

    // An array the engine sizes to the count: reused only at that exact length, so Length keeps meaning what it did
    private static T[]? Exact<T>(T[]? existing, T[]? source, int count) where T : unmanaged
    {
        if (source is null) return null;
        if (!Assert(count >= 0 && count <= source.Length)) return (T[])source.Clone();
        var target = existing is not null && existing.Length == count
            ? existing : GC.AllocateUninitializedArray<T>(count);
        Array.Copy(source, target, count);
        if (Counting.Hud && ReferenceEquals(target, existing)) Reuse(count);
        return target;
    }

    // Each engine Clone is new TPart() plus SetFrom; a part without values is left to exactly that
    private static TPart? Part<TPart, T>(TPart? dest, TPart? source)
        where TPart : CustomMeshDataPart<T>, new() where T : unmanaged
    {
        if (source is null) return null;
        TPart part;
        if (source.Values is null || !Assert(source.Count >= 0 && source.Count <= source.Values.Length))
        {
            part = new TPart();
            part.SetFrom(source);
        }
        else
        {
            part = Filled(dest ?? new TPart(), source);
        }

        // CustomMeshDataPartByte.Clone copies Conversion; the Short and Int clones leave their class default
        if (part is CustomMeshDataPartByte bytes && source is CustomMeshDataPartByte from)
            bytes.Conversion = from.Conversion;
        else if (part is CustomMeshDataPartShort shorts) shorts.Conversion = ShortConversion;
        else if (part is CustomMeshDataPartInt ints) ints.Conversion = IntConversion;
        return part;
    }

    // Values is addressed through Count and its length is only ever read as capacity, so the buffer may be larger than the source's
    private static TPart Filled<TPart, T>(TPart dest, CustomMeshDataPart<T> source)
        where TPart : CustomMeshDataPart<T> where T : unmanaged
    {
        if (!NotNull(source.Values) || !Assert(source.Count <= source.Values.Length)) return dest;
        var values = dest.Values is { } kept && kept.Length >= source.Count
            ? kept : GC.AllocateUninitializedArray<T>(Capacity(source.Count));
        var reuse = ReferenceEquals(values, dest.Values);
        Array.Copy(source.Values, values, source.Count);
        (dest.Values, dest.Count) = (values, source.Count);
        Allocation<T>.Custom(dest) = Allocation<T>.Custom(source);
        Allocation<T>.Size(dest) = Allocation<T>.Size(source);
        dest.InterleaveSizes = Exact(dest.InterleaveSizes, source.InterleaveSizes, source.InterleaveSizes?.Length ?? 0);
        dest.InterleaveOffsets = Exact(dest.InterleaveOffsets, source.InterleaveOffsets,
            source.InterleaveOffsets?.Length ?? 0);
        (dest.InterleaveStride, dest.Instanced, dest.StaticDraw, dest.BaseOffset) =
            (source.InterleaveStride, source.Instanced, source.StaticDraw, source.BaseOffset);
        if (Counting.Hud && reuse) Reuse(source.Values.Length);
        return dest;
    }

    // A fresh buffer is sized to what this mesh holds plus room to grow, not to the tesselator buffer the engine's SetFrom copies whole
    private static int Capacity(int count)
    {
        return Assert(count >= 0) ? Math.Max(4, count + count / 4) : 4;
    }

    // The fields SetFrom copies behind AllocationSize, the GL buffer size of the part
    private static class Allocation<T>
    {
        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "customAllocationSize")]
        public static extern ref bool Custom(CustomMeshDataPart<T> part);

        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "allocationSize")]
        public static extern ref int Size(CustomMeshDataPart<T> part);
    }
}
