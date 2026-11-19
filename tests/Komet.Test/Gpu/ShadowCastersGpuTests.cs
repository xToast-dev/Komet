using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

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

    // Every texel a pixel in view samples (its filter's too) is the engine's in the far map drawn from the casters kept; and the
    // camera's draws sorted near to far leave every depth as it was
    [Test]
    [Category("Slow")]
    public void TheShadowsInViewAndTheSortedDepthsAreTheEngines() =>
        TerrainWorld.Run(GpuRig.Gl, 160, world =>
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
    private static (float[] Unsorted, float[] Sorted) Viewed(TerrainWorld world, TerrainView view, int program,
        DepthTarget target)
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

    private static float[] Shadow(TerrainWorld world, TerrainView view, FrustumCulling? casters, int program,
        DepthTarget map,
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

    private static bool Same(float a, float b) =>
        BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
}
