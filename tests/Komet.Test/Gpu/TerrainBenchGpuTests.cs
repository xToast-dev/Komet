using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The GPU time of the terrain's culling and its draws from rows.comp's commands on TerrainWorld (radius 320, six pools), by
// OpenGL's timer: the far shadow map (4096, chunkshadowmap's alpha test) as the engine culls it and with the casters the view
// cannot see left out; the camera's view (2560x1366, four RGBA16F outputs, a fragment shader as heavy as chunkopaque's) as
// written and sorted near to far. Rounds alternate the variants; the medians are printed. Run it alone, as FrameBenchGpuTests.
// Dense foliage: the same screen in Vulkan, the engine's chunkopaque on 48 layers of waving leaves (the opaque pass not
// culled, as RenderOpaque draws leaves), on the engine's depth and on Komet's own: the opaque terrain's GPU time (the depth's
// copies in and back included) and fragment shader invocations. Dense foliage in a shadow map: the far map's size (4096), the
// engine's chunkshadowmap (its alpha test) on the same leaves in OcclusionCulling's counted draws, the map cleared by Vulkan
// (the scene on, as the shadow passes are Komet's only then), on the engine's depth and on Komet's own (taken over by the
// clear, written back at the pass's end): the pass's GPU time and fragment shader invocations. Faces facing away: the camera's
// view of the world sorted as the tessellator sorts (FaceSorting), drawn with back faces culled as RenderOpaque draws, whole,
// with the directions facing away left out and with their parts past the splits too: the culling's and the draw's GPU time, the
// draw's vertex shader invocations, primitives and primitives after clipping (ARB_pipeline_statistics_query).
[NonParallelizable]
[Explicit("a benchmark")]
public sealed class TerrainBenchGpuTests
{
    private const int Rounds = 40, Map = 4096, Width = 2560, Height = 1366;

    private static readonly (double Yaw, double Pitch, double[] Sun)[] Views =
        [(0.3, -0.15, [0.4, 0.8, 0.3]), (2.4, -0.5, [-0.7, 0.35, 0.2]), (4.2, 0.1, [0.1, 0.25, -0.9])];

    [Test]
    public void TheTerrainsDrawsOnTheGpu() => ShadowCastersGpuTests.Run(GpuRig.Gl, 320, world =>
    {
        var (shadow, opaque) = (TerrainWorld.Program(TerrainWorld.ShadowFragment), TerrainWorld.Program(TerrainWorld.OpaqueFragment));
        using var map = new DepthTarget(Map, Map);
        using var screen = new DepthTarget(Width, Height, 4);
        var query = GL.GenQuery();
        try
        {
            TestContext.Out.WriteLine($"{world.Triangles / 1e6:0.00} M triangles in {world.Locations.Count()} locations");
            foreach (var (yaw, pitch, sun) in Views)
            {
                var view = new TerrainView(yaw, pitch, sun);
                var mvp = TerrainView.Floats(view.ShadowMvp);
                var culler = ShadowCastersGpuTests.Culler(view);
                var times = new List<double>[4];
                for (var v = 0; v < times.Length; v++) times[v] = [];
                for (var round = 0; round < Rounds; round++)
                {
                    times[0].Add(Shadow(world, view, null, (shadow, map, mvp, query)));
                    times[1].Add(Shadow(world, view, view.View, (shadow, map, mvp, query)));
                    times[2].Add(Camera(world, view, culler, false, (opaque, screen, query)));
                    times[3].Add(Camera(world, view, culler, true, (opaque, screen, query)));
                }

                TestContext.Out.WriteLine(
                    $"yaw {yaw}: far map {Median(times[0]):0.000} ms as the engine culls it, {Median(times[1]):0.000} with the " +
                    $"casters out of view left out; the view {Median(times[2]):0.000} ms as written, {Median(times[3]):0.000} " +
                    "sorted near to far");
            }
        }
        finally
        {
            OcclusionCulling.SortAlways = false;
            GL.DeleteQuery(query);
            GL.DeleteProgram(shadow);
            GL.DeleteProgram(opaque);
        }
    });

    [Test]
    public void FacesFacingAwayLeftOut() => ShadowCastersGpuTests.Run(GpuRig.Gl, 320, world =>
    {
        var opaque = TerrainWorld.Program(TerrainWorld.OpaqueFragment);
        using var screen = new DepthTarget(Width, Height, 4);
        int[] queries = [GL.GenQuery(), GL.GenQuery(), GL.GenQuery(), GL.GenQuery()];
        try
        {
            TestContext.Out.WriteLine($"{world.Triangles / 1e6:0.00} M triangles in {world.Locations.Count()} locations");
            foreach (var (yaw, pitch, _) in Views)
            {
                var view = new TerrainView(yaw, pitch, [0.4, 0.8, 0.3]);
                var samples = FaceSplitsGpuTests.Variants.Select(_ => new List<double[]>()).ToArray();
                for (var round = 0; round < Rounds; round++)
                    for (var v = 0; v < samples.Length; v++)
                        samples[v].Add(Facing(world, view, FaceSplitsGpuTests.Variants[v], (opaque, screen, queries)));
                var lines = samples.Select(s => string.Create(CultureInfo.InvariantCulture,
                    $"{MedianOf(s, 0):0.000} ms, {MedianOf(s, 1) / 1e6:0.00} M vertex shader invocations, " +
                    $"{MedianOf(s, 2) / 1e6:0.00} M primitives, {MedianOf(s, 3) / 1e6:0.00} M after clipping")).ToArray();
                TestContext.Out.WriteLine($"yaw {yaw}: whole {lines[0]}; directions facing away left out {lines[1]}; " +
                                          $"their parts past the splits too {lines[2]}");
            }
        }
        finally
        {
            foreach (var query in queries) GL.DeleteQuery(query);
            GL.DeleteProgram(opaque);
        }
    }, sorted: true);

    private const int VertexShaderInvocations = 0x82F0, PrimitivesSubmitted = 0x82EF, ClippingOutputPrimitives = 0x82F7;

    // The culling's and the draw's GPU time, then the draw's statistics
    private static double[] Facing(TerrainWorld world, TerrainView view, (bool BackFaces, bool Splitting) how,
        (int Program, DepthTarget Screen, int[] Queries) with)
    {
        var culling = Timed(with.Queries[0], () => FaceSplitsGpuTests.Run(world, view, how));
        with.Screen.Begin();
        FaceSplitsGpuTests.BackCulled();
        int[] targets = [VertexShaderInvocations, PrimitivesSubmitted, ClippingOutputPrimitives];
        for (var q = 0; q < targets.Length; q++) GL.BeginQuery((QueryTarget)targets[q], with.Queries[q + 1]);
        var drawing = Timed(with.Queries[0],
            () => world.DrawCulled(with.Program, view.CameraMvp, view.Camera, false, false, how.BackFaces ? 4 : 1));
        foreach (var target in targets) GL.EndQuery((QueryTarget)target);
        GL.Disable(EnableCap.CullFace);
        var result = new double[4];
        result[0] = culling + drawing;
        for (var q = 0; q < targets.Length; q++)
        {
            GL.GetQueryObject(with.Queries[q + 1], GetQueryObjectParam.QueryResult, out long count);
            result[q + 1] = count;
        }

        return result;
    }

    private static double MedianOf(List<double[]> samples, int at) => Median([.. samples.Select(s => s[at])]);

    [Test]
    public void DenseFoliageOnKometsDepth()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var foliage = Foliage.Layers(Layers, Across, 11, false);
        var quads = foliage.Faces.Length;
        (int, int)[] draws = [.. Enumerable.Range(0, Pools).Select(p => (p * quads / Pools, quads / Pools))];
        using var scene = new TerrainScene(rig.Device!, true, "chunkopaque", foliage) { Counts = rig.Backend };
        scene.Set("alphaTest", [0.25f]);
        scene.Set("frameSize", [Width, Height]);
        var screen = OwnDepthParityGpuTests.Framebuffer(Width, Height);
        var measuring = VulkanFrame.Measuring;
        (VulkanFrame.Measuring, Builds.Waited) = (true, true);
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            using var capture = TerrainRenderer.Create(rig.Device!, quads, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            var section = VulkanFrame.SectionOf("bench opaque terrain");
            capture.Frame.Counted = section;
            List<double>[] ms = [[], []], fragments = [[], []];
            for (var round = 0; round < 2 * FoliageRounds; round++)
                for (var f = 0; f < RoundFrames; f++)
                {
                    var on = round % 2 == 1;
                    Frame(capture, scene, (screen, draws), (round * RoundFrames + f, on));
                    if (f < RoundFrames / 2) continue; // what LastMs and LastStatistic read is this round's frames
                    ms[on ? 1 : 0].Add(capture.Frame.LastMs(section));
                    fragments[on ? 1 : 0].Add(capture.Frame.LastStatistic(1));
                }

            Assert.That(capture.Left, Is.Zero, string.Join("; ", capture.Reasons));
            Assert.That(capture.OwnCopies, Is.EqualTo(2 * FoliageRounds * RoundFrames), "copied in and back each frame on");
            var pixels = (double)Width * Height;
            TestContext.Out.WriteLine($"{quads} leaf quads in {Layers} layers, {Pools} pool draws, {Width}x{Height}: opaque " +
                                      "terrain " +
                                      $"on the engine's depth {Median(ms[0]):0.000} ms GPU, " +
                                      $"{Median(fragments[0]) / 1e6:0.0} M fragment shader invocations " +
                                      $"({Median(fragments[0]) / pixels:0.0} a pixel); on Komet's {Median(ms[1]):0.000} ms, " +
                                      $"{Median(fragments[1]) / 1e6:0.0} M ({Median(fragments[1]) / pixels:0.0} a pixel)");
        }
        finally
        {
            GlTap.Untap();
            VulkanFrame.Measuring = measuring;
            Parity.Delete(screen);
        }
    }

    private const int Layers = 48, Across = 10, Pools = 8, FoliageRounds = 6, RoundFrames = 40;

    [Test]
    public void DenseFoliageShadowOnKometsDepth()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var foliage = Foliage.Layers(Layers, Across, 11, false);
        var quads = foliage.Faces.Length;
        (int, int)[] draws = [.. Enumerable.Range(0, Pools).Select(p => (p * quads / Pools, quads / Pools))];
        using var scene = new TerrainScene(rig.Device!, true, "chunkshadowmap", foliage) { Counts = rig.Backend, Counted = true };
        var map = ShadowOwnDepthGpuTests.Map(Map);
        var measuring = VulkanFrame.Measuring;
        (VulkanFrame.Measuring, Builds.Waited) = (true, true);
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        try
        {
            using var capture = TerrainRenderer.Create(rig.Device!, quads, null, out var why);
            Assert.That(capture, Is.Not.Null, why);
            _ = capture!.Watch(scene.Name, scene.Program, scene.Ported);
            using var drawer = SceneParityGpuTests.Hooked(capture);
            var section = VulkanFrame.SectionOf("bench shadow map");
            capture.Frame.Counted = section;
            List<double>[] ms = [[], []], fragments = [[], []];
            for (var round = 0; round < 2 * FoliageRounds; round++)
                for (var f = 0; f < RoundFrames; f++)
                {
                    var on = round % 2 == 1;
                    ShadowFrame(capture, scene, (map, draws), (round * RoundFrames + f, on));
                    if (f < RoundFrames / 2) continue;
                    ms[on ? 1 : 0].Add(capture.Frame.LastMs(section));
                    fragments[on ? 1 : 0].Add(capture.Frame.LastStatistic(1));
                }

            Assert.That(capture.Left + drawer.Left, Is.Zero, string.Join("; ", capture.Reasons.Concat(drawer.Reasons)));
            Assert.That((capture.OwnClears, capture.OwnCopies), Is.EqualTo(((long)FoliageRounds * RoundFrames,
                (long)FoliageRounds * RoundFrames)), "taken over by the clear and written back each frame on");
            var texels = (double)Map * Map;
            TestContext.Out.WriteLine($"{quads} leaf quads in {Layers} layers, {Pools} counted draws, a {Map}x{Map} map: " +
                                      $"on the engine's depth {Median(ms[0]):0.000} ms GPU, " +
                                      $"{Median(fragments[0]) / 1e6:0.0} M fragment shader invocations " +
                                      $"({Median(fragments[0]) / texels:0.0} a texel); on Komet's {Median(ms[1]):0.000} ms " +
                                      $"(the copy back included), {Median(fragments[1]) / 1e6:0.0} M " +
                                      $"({Median(fragments[1]) / texels:0.0} a texel)");
            capture.Release(map);
        }
        finally
        {
            GlTap.Untap();
            VulkanFrame.Measuring = measuring;
            Parity.Delete(map);
        }
    }

    // The pass's clear is in its time, as is the map's copy back on Komet's depth
    private static void ShadowFrame(TerrainRenderer capture, TerrainScene scene, (FrameBufferRef Map, (int, int)[] Draws) into,
        (int Number, bool On) frame)
    {
        Assert.That(capture.Start([into.Map], [0], out var why), Is.True, why);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, into.Map.FboId);
        GL.Viewport(0, 0, Map, Map);
        ShadowOwnDepthGpuTests.Depth();
        OwnDepthParityGpuTests.Wind(scene, frame.Number);
        capture.Frame.Section(VulkanFrame.SectionOf("bench shadow map"));
        capture.Owning = frame.On;
        var one = 1f;
        GL.ClearBuffer(ClearBuffer.Depth, 0, ref one);
        Assert.That(scene.DrawCall(capture, into.Draws, false, kept: true), Is.True, string.Join("; ", capture.Reasons));
        capture.Disown();
        capture.Frame.Section(VulkanFrame.SectionOf("bench rest"));
        capture.Frame.Close("the bench's frame");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static void Frame(TerrainRenderer capture, TerrainScene scene, (FrameBufferRef Screen, (int, int)[] Draws) into,
        (int Number, bool On) frame)
    {
        Assert.That(capture.Start([into.Screen], [0], out var why), Is.True, why);
        Parity.Clear(into.Screen, [0.5f, 0.6f, 0.7f, 1f]);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, into.Screen.FboId);
        GL.Viewport(0, 0, Width, Height);
        OwnDepthParityGpuTests.Engine(false, true);
        OwnDepthParityGpuTests.Wind(scene, frame.Number);
        capture.Frame.Section(VulkanFrame.SectionOf("bench opaque terrain"));
        Assert.That(scene.DrawCall(capture, into.Draws, frame.On), Is.True, string.Join("; ", capture.Reasons));
        capture.Frame.Section(VulkanFrame.SectionOf("bench rest"));
        capture.Frame.Close("the bench's frame");
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private static double Shadow(TerrainWorld world, TerrainView view, FrustumCulling? casters,
        (int Program, DepthTarget Map, float[] Mvp, int Query) with)
    {
        var culling = Timed(with.Query, () => Assert.That(OcclusionCulling.RunShadow(world.Pools, view.ShadowCuller,
            view.Camera, EnumFrustumCullMode.CullInstantShadowPassFar, casters), Is.True));
        with.Map.Begin();
        return culling + Timed(with.Query, () => world.DrawCulled(with.Program, with.Mvp, view.Camera, true, false));
    }

    private static double Camera(TerrainWorld world, TerrainView view, FrustumCulling culler, bool sorted,
        (int Program, DepthTarget Screen, int Query) with)
    {
        OcclusionCulling.SortAlways = sorted;
        var culling = Timed(with.Query,
            () => Assert.That(OcclusionCulling.RunRows(world.Pools, culler, view.Camera, view.CameraMvp), Is.True));
        with.Screen.Begin();
        return culling + Timed(with.Query, () => world.DrawCulled(with.Program, view.CameraMvp, view.Camera, false, sorted));
    }

    private static double Timed(int query, Action draw)
    {
        GL.Finish();
        GL.BeginQuery(QueryTarget.TimeElapsed, query);
        draw();
        GL.EndQuery(QueryTarget.TimeElapsed);
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long ns);
        return ns / 1e6;
    }

    private static double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
}
