using Komet.Rendering;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The camera's draws sorted near to far: the same commands as rows.comp wrote, each draw's in the order of their boxes' distance,
// and only where the order changes nothing but ties
[NonParallelizable]
public sealed class SortedDrawsGpuTests
{
    private static readonly bool[] Expected = [true, false, false, false, true, false];

    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void TheSortedCommandsAreTheWrittenOnesNearestFirst(string backend) => TerrainWorld.Run(backend, 160, world =>
    {
        var view = new TerrainView(0.3, -0.15, [0.4, 0.8, 0.3]);
        OcclusionCulling.SortAlways = true;
        try
        {
            Assert.That(OcclusionCulling.RunRows(world.Pools, TerrainWorld.Culler(view), view.Camera, view.CameraMvp),
                Is.True);
        }
        finally
        {
            OcclusionCulling.SortAlways = false;
        }

        var counters = CullingState.Counters(1);
        var (region, reordered) = (0, 0);
        for (var p = 0; p < world.Pools.Count; p++)
        {
            var listed = CullingState.Listed(world.Pools[p]);
            var count = (int)counters[CullingState.StatUints + p];
            var (written, sorted) = (CullingState.Commands(region, count), CullingState.Commands(region, count, sorted: true));
            var buckets = Enumerable.Range(0, count)
                .Select(c => Bucket(listed.First(l => l.IndicesStart == (int)sorted[5 * c + 2]).FrustumCullSphere, view.Camera))
                .ToList();
            Assert.Multiple(() =>
            {
                Assert.That(Commands(sorted, count), Is.EquivalentTo(Commands(written, count)), $"pool {p}: the same commands");
                Assert.That(buckets.Zip(buckets.Skip(1)).All(b => b.Second >= b.First - 1), Is.True,
                    $"pool {p}: nearest first: {string.Join(' ', buckets)}");
            });
            if (!Commands(sorted, count).SequenceEqual(Commands(written, count))) reordered++;
            region += listed.Count;
        }

        Assert.That(reordered, Is.Positive, "some draw's order changed");
    });

    // Sorted only while OpenGL tests and writes depth with LESS or LEQUAL and blends into no draw buffer
    [Test]
    public void OnlyAnOrderlessStateIsSorted()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        Assert.That(OcclusionCulling.Orderless(), Is.False, "not tapped: OpenGL's state unknown");
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.Blend);
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            var states = new List<bool> { OcclusionCulling.Orderless() };
            GL.Enable(IndexedEnableCap.Blend, 3);
            states.Add(OcclusionCulling.Orderless());
            GL.Disable(EnableCap.Blend);
            GL.DepthMask(false);
            states.Add(OcclusionCulling.Orderless());
            GL.DepthMask(true);
            GL.DepthFunc(DepthFunction.Always);
            states.Add(OcclusionCulling.Orderless());
            GL.DepthFunc(DepthFunction.Less);
            states.Add(OcclusionCulling.Orderless());
            GL.Disable(EnableCap.DepthTest);
            states.Add(OcclusionCulling.Orderless());
            Assert.That(states, Is.EqualTo(Expected),
                "LEQUAL, a blending buffer, no depth writes, ALWAYS, LESS, no depth test");
        }
        finally
        {
            GlTap.Untap();
        }
    }

    private static List<(uint, uint)> Commands(uint[] commands, int count) =>
        [.. Enumerable.Range(0, count).Select(c => (commands[5 * c], commands[5 * c + 2]))];

    // rows.comp's bucket, in doubles
    private static int Bucket(Sphere sphere, Vec3d camera)
    {
        var e = sphere.radius / 1.7320508f;
        double[] d = [Math.Abs(sphere.x - camera.X) - e, Math.Abs(sphere.y - camera.Y) - e, Math.Abs(sphere.z - camera.Z) - e];
        var distance = Math.Sqrt(d.Sum(v => Math.Max(v, 0) * Math.Max(v, 0)));
        return Math.Min((int)(Math.Sqrt(distance) * 2.8), 63);
    }
}
