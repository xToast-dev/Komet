using Komet.Gpu;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

public sealed class OcclusionCullingGpuTests
{
    private const int CommandUints = 5, Commands = 2 * CommandUints; // two DrawElementsIndirectCommands
    private const string Culling = "Komet.Rendering.OcclusionCulling, Komet";

    private static readonly Occlusion.Range[] Ranges =
    [
        new(-2, -2, -22, 300, 2, 2, -18, 0), // behind the wall
        new(-1, -1, -6, 600, 1, 1, -4, 300) // in front of it
    ];

    private static ICoreClientAPI Client(FrameBufferRef depth) => Answers.Of<ICoreClientAPI>(new()
    {
        ["get_Render"] = _ =>
            Answers.Of<IRenderAPI>(new() { ["get_FrameBuffers"] = _ => new List<FrameBufferRef> { depth } })
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
        ReadFrame(counters, first, late);
        return (counters[..8], counters[8], counters[8 + OcclusionCulling.MaxDraws], first, late);
    }

    private static void Run(string backend, bool wall, Action<GpuRig, FrameBufferRef, float[]> test)
    {
        using var rig = GpuRig.Open(backend);
        var projection = OcclusionGpuTests.Projection();
        var size = OcclusionGpuTests.Size;
        var depth = wall ? OcclusionGpuTests.Depth(projection, OcclusionGpuTests.WallDistance) : 1;
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
    public void BehindTheWallIsLeftOutAndDrawnLateOnceTheWallIsGone(string backend) => Run(backend, true,
        (rig, wall, projection) =>
        {
            var first = Frame(projection); // no pyramid yet: both kept
            var second = Frame(projection); // the wall hides the far range, in pass 0 and again in pass 1
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            // the wall is gone: pass 0 still hides it, pass 1 draws it
            wall.DepthTextureId = rig.Depth(1, OcclusionGpuTests.Size);
            var opened = Frame(projection);
            Assert.Multiple(() =>
            {
                Assert.That(OcclusionCulling.Supported, Is.True, "cull.comp builds");
                Assert.That((first.Count, first.Late), Is.EqualTo((2u, 0u)), "first frame");
                Assert.That((second.Count, second.Late), Is.EqualTo((1u, 0u)), "second frame");
                Assert.That(second.Counters, Is.EqualTo(new uint[] { 2, 1, 300, 100, 1, 1, 100, 100 }), "second frame");
                Assert.That(second.First[..5], Is.EqualTo(new uint[] { 600, 1, 300, 0, 0 }), "the range in front");
                Assert.That((opened.Count, opened.Late), Is.EqualTo((1u, 1u)), "the wall gone");
                Assert.That(opened.Second[..5], Is.EqualTo(new uint[] { 300, 1, 0, 0, 0 }), "the range behind, late");
            });
        });

    // Pool draws without ranges between those with: each range still lands in its own draw's count and region
    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void EachRangeFindsItsDrawAmongEmptyOnes(string backend) => Run(backend, false, (_, _, projection) =>
    {
        OcclusionCulling.RunFrame(Ranges, projection, (0, 0, 0), [0, 1, 0, 0, 1, 0]);
        var counters = new uint[8 + 2 * OcclusionCulling.MaxDraws];
        var (first, late) = (new uint[Commands], new uint[Commands]);
        ReadFrame(counters, first, late);
        Assert.Multiple(() =>
        {
            Assert.That(counters[8..14], Is.EqualTo(new uint[] { 0, 1, 0, 0, 1, 0 }), "a range per draw with ranges");
            Assert.That(first[..5], Is.EqualTo(new uint[] { 300, 1, 0, 0, 0 }), "the first range, at its draw's start");
            Assert.That(first[5..], Is.EqualTo(new uint[] { 600, 1, 300, 0, 0 }), "the second, at its draw's start");
        });
    });

    // What the HUD reads: the readable copy of the oldest frame of the ring, the same counts as the frame's own buffer
    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void TheHudReadsTheOldestFramesCopy(string backend) => Run(backend, true, (_, _, projection) =>
    {
        var frames = new List<uint[]>();
        for (var i = 0; i < 5; i++) frames.Add(Frame(projection).Counters);
        OcclusionCulling.Enabled = true;
        OcclusionCulling.Sample(); // five frames done: the ring's oldest is the third
        var read = Enumerable.Range(0, 8).Select(i => (uint)OcclusionCulling.Stat(i)).ToArray();
        Assert.That(read, Is.EqualTo(frames[2]));
    });

    // The passes on the Vulkan frame (VulkanBackend recording into its segments, the depth the frame's shared image of the
    // engine's texture) count as they do on OpenGL and on Vulkan alone
    [Test]
    public void OnTheVulkanFrameTheWallHidesTheRangeBehindIt()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        var (texture, framebuffer) = SharedDepth();
        const uint usage = Komet.Vulkan.Vk.Sampled | Komet.Vulkan.Vk.DepthAttachment | Komet.Vulkan.Vk.TransferDst;
        var size = OcclusionGpuTests.Size;
        using var depth = SharedImage.Adopt(device, texture, (size, size, 1, 1), SharedFormat.Depth32F, usage, out var why);
        Assert.That(depth, Is.Not.Null, why);
        var projection = OcclusionGpuTests.Projection();
        Clear(framebuffer, OcclusionGpuTests.Depth(projection, OcclusionGpuTests.WallDistance));
        using var frame = VulkanFrame.Create(device, out why);
        Assert.That(frame, Is.Not.Null, why);
        frame!.Images = () => [new VulkanFrame.Shared(depth!, Komet.Vulkan.Vk.LayoutDepthAttachment,
            GlInterop.LayoutDepthAttachment)];
        using var backend = new VulkanBackend(device, frame, t => t == texture ? depth : null);
        GpuBackends.Current = backend;
        var wall = new FrameBufferRef { DepthTextureId = texture, Width = size, Height = size };
        using var harmony = new TestHarmony("komet-test-culling-frame");
        OcclusionCulling.Install(harmony, Client(wall), new CapturingLogger());
        try
        {
            var first = OnFrame(frame, device, projection);
            var second = OnFrame(frame, device, projection);
            Assert.Multiple(() =>
            {
                Assert.That(OcclusionCulling.Supported, Is.True, "cull.comp builds for the frame");
                Assert.That((first.Count, first.Late), Is.EqualTo((2u, 0u)), "first frame");
                Assert.That((second.Count, second.Late), Is.EqualTo((1u, 0u)), "second frame");
                Assert.That(second.Counters, Is.EqualTo(new uint[] { 2, 1, 300, 100, 1, 1, 100, 100 }), "second frame");
                Assert.That(backend.Refused, Is.Zero, "the frame took every pass");
            });
        }
        finally
        {
            OcclusionCulling.Clear();
            GpuBackends.Current = GlBackend.Instance;
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            GL.DeleteTexture(texture);
        }
    }

    private static (uint[] Counters, uint Count, uint Late, uint[] First, uint[] Second) OnFrame(VulkanFrame frame,
        VulkanDevice device, float[] projection)
    {
        frame.Next();
        OcclusionCulling.RunFrame(Ranges, projection, (0, 0, 0));
        frame.Close("the test's end");
        Assert.That(device.WaitIdle(), Is.True);
        return Read();
    }

    // A mutable 32-bit float depth texture, as the engine's, attached to a framebuffer of its own
    internal static (int Texture, int Framebuffer) SharedDepth()
    {
        var size = OcclusionGpuTests.Size;
        GL.GenTextures(1, out int texture);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent32f, size, size, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.GenFramebuffers(1, out int framebuffer);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, texture, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        return (texture, framebuffer);
    }

    internal static void Clear(int framebuffer, float depth)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        GL.ClearDepth(depth);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        GL.Finish();
    }

    // The frame just run: its counters, and its first draw's commands of both passes (or the shadow first draw's, which
    // OcclusionCulling places after both camera passes')
    internal static void ReadFrame(Span<uint> counters, Span<uint> first, Span<uint> late, bool shadow = false)
    {
        if (!OcclusionCulling.Supported) return;
        var (gpu, buffers, commands) = (CullingGpu(null), CounterBuffers(null), CommandBuffer(null));
        gpu.Read(buffers[(FrameCount(null) - 1) % buffers.Length], 0, counters);
        gpu.Read(commands, (shadow ? 2 * OcclusionCulling.MaxRanges : 0) * CommandUints, first);
        gpu.Read(commands, OcclusionCulling.MaxRanges * CommandUints, late);
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_Gpu")]
    private static extern IGpuBackend CullingGpu([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CounterBuffers")]
    private static extern ref int[] CounterBuffers([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_commands")]
    private static extern ref int CommandBuffer([UnsafeAccessorType(Culling)] object? culling);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_frame")]
    private static extern ref int FrameCount([UnsafeAccessorType(Culling)] object? culling);
}
