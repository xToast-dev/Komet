using Komet.Rendering;

namespace Komet.Test.Gpu;

// OcclusionCulling's private GPU state as the GPU tests read it back: its backend, the counter ring, the command buffers and
// the frame counter, plus the last frame's counters and commands and a pool's locations
internal static class CullingState
{
    public const int StatUints = 8, CommandUints = 5;
    private const string Culling = "Komet.Rendering.OcclusionCulling, Komet.Rendering";

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_Gpu")]
    public static extern IGpuBackend Gpu([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CounterBuffers")]
    public static extern ref int[] CounterBuffers([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_commands")]
    public static extern ref int CommandBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_sorted")]
    public static extern ref int SortedBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_frame")]
    public static extern ref int FrameCount([UnsafeAccessorType(Culling)] object? culling);

    // The last frame's counter buffer: the stats, then draws counts of MaxDraws each (camera, late, shadow)
    public static uint[] Counters(int draws)
    {
        var counters = new uint[StatUints + draws * OcclusionCulling.MaxDraws];
        Counters(counters);
        return counters;
    }

    public static void Counters(Span<uint> counters)
    {
        var buffers = CounterBuffers(null);
        Gpu(null).Read(buffers[(FrameCount(null) - 1) % buffers.Length], 0, counters);
    }

    // count commands from the command region on (count, instances, first index, base vertex, base instance each), as written
    // or as sorted near to far
    public static uint[] Commands(int region, int count, bool sorted = false)
    {
        var commands = new uint[CommandUints * Math.Max(count, 1)];
        Gpu(null).Read(sorted ? SortedBuffer(null) : CommandBuffer(null), CommandUints * region, commands);
        return commands;
    }

    // The frame just run: its counters, and its first draw's commands of both passes (or the shadow first draw's, which
    // OcclusionCulling places after both camera passes')
    public static void Frame(Span<uint> counters, Span<uint> first, Span<uint> late, bool shadow = false)
    {
        if (!OcclusionCulling.Supported) return;
        var (gpu, commands) = (Gpu(null), CommandBuffer(null));
        Counters(counters);
        gpu.Read(commands, (shadow ? 2 * OcclusionCulling.MaxRanges : 0) * CommandUints, first);
        gpu.Read(commands, OcclusionCulling.MaxRanges * CommandUints, late);
    }

    // The pool's locations, in the order it lists them
    public static List<ModelDataPoolLocation> Listed(MeshDataPool pool) =>
        (List<ModelDataPoolLocation>)AccessTools.Field(typeof(MeshDataPool), "poolLocations").GetValue(pool)!;
}
