using Komet.Rendering;
using OpenTK.Graphics.OpenGL;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Test.Gpu;

// The shadow passes leave out casters whose shadow cannot reach the camera's view, and the camera's sorted draws keep every
// pixel: on a world of hills and tree crowns, against the engine's culling of the same passes.
[NonParallelizable]
public sealed class ShadowCastersGpuTests
{
    private const int Map = 2048, Width = 960, Height = 512, Kernel = 2;

    // yaw, pitch (radians), the sun; whether some caster is left out (looking down, the view reaches every caster below the sun)
    private static readonly (double Yaw, double Pitch, double[] Sun, bool Culls)[] Views =
    [
        (0.3, -0.15, [0.4, 0.8, 0.3], true), (2.4, -0.5, [-0.7, 0.35, 0.2], true), (4.2, 0.1, [0.1, 0.25, -0.9], true),
        (1.2, -1.45, [0.05, 0.99, 0.1], false)
    ];

    // Whatever the engine keeps for the far pass whose box swept along the light meets the view (exactly, by separating axes) is
    // drawn; nothing the engine leaves out is
    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void WhatCastsIntoTheViewIsKept(string backend) => TerrainWorld.Run(backend, 160, world =>
    {
        foreach (var (yaw, pitch, sun, culls) in Views)
        {
            var view = new TerrainView(yaw, pitch, sun);
            var locations = world.Locations.ToList();
            var engine = locations.Where(l => l.IsVisible(EnumFrustumCullMode.CullInstantShadowPassFar, view.ShadowCuller))
                .ToHashSet();
            var light = Light(view.ShadowCuller);
            var corners = Corners(view);
            var needed = engine.Where(l => Meets(l.FrustumCullSphere, light, corners, Planes(view.View))).ToHashSet();
            Assert.That(OcclusionCulling.RunShadow(world.Pools, view.ShadowCuller, view.Camera,
                EnumFrustumCullMode.CullInstantShadowPassFar, view.View), Is.True);
            var drawn = Drawn(world, locations);
            Assert.Multiple(() =>
            {
                Assert.That(needed.Except(drawn), Is.Empty, $"yaw {yaw}: every caster of the view drawn");
                Assert.That(drawn.Except(engine), Is.Empty, $"yaw {yaw}: nothing the engine leaves out");
                Assert.That(drawn, Has.Count.LessThan(culls ? engine.Count * 4 / 5 : int.MaxValue),
                    $"yaw {yaw}: of {engine.Count}, {needed.Count} needed");
            });
        }
    });

    // Every texel a pixel in view samples (its filter's too) is the engine's in the far map drawn from the casters kept; and the
    // camera's draws sorted near to far leave every depth as it was
    [Test]
    [Category("Slow")]
    public void TheShadowsInViewAndTheSortedDepthsAreTheEngines() => TerrainWorld.Run(GpuRig.Gl, 160, world =>
    {
        var shadow = TerrainWorld.Program(TerrainWorld.ShadowFragment);
        using var map = new DepthTarget(Map, Map);
        using var camera = new DepthTarget(Width, Height);
        try
        {
            foreach (var (yaw, pitch, sun, culls) in Views)
            {
                var view = new TerrainView(yaw, pitch, sun);
                var mvp = TerrainView.Floats(view.ShadowMvp);
                var all = Shadow(world, view, null, shadow, map, mvp);
                var culled = Shadow(world, view, view.View, shadow, map, mvp);
                camera.Begin();
                world.DrawAll(shadow, view.CameraMvp, view.Camera);
                var depth = camera.Read();
                var (sampled, differing) = Sampled(view, depth, all, culled);
                var (unsorted, inOrder) = Viewed(world, view, shadow, camera);
                Assert.Multiple(() =>
                {
                    Assert.That(differing, Is.Zero, $"yaw {yaw}: of {sampled} texels sampled");
                    Assert.That(sampled, Is.GreaterThan(10_000), $"yaw {yaw}: texels sampled");
                    Assert.That(all.Zip(culled).Any(p => !Same(p.First, p.Second)), Is.EqualTo(culls),
                        $"yaw {yaw}: casters left out");
                    Assert.That(inOrder, Is.EqualTo(unsorted), $"yaw {yaw}: the camera's sorted depth");
                });
            }
        }
        finally
        {
            OcclusionCulling.SortAlways = false;
            GL.DeleteProgram(shadow);
        }
    });

    // The camera's terrain culled from rows, drawn as written and sorted: its depth both ways
    private static (float[] Unsorted, float[] Sorted) Viewed(TerrainWorld world, TerrainView view, int program, DepthTarget target)
    {
        var culler = TerrainWorld.Culler(view);
        var results = new float[2][];
        for (var i = 0; i < 2; i++)
        {
            OcclusionCulling.SortAlways = i == 1;
            Assert.That(OcclusionCulling.RunRows(world.Pools, culler, view.Camera, view.CameraMvp), Is.True);
            target.Begin();
            world.DrawCulled(program, view.CameraMvp, view.Camera, false, i == 1);
            results[i] = target.Read();
        }

        OcclusionCulling.SortAlways = false;
        return (results[0], results[1]);
    }

    private static float[] Shadow(TerrainWorld world, TerrainView view, FrustumCulling? casters, int program, DepthTarget map,
        float[] mvp)
    {
        Assert.That(OcclusionCulling.RunShadow(world.Pools, view.ShadowCuller, view.Camera,
            EnumFrustumCullMode.CullInstantShadowPassFar, casters), Is.True);
        map.Begin();
        world.DrawCulled(program, mvp, view.Camera, true, false);
        return map.Read();
    }

    // Each pixel the camera drew, back into the world and into the far map: the texels its filter reads, compared
    private static (int Sampled, int Differing) Sampled(TerrainView view, float[] depth, float[] all, float[] culled)
    {
        var (sampled, differing) = (0, 0);
        for (var at = 0; at < Width * Height; at++)
        {
            var d = depth[at];
            if (d >= 1) continue;
            var (x, y) = (at % Width, at / Width);
            double[] ndc = [(x + 0.5) / Width * 2 - 1, (y + 0.5) / Height * 2 - 1, d * 2 - 1, 1];
            var world = Mat4d.MulWithVec4(view.Inverse, ndc);
            double[] point = [world[0] / world[3], world[1] / world[3], world[2] / world[3], 1];
            var clip = Mat4d.MulWithVec4(view.ShadowMvp, point);
            var (u, v) = ((int)((clip[0] * 0.5 + 0.5) * Map), (int)((clip[1] * 0.5 + 0.5) * Map));
            for (var k = 0; k < (2 * Kernel + 1) * (2 * Kernel + 1); k++)
            {
                var (tu, tv) = (u + k % (2 * Kernel + 1) - Kernel, v + k / (2 * Kernel + 1) - Kernel);
                if (tu < 0 || tv < 0 || tu >= Map || tv >= Map) continue;
                sampled++;
                if (!Same(all[tv * Map + tu], culled[tv * Map + tu])) differing++;
            }
        }

        return (sampled, differing);
    }

    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

    // The draws' commands as rows.comp wrote them for the shadow pass: their locations
    private static HashSet<ModelDataPoolLocation> Drawn(TerrainWorld world, List<ModelDataPoolLocation> locations)
    {
        var counters = CullingState.Counters(3);
        var (drawn, region, at) = (new HashSet<ModelDataPoolLocation>(), 2 * OcclusionCulling.MaxRanges, 0);
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var listed = CullingState.Listed(world.Pools[p]);
            var count = (int)counters[CullingState.StatUints + 2 * OcclusionCulling.MaxDraws + p];
            var commands = CullingState.Commands(region, count);
            for (var c = 0; c < count; c++) _ = drawn.Add(listed.First(l => l.IndicesStart == (int)commands[5 * c + 2]));
            (region, at) = (region + listed.Count, at + listed.Count);
        }

        Assert.That(at, Is.EqualTo(locations.Count));
        return drawn;
    }

    // The light's direction: a shadow pass's near plane faces along it
    private static double[] Light(FrustumCulling culler)
    {
        var near = Planes(culler)[0];
        return [near.normalX, near.normalY, near.normalZ];
    }

    // The view's eight corners in the world
    private static double[][] Corners(TerrainView view)
    {
        var inverse = Mat4d.Invert(Mat4d.Create(), Mat4d.Mul(Mat4d.Create(), view.Projection, view.World));
        return [.. from x in new[] { -1, 1 } from y in new[] { -1, 1 } from z in new[] { -1, 1 }
            select Mat4d.MulWithVec4(inverse, [x, y, z, 1]) into c select new[] { c[0] / c[3], c[1] / c[3], c[2] / c[3] }];
    }

    // Separating axes of two convex solids: the view, and the box swept far along the light
    private static bool Meets(Sphere sphere, double[] light, double[][] view, Plane[] planes)
    {
        double[] e = [sphere.radius / 1.7320508f, sphere.radiusY / 1.7320508f, sphere.radiusZ / 1.7320508f];
        var box = (from x in new[] { -1, 1 } from y in new[] { -1, 1 } from z in new[] { -1, 1 }
            select new[] { sphere.x + x * e[0], sphere.y + y * e[1], sphere.z + z * e[2] }).ToList();
        var swept = box.Concat(box.Select(b => new[] { b[0] + 4000 * light[0], b[1] + 4000 * light[1], b[2] + 4000 * light[2] }))
            .ToArray();
        double[][] axes = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
        var edges = new List<double[]>();
        for (var k = 0; k < 64; k++)
            if (k / 8 > k % 8 && BitCount(k / 8 ^ k % 8) == 1) edges.Add(Minus(view[k / 8], view[k % 8]));
        var candidates = planes.Select(p => new[] { p.normalX, p.normalY, p.normalZ }).Concat(axes)
            .Concat(axes.Select(a => Cross(light, a)))
            .Concat(from f in edges from b in axes.Append(light) select Cross(f, b));
        return candidates.Where(a => a.Sum(v => v * v) > 1e-12).All(a => Overlap(a, view, swept));
    }

    private static int BitCount(int v) => System.Numerics.BitOperations.PopCount((uint)v);

    private static double[] Minus(double[] a, double[] b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];

    private static double[] Cross(double[] a, double[] b) =>
        [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];

    private static bool Overlap(double[] axis, double[][] a, double[][] b)
    {
        var (pa, pb) = (a.Select(p => Dot(p, axis)).ToArray(), b.Select(p => Dot(p, axis)).ToArray());
        return pa.Max() >= pb.Min() && pb.Max() >= pa.Min();
    }

    private static double Dot(double[] p, double[] a) => p[0] * a[0] + p[1] * a[1] + p[2] * a[2];

    private static Plane[] Planes(FrustumCulling culler) =>
        (Plane[])AccessTools.Field(typeof(FrustumCulling), "frustum").GetValue(culler)!;
}
