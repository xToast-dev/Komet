using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

public sealed class OcclusionCullingGpuTests
{
    private const int Commands = 2 * CullingState.CommandUints; // two DrawElementsIndirectCommands

    private static readonly Occlusion.Range[] Ranges =
    [
        new(-2, -2, -22, 300, 2, 2, -18, 0), // behind the wall
        new(-1, -1, -6, 600, 1, 1, -4, 300) // in front of it
    ];

    private static ICoreClientAPI Client(FrameBufferRef depth) =>
        Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["get_Render"] = _ =>
                Answers.Of<IRenderAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
                    { ["get_FrameBuffers"] = _ => new List<FrameBufferRef> { depth } })
        });

    // counters: pass 0's ranges, hidden, triangles, hidden; pass 1's the same; the draw's two counts. Commands: count, instances,
    // first index, base vertex, base instance
    private static (uint[] Counters, uint Count, uint Late, uint[] First, uint[] Second) Frame(float[] projection)
    {
        OcclusionCulling.RunFrame(Ranges, projection, (0, 0, 0));
        return Read();
    }

    private static (uint[] Counters, uint Count, uint Late, uint[] First, uint[] Second) Read()
    {
        var counters = new uint[8 + 2 * OcclusionCulling.MaxDraws];
        var (first, late) = (new uint[Commands], new uint[Commands]);
        CullingState.Frame(counters, first, late);
        return (counters[..8], counters[8], counters[8 + OcclusionCulling.MaxDraws], first, late);
    }

    private static void Run(string backend, bool wall, Action<GpuRig, FrameBufferRef, float[]> test)
    {
        using var rig = GpuRig.Open(backend);
        var projection = GlContext.Projection();
        const int size = GlContext.Size;
        var depth = wall ? GlContext.Depth(projection, GlContext.WallDistance) : 1;
        var framebuffer = new FrameBufferRef { DepthTextureId = rig.Depth(depth, size), Width = size, Height = size };
        using var harmony = new TestHarmony("komet-test-culling-gpu");
        OcclusionCulling.Install(harmony, Client(framebuffer), new CapturingLogger());
        try
        {
            test(rig, framebuffer, projection);
        }
        finally
        {
            OcclusionCulling.Enabled = false;
            OcclusionCulling.Clear();
        }
    }

    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void BehindTheWallIsLeftOutAndDrawnLateOnceTheWallIsGone(string backend)
    {
        Run(backend, true,
            (rig, wall, projection) =>
            {
                var first = Frame(projection); // no pyramid yet: both kept
                var second = Frame(projection); // the wall hides the far range, in pass 0 and again in pass 1
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                // the wall is gone: pass 0 still hides it, pass 1 draws it
                wall.DepthTextureId = rig.Depth(1, GlContext.Size);
                var opened = Frame(projection);
                Assert.Multiple(() =>
                {
                    Assert.That(OcclusionCulling.Supported, Is.True, "cull.comp builds");
                    Assert.That((first.Count, first.Late), Is.EqualTo((2u, 0u)), "first frame");
                    Assert.That((second.Count, second.Late), Is.EqualTo((1u, 0u)), "second frame");
                    Assert.That(second.Counters, Is.EqualTo(new uint[] { 2, 1, 300, 100, 1, 1, 100, 100 }),
                        "second frame");
                    Assert.That(second.First[..5], Is.EqualTo(new uint[] { 600, 1, 300, 0, 0 }), "the range in front");
                    Assert.That((opened.Count, opened.Late), Is.EqualTo((1u, 1u)), "the wall gone");
                    Assert.That(opened.Second[..5], Is.EqualTo(new uint[] { 300, 1, 0, 0, 0 }),
                        "the range behind, late");
                });
            });
    }

}
