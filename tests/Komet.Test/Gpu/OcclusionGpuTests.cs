using System.Runtime.InteropServices;
using Komet.Gpu;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Test.Gpu;

public sealed class OcclusionGpuTests
{
    internal const int Size = 64, WallDistance = 10;
    private const string Measured = "Komet.Rendering.Occlusion, Komet";
    private static bool _resolving;

    // OpenTK asks for libglfw.so.3 by name, which the loader then finds among the libraries already in the process; the game ships it
    private static void Resolve()
    {
        if (_resolving) return;
        _resolving = true;
        _ = NativeLibrary.TryLoad(Path.Combine(GameInstall.Lib, "libglfw.so.3"), out _);
    }

    internal static string Why { get; private set; } = "";

    internal static NativeWindow? Context()
    {
        Resolve();
        GLFWProvider.CheckForMainThread = false; // NUnit runs tests on worker threads; the context stays on this one
        try
        {
            var window = new NativeWindow(new NativeWindowSettings
            {
                StartVisible = false, APIVersion = new Version(4, 6), Profile = ContextProfile.Compatability,
                ClientSize = new OpenTK.Mathematics.Vector2i(Size, Size)
            });
            window.MakeCurrent();
            return window;
        }
        catch (Exception e) when (e is GLFWException or DllNotFoundException or TypeInitializationException
                                      or InvalidOperationException)
        {
            Why = e.ToString();
            return null;
        }
    }

    internal static float[] Projection() => Mat4f.Perspective(new float[16], MathF.PI / 2, 1, 0.1f, 1000);

    internal static float Depth(float[] projection, float distance)
    {
        var clip = Mat4f.MulWithVec4(projection, [0, 0, -distance, 1]);
        return clip[2] / clip[3] * 0.5f + 0.5f;
    }

    // A depth texture as the engine makes the primary one, cleared to the wall's depth
    internal static int Wall(float depth)
    {
        GL.GenTextures(1, out int texture);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.DepthComponent24, Size, Size, 0,
            PixelFormat.DepthComponent, PixelType.Float, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.GenFramebuffers(1, out int fbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, texture, 0);
        GL.DrawBuffer(DrawBufferMode.None);
        GL.ClearDepth(depth);
        GL.Clear(ClearBufferMask.DepthBufferBit);
        return texture;
    }

    // The two boxes relative to a camera at z, which looks down -z
    private static void Boxes(float z)
    {
        Occlusion.Gathered[0] = new Occlusion.Range(-2, -2, -22 - z, 300, 2, 2, -18 - z, 0); // behind the wall
        Occlusion.Gathered[1] = new Occlusion.Range(-1, -1, -6 - z, 600, 1, 1, -4 - z, 300); // in front of it
    }

    private static uint[] Frame(int depth, float[] viewProjection, float z)
    {
        Boxes(z);
        Occlusion.Run(depth, Size, Size, 2, viewProjection, (0, 0, z));
        var counters = new uint[20];
        if (Occlusion.Supported)
        {
            var buffers = CounterBuffers(null);
            Gpu(null).Read(buffers[(FrameCount(null) - 1) % buffers.Length], 0, counters);
        }

        return counters;
    }

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "get_Gpu")]
    private static extern IGpuBackend Gpu([UnsafeAccessorType(Measured)] object? occlusion);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "CounterBuffers")]
    private static extern ref int[] CounterBuffers([UnsafeAccessorType(Measured)] object? occlusion);

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_frame")]
    private static extern ref int FrameCount([UnsafeAccessorType(Measured)] object? occlusion);

    private static void Run(string backend, Action<GpuRig, int, float[]> test)
    {
        using var rig = GpuRig.Open(backend);
        using var harmony = new TestHarmony("komet-test-occlusion-gpu");
        Occlusion.Install(harmony, Answers.NullClient, new CapturingLogger());
        try
        {
            var projection = Projection();
            test(rig, rig.Depth(Depth(projection, WallDistance), Size), projection);
        }
        finally
        {
            Occlusion.Clear();
        }
    }

    // The same passes on OpenGL and on Vulkan (the Vulkan backend, from the same GLSL as SPIR-V) must count the same
    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void TheWallHidesTheBoxBehindItThisFrameAndTheNext(string backend) => Run(backend, (rig, depth, projection) =>
    {
        var first = Frame(depth, projection, 0);
        var second = Frame(depth, projection, 0);
        var moved = Frame(depth, projection, 1); // the camera stepped a block back: the boxes are a block farther
        var opened = Frame(rig.Depth(1, Size), projection, 1); // wall gone: the box behind it shows a frame late
        Assert.Multiple(() =>
        {
            Assert.That(Occlusion.Supported, Is.True, "the programs compile and link");
                // Per test (before, then after) the ranges and triangles tested and hidden, then the ranges left visible through the
                // near plane, off screen, in front, without a level; last this frame buffer's totals: compared, shown late, their
                // triangles, frames
            Assert.That(first,
                Is.EqualTo(new uint[] { 0, 0, 0, 0, 0, 0, 0, 0, 2, 300, 1, 100, 0, 0, 1, 0, 0, 0, 0, 0 }),
                "first frame: no pyramid before");
            Assert.That(second,
                Is.EqualTo(new uint[] { 2, 300, 1, 100, 0, 0, 1, 0, 2, 300, 1, 100, 0, 0, 1, 0, 2, 0, 0, 1 }),
                "second frame");
            Assert.That(moved,
                Is.EqualTo(new uint[] { 2, 300, 1, 100, 0, 0, 1, 0, 2, 300, 1, 100, 0, 0, 1, 0, 2, 0, 0, 1 }),
                "after a step");
            Assert.That(opened,
                Is.EqualTo(new uint[] { 2, 300, 1, 100, 0, 0, 1, 0, 2, 300, 0, 0, 0, 0, 2, 0, 2, 1, 100, 1 }),
                "the wall gone");
        });
    });

    // Vulkan draws the frame without culling: the measurement runs on its backend in the frame's segments (the depth the
    // frame's image of the engine's), counts as on OpenGL, and OpenGL runs no compute shader; drawn by OpenGL again, it does
    [Test]
    public void OnTheVulkanFrameOpenGlComputesNothing()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        GpuBackends.Current = GlBackend.Instance; // the culling's: off
        var device = rig.Device!;
        var (texture, framebuffer) = OcclusionCullingGpuTests.SharedDepth();
        const uint usage = Komet.Vulkan.Vk.Sampled | Komet.Vulkan.Vk.DepthAttachment | Komet.Vulkan.Vk.TransferDst;
        using var depth = SharedImage.Adopt(device, texture, (Size, Size, 1, 1), SharedFormat.Depth32F, usage, out var why);
        Assert.That(depth, Is.Not.Null, why);
        var projection = Projection();
        OcclusionCullingGpuTests.Clear(framebuffer, Depth(projection, WallDistance));
        using var frame = VulkanFrame.Create(device, out why);
        Assert.That(frame, Is.Not.Null, why);
        frame!.Images = () => [new VulkanFrame.Shared(depth!, Komet.Vulkan.Vk.LayoutDepthAttachment,
            GlInterop.LayoutDepthAttachment)];
        using var backend = new VulkanBackend(device, frame, t => t == texture ? depth : null);
        GpuBackends.Frame = backend;
        var query = GL.GenQuery();
        using var harmony = new TestHarmony("komet-test-occlusion-frame");
        Occlusion.Install(harmony, Answers.NullClient, new CapturingLogger());
        try
        {
            var (vulkan, second) = Computed(query, () =>
            {
                for (var i = 0; i < 2; i++)
                {
                    frame.Next();
                    Boxes(0);
                    Occlusion.Run(backend.Engine(texture), Size, Size, 2, projection, (0, 0, 0));
                    frame.Close("the test's end");
                    Assert.That(device.WaitIdle(), Is.True);
                }
            });
            var onVulkan = ReferenceEquals(Gpu(null), backend);
            GpuBackends.Frame = null; // OpenGL draws again: the measurement goes back to it
            var (gl, _) = Computed(query, () => Frame(Wall(Depth(projection, WallDistance)), projection, 0));
            Assert.Multiple(() =>
            {
                Assert.That(onVulkan, Is.True, "the measurement ran on the frame's backend");
                Assert.That(second, Is.EqualTo(new uint[] { 2, 300, 1, 100, 0, 0, 1, 0, 2, 300, 1, 100, 0, 0, 1, 0, 2, 0, 0, 1 }),
                    "the second frame counts as on OpenGL");
                Assert.That(vulkan, Is.Zero, "no OpenGL compute shader ran");
                Assert.That(gl, Is.GreaterThan(0), "on OpenGL it does (the query counts)");
                Assert.That(backend.Refused, Is.Zero, "the frame took every pass");
            });
        }
        finally
        {
            Occlusion.Clear();
            GpuBackends.Frame = null;
            GL.DeleteQuery(query);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(framebuffer);
            GL.DeleteTexture(texture);
        }
    }

    // GL_COMPUTE_SHADER_INVOCATIONS_ARB over the run, and the counters of the run's last frame
    private static (long Invocations, uint[] Counters) Computed(int query, Action run)
    {
        GL.BeginQuery((QueryTarget)0x82F5, query);
        run();
        GL.EndQuery((QueryTarget)0x82F5);
        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long invocations);
        var counters = new uint[20];
        Gpu(null).Read(CounterBuffers(null)[(FrameCount(null) - 1) % CounterBuffers(null).Length], 0, counters);
        return (invocations, counters);
    }

    // What the HUD reads: the readable copy of the oldest frame of the ring, the same counts as the frame's own buffer
    [TestCase(GpuRig.Gl)]
    [TestCase(GpuRig.Vulkan)]
    public void TheHudReadsTheOldestFramesCopy(string backend) => Run(backend, (_, depth, projection) =>
    {
        var frames = new List<uint[]>();
        for (var i = 0; i < 5; i++) frames.Add(Frame(depth, projection, 0));
        Occlusion.ReadOldest(); // five frames done: the ring's oldest is the third
        var read = Enumerable.Range(0, 16).Select(i => (uint)Occlusion.Count(i)).ToArray();
        Assert.That(read, Is.EqualTo(frames[2][..16]));
    });
}
