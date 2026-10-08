using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Komet.Gpu;

namespace Komet.Rendering;

// Counters live in one buffer per ring frame, copied on the GPU into a readable one and read once per HUD interval from the oldest
// (as RenderCost reads timestamps): the read waits for nothing, as it was last written Ring - 1 frames ago.
internal static partial class Occlusion
{
    private static readonly DepthPyramid Depth = new();

    private const int Ring = 4, FrameUints = 16, CounterUints = 20, Statistics = 4;
    private const int Group = 64, ParamsBinding = 14, RangesBinding = 4, CountersBinding = 5, VerdictsBinding = 6;
    private const int TotalUints = 4;

    // ARB_pipeline_statistics_query: vertex and fragment shader invocations, clipping input and output primitives
    private static readonly QueryTarget[] StatisticTargets =
        [(QueryTarget)0x82F0, (QueryTarget)0x82F4, (QueryTarget)0x82F6, (QueryTarget)0x82F7];

    private static readonly float[] ViewProjection = new float[16], Previous = new float[16];
    private static readonly float[] Projection = new float[16];
    private static readonly int[] Queries = new int[Ring * Statistics];
    private static readonly uint[] Counts = new uint[CounterUints];

    private static readonly int[] CounterBuffers = new int[Ring], ReadBuffers = new int[Ring];

    // Each buffer's totals as last read; summed, the totals over every frame (each holds every Ring-th)
    private static readonly long[] SlotTotals = new long[Ring * TotalUints];
    private static readonly long[] Pipeline = new long[Statistics];
    private static int _test, _ranges, _verdicts, _params, _frame;
    private static int _depthWidth, _depthHeight;
    private static bool _detected, _supported, _statistics, _pyramidReady, _counting;
    private static double _px, _py, _pz;

    // Counters of the oldest complete frame, eight per test: ranges, triangles, hidden ranges, hidden triangles, and ranges left
    // visible through the near plane, off screen, in front of the pyramid, for want of a level; first against the previous
    // frame's pyramid (Before), then this frame's
    public const int TrianglesBefore = 1, HiddenBefore = 3, NearBefore = 4, OffscreenBefore = 5, InFrontBefore = 6;
    public const int Ranges = 8, Triangles = 9, HiddenAfter = 11, PerTest = 8;

    // Totals since the measurement began: ranges compared, those previous-frame culling would show late, their triangles, frames
    public const int Compared = 0, Late = 1, LateTriangles = 2, ComparedFrames = 3;

    // The same frame's pipeline statistics, in StatisticTargets' order
    public const int Vertices = 0, Fragments = 1, Primitives = 2, Rasterized = 3;

    private static IGpuBackend? _gpu;

    // The backend the buffers and programs were made with: Vulkan's in its frame while it draws the opaque terrain, so OpenGL
    // dispatches nothing then; OpenGL's otherwise. The pipeline statistics are OpenGL queries, counted on OpenGL only.
    private static IGpuBackend Gpu => _gpu ?? GpuBackends.Measuring;

    public static bool Detected => _detected;
    public static bool Supported => _supported;
    public static double Pixels =>
        Vulkan.VulkanRenderer.TimesTerrain ? Vulkan.VulkanRenderer.TerrainPixels : _depthWidth * _depthHeight;

    // NaN until the first read back
    public static double Count(int at) =>
        _counting && Index(at, FrameUints) && Assert(Counts.Length == CounterUints) ? Counts[at] : double.NaN;

    // A total over every frame so far as read back; 0 before the first read (the HUD's rates take differences)
    public static double Total(int at)
    {
        if (!_counting || !Index(at, TotalUints)) return 0;
        long sum = 0;
        for (var slot = 0; slot < Ring; slot++) sum += SlotTotals[slot * TotalUints + at];
        return Assert(sum >= 0) ? sum : double.NaN;
    }

    public static double Share(int hidden, int of)
    {
        var (part, whole) = (Count(hidden), Count(of));
        return Assert(double.IsNaN(part) || part >= 0) && whole > 0 ? 100 * part / whole : double.NaN;
    }

    // NaN without the extension or before the first result; Vulkan's own queries while it draws the opaque terrain
    public static double Statistic(int at)
    {
        if (Vulkan.VulkanRenderer.TimesTerrain) return Vulkan.VulkanRenderer.TerrainStatistic(at);
        return _statistics && Index(at, Statistics) && Assert(Pipeline[at] >= -1) && Pipeline[at] >= 0
            ? Pipeline[at]
            : double.NaN;
    }

    // ClientMain refills one array for both matrices on every read, so the projection is copied before the model view is read
    internal static bool Capture(IRenderAPI render, float[] into)
    {
        if (!NotNull(render) || !Assert(into.Length == 16) ||
            render.CurrentProjectionMatrix is not { Length: 16 } projection) return false;
        projection.CopyTo(Projection, 0);
        if (render.CurrentModelviewMatrix is not { Length: 16 } view) return false;
        _ = Mat4f.Mul(into, Projection, view);
        return Finite(into[15]) && Finite(into[0]);
    }

    private static bool Ready()
    {
        if (_detected && !ReferenceEquals(_gpu, GpuBackends.Measuring)) Release(); // Vulkan took the frame or gave it back
        if (_detected) return _supported;
        (_detected, _gpu) = (true, GpuBackends.Measuring);
        _supported = Gpu.Supported() && NotNull(_logger);
        if (!_supported || _logger is null) return false;
        _test = Gpu.Program(GpuShaders.Occlusion, _logger);
        (_ranges, _verdicts, _params) = (Gpu.Buffer(), Gpu.Buffer(), Gpu.Buffer());
        Gpu.Upload<uint>(_verdicts, [], 4 * MaxRanges);
        for (var slot = 0; slot < Ring; slot++)
        {
            CounterBuffers[slot] = Gpu.Buffer();
            Gpu.Upload<uint>(CounterBuffers[slot], [], 4 * CounterUints);
            Gpu.Zero(CounterBuffers[slot], 0, CounterUints);
            ReadBuffers[slot] = Gpu.Buffer();
            Gpu.Readable(ReadBuffers[slot], 4 * CounterUints);
        }

        _statistics = Gpu is GlBackend && GpuStats.Offered("GL_ARB_pipeline_statistics_query");
        if (_statistics) GL.GenQueries(Queries.Length, Queries);
        _supported = Depth.Ready(_logger, Gpu) && _test != 0;
        return Assert(CounterBuffers[Ring - 1] != 0) && _supported;
    }

    private static void BeginStatistics()
    {
        if (!Ready() || !_statistics || !Assert(Queries[0] != 0)) return;
        var slot = _frame % Ring;
        if (!Index(slot * Statistics + Statistics - 1, Queries.Length)) return;
        for (var i = 0; i < Statistics; i++) GL.BeginQuery(StatisticTargets[i], Queries[slot * Statistics + i]);
    }

    private static void EndStatistics()
    {
        if (!_supported || !_statistics || !Assert(StatisticTargets.Length == Statistics) ||
            !Assert(Queries[0] != 0)) return;
        for (var i = 0; i < Statistics; i++) GL.EndQuery(StatisticTargets[i]);
    }

    private static void Frame()
    {
        var depth = _api?.Render.FrameBuffers is { Count: > 0 } buffers ? buffers[0] : null;
        if (!NotNull(depth) || !Assert(_gathered <= MaxRanges)) return;
        var texture = Ready() ? Gpu.Engine(depth.DepthTextureId) : 0; // Vulkan: the frame's image of it
        if (texture > 0)
            Run(texture, depth.Width, depth.Height, _camera ? _gathered : 0, ViewProjection, (_x, _y, _z));
        _gathered = 0;
    }

    // The engine-free part of Frame (GPU tests)
    internal static void Run(int depth, int width, int height, int ranges, float[] viewProjection,
        (double X, double Y, double Z) camera)
    {
        if (!Ready() || !Assert(depth > 0 && width > 0 && height > 0) ||
            !Assert(ranges is >= 0 and <= MaxRanges)) return;
        var counters = CounterBuffers[_frame % Ring];
        Gpu.Zero(counters, 0, FrameUints); // the totals behind them stay
        Gpu.Upload<Range>(_ranges, Gathered.AsSpan(0, ranges));
        Gpu.Storage(RangesBinding, _ranges);
        Gpu.Storage(CountersBinding, counters);
        Gpu.Storage(VerdictsBinding, _verdicts);
        var (shift, compared) = ((camera.X - _px, camera.Y - _py, camera.Z - _pz), _pyramidReady && ranges > 0);
        if (compared) Test(Previous, shift, (false, false), 0, ranges);
        Gpu.Barrier(GpuBarrier.Storage | GpuBarrier.Images | GpuBarrier.Textures);
        (_depthWidth, _depthHeight) = (width, height);
        _ = Depth.Build(depth, width, height);
        Test(viewProjection, (0, 0, 0), (true, compared), PerTest, ranges);
        Gpu.Barrier(GpuBarrier.Copy);
        Gpu.Copy(counters, ReadBuffers[_frame % Ring], 0, CounterUints);
        viewProjection.CopyTo(Previous, 0);
        (_px, _py, _pz, _pyramidReady) = (camera.X, camera.Y, camera.Z, ranges > 0);
        Gpu.Texture(DepthPyramid.Unit, 0);
        _frame++;
    }

    // mode: Current = the pyramid is this frame's; Compared = the previous frame's test left verdicts for these ranges
    private static void Test(float[] matrix, (double X, double Y, double Z) shift, (bool Current, bool Compared) mode,
        int counter, int ranges)
    {
        if (!Assert(matrix.Length == 16) || !Assert(counter >= 0) || !Assert(Depth.Texture != 0) || ranges == 0) return;
        Span<float> p = stackalloc float[ShaderParams.TestFloats];
        var flags = (mode.Current ? 1 : 0, counter, mode.Compared ? 1 : 0, 0);
        ShaderParams.Test(p, matrix, shift, Depth, (0, ranges), flags);
        Gpu.Upload<float>(_params, p);
        Gpu.Uniforms(ParamsBinding, _params);
        Gpu.Texture(DepthPyramid.Unit, Depth.Texture);
        Gpu.Dispatch(_test, (ranges + Group - 1) / Group);
    }

    // Once per HUD interval: the ring's oldest frame
    public static void Sample()
    {
        if (Enabled) ReadOldest();
    }

    internal static void ReadOldest()
    {
        if (!_supported || _frame < Ring || !Assert(Ring > 1)) return;
        var slot = (_frame + 1) % Ring;
        if (!Index(slot * Statistics + Statistics - 1, Queries.Length)) return;
        Gpu.Read(ReadBuffers[slot], 0, Counts);
        for (var i = 0; i < TotalUints; i++) SlotTotals[slot * TotalUints + i] = Counts[FrameUints + i];
        _counting = true;
        if (!_statistics) return;
        GL.GetQueryObject(Queries[slot * Statistics + Statistics - 1], GetQueryObjectParam.QueryResultAvailable,
            out int ready);
        if (!Assert(ready is 0 or 1) || ready == 0) return;
        for (var i = 0; i < Statistics; i++)
            GL.GetQueryObject(Queries[slot * Statistics + i], GetQueryObjectParam.QueryResult, out Pipeline[i]);
    }

    private static void Release()
    {
        Depth.Release();
        Gpu.DeleteProgram(ref _test);
        Gpu.DeleteBuffer(ref _ranges);
        Gpu.DeleteBuffer(ref _verdicts);
        for (var slot = 0; slot < Ring; slot++)
        {
            Gpu.DeleteBuffer(ref CounterBuffers[slot]);
            Gpu.DeleteBuffer(ref ReadBuffers[slot]);
        }

        Array.Clear(SlotTotals);
        Gpu.DeleteBuffer(ref _params);
        if (Queries[0] != 0) GL.DeleteQueries(Queries.Length, Queries);
        Array.Clear(Queries);
        Array.Clear(Counts);
        Array.Fill(Pipeline, -1);
        (_detected, _supported, _statistics, _pyramidReady, _counting, _frame) =
            (false, false, false, false, false, 0);
        (_depthWidth, _depthHeight, _gpu) = (0, 0, null);
        _ = Assert(Depth.Texture == 0) && Assert(_test == 0);
    }
}
