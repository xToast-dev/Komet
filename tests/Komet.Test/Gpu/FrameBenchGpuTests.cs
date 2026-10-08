using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Test.Gpu;

// The engine thread's share of Vulkan's frame path, timed per stage over many frames: two segments a frame as the game runs
// them (the scene, then the window), OpenGL's own work between them touching shared images (a signal and a wait), the
// recording thread busy with each segment's draws, optionally the window shown by Vulkan. The engine's own work (engineNs a
// draw, spun on the engine thread: what the scene spends deciding and packing a draw; with it a tick of TickNs before each
// frame's passes, the world's update and the wait for the display) is left out of the stages.
// Run it alone, as the game runs:
// mesa_glthread=true dotnet test tests/Komet.Test/Komet.Test.csproj -c Release --no-build
//     --filter "FullyQualifiedName~FrameBenchGpuTests" --logger "console;verbosity=detailed"
[NonParallelizable]
[Explicit("a benchmark")]
public sealed class FrameBenchGpuTests
{
    private const int Warm = 300, Frames = 3000, Side = 256, Copy = 64, Draws = 160, WindowDraws = 70, GlCalls = 60;
    private const int DrawNs = 1500; // what the recording thread spends on a draw: descriptors pushed, pipeline bound, the draw
    private const int TickNs = 1_000_000;

    private static readonly string[] Stages =
        ["next", "OpenGL before", "open scene", "record scene", "close scene", "OpenGL between", "open window",
            "record window", "close window", "flush"];

    // images: handed over with every segment, the framebuffers' and the copies of the textures Vulkan's draws sample; the
    // first exported of them exportable (shared with OpenGL), the rest private, as TerrainTextures makes the copies now
    [TestCase(true, true, 24, 0, 24)]
    [TestCase(true, true, 24, 6000, 24)]
    [TestCase(true, true, 320, 6000, 320)]
    [TestCase(true, true, 320, 6000, 24)]
    [TestCase(false, false, 24, 6000, 24)]
    public void TheEngineThreadsShareOfAFrame(bool lazy, bool present, int images, int engineNs, int exported)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        rig.Backend!.Flush();
        using var set = new Set(device, (images, exported), engineNs);
        using var frame = VulkanFrame.Create(device, out var why);
        Assert.That(frame, Is.Not.Null, why);
        using var chain = present ? Chain(rig) : null;
        if (present && chain is null) Assert.Ignore("no swapchain for the test window");
        (frame!.Images, frame.Buffers, frame.Lazy) = (() => set.Shared, () => set.Buffers, lazy);
        var (ticks, worst) = (new long[Stages.Length], new long[Stages.Length]);
        var (submit, open, signal, wait) = (0L, 0L, 0L, 0L);
        for (var f = 0; f < Warm + Frames; f++)
        {
            if (f == Warm)
            {
                Array.Clear(ticks);
                Array.Clear(worst);
                (submit, open, signal, wait) = (frame.SubmitTicks, frame.OpenTicks, frame.SignalTicks, frame.WaitTicks);
            }

            Run(frame, set, chain, ticks, worst);
        }

        frame.Close("the bench's end");
        Assert.That(frame.Fault, Is.Empty);
        var us = 1e6 / Stopwatch.Frequency / Frames;
        TestContext.Out.WriteLine($"lazy {lazy}, present {present}, {images} images ({exported} exported, " +
                                  $"{VkMemory.Exported} exportable allocations live), " +
                                  $"engine {engineNs / 1000.0} us a draw, " +
                                  $"glthread {Environment.GetEnvironmentVariable("mesa_glthread")}: " +
                                  $"engine thread {ticks.Sum() * us:0.0} us a frame");
        for (var i = 0; i < Stages.Length; i++)
            TestContext.Out.WriteLine($"  {Stages[i],-15} {ticks[i] * us,8:0.0} us  worst " +
                                      $"{worst[i] * 1e6 / Stopwatch.Frequency,8:0} us");
        TestContext.Out.WriteLine($"  frame's own: opening {(frame.OpenTicks - open) * us:0.0}, OpenGL's signal " +
                                  $"{(frame.SignalTicks - signal) * us:0.0}, " +
                                  $"OpenGL's wait {(frame.WaitTicks - wait) * us:0.0}, " +
                                  $"submitting (recording thread) {(frame.SubmitTicks - submit) * us:0.0} us a frame");
    }

    private static void Run(VulkanFrame frame, Set set, WindowSwapchain? chain, long[] ticks, long[] worst)
    {
        if (set.EngineNs > 0) Set.Spin(Stopwatch.GetTimestamp(), TickNs);
        var at = Stopwatch.GetTimestamp();
        frame.Next();
        Lap(ticks, worst, 0, ref at);
        set.OpenGl(frame, 0);
        Lap(ticks, worst, 1, ref at);
        Assert.That(frame.Begin(SegmentSync.Ordered, out var why), Is.True, why);
        Lap(ticks, worst, 2, ref at);
        at += set.Record(frame, set.Scene, Draws);
        Lap(ticks, worst, 3, ref at);
        frame.Close("the scene's end");
        Lap(ticks, worst, 4, ref at);
        set.OpenGl(frame, 1);
        Lap(ticks, worst, 5, ref at);
        Assert.That(frame.Begin(SegmentSync.Ordered, out why), Is.True, why);
        var index = chain?.Acquire(chain.Width > 0 ? chain.Width : Side, chain.Height > 0 ? chain.Height : Side, 0,
            out why) ?? -1;
        Lap(ticks, worst, 6, ref at);
        at += set.Record(frame, set.Window, WindowDraws);
        if (chain is not null && index >= 0)
            Assert.That(frame.Show(set.Window.Colors[0]!, chain.Image(index), (chain.Width, chain.Height)), Is.True);
        Lap(ticks, worst, 7, ref at);
        frame.Close("the frame shown", chain is not null && index >= 0 ? () => chain.Present(index) : null);
        Lap(ticks, worst, 8, ref at);
        frame.Flush();
        Lap(ticks, worst, 9, ref at);
    }

    private static void Lap(long[] ticks, long[] worst, int stage, ref long at)
    {
        var now = Stopwatch.GetTimestamp();
        ticks[stage] += now - at;
        worst[stage] = Math.Max(worst[stage], now - at);
        at = now;
    }

    private static unsafe WindowSwapchain? Chain(GpuRig rig)
    {
        var platform = GLFW.GetPlatform();
        if (platform is not (Platform.X11 or Platform.Wayland) || !rig.Device!.Swapchains) return null;
        var window = rig.Window!;
        var native = platform == Platform.Wayland
            ? new WindowSwapchain.Native(true, GLFW.GetWaylandDisplay(), (nuint)GLFW.GetWaylandWindow(window.WindowPtr))
            : new WindowSwapchain.Native(false, GLFW.GetX11Display(), GLFW.GetX11Window(window.WindowPtr));
        return WindowSwapchain.Create(rig.Device, native, out _);
    }

    // The shared images as the engine's framebuffers and texture copies are handed over, two of them drawn into
    private sealed class Set : IDisposable
    {
        private readonly List<SharedImage> _images = [];
        private readonly SharedBuffer _pool;
        private readonly int _own, _ownFramebuffer;

        public Set(VulkanDevice device, (int Count, int Exported) images, int engineNs)
        {
            EngineNs = engineNs;
            var shared = new List<VulkanFrame.Shared>();
            for (var i = 0; i < images.Count; i++)
            {
                var (format, layout, gl) = i switch
                {
                    0 or 1 => (SharedFormat.Rgba8, Vk.LayoutColorAttachment, GlInterop.LayoutColorAttachment),
                    2 => (SharedFormat.Depth32F, Vk.LayoutDepthAttachment, GlInterop.LayoutDepthAttachment),
                    _ => (SharedFormat.Rgba8, Vk.LayoutShaderRead, GlInterop.LayoutShaderRead)
                };
                var usage = format == SharedFormat.Depth32F
                    ? Vk.DepthAttachment | Vk.Sampled
                    : Vk.ColorAttachment | Vk.Sampled | Vk.TransferSrc | Vk.TransferDst;
                var side = i < 6 ? Side : Copy;
                var image = (i < images.Exported
                                ? SharedImage.Create(device, side, side, 1, format, usage, out var why)
                                : SharedImage.Private(device, (side, side, 1, 1), format, usage, out why)) ??
                            throw new InvalidOperationException(why);
                _images.Add(image);
                shared.Add(new VulkanFrame.Shared(image, layout, gl));
            }

            Shared = shared;
            _pool = SharedBuffer.Create(device, 1 << 20, out var refused) ??
                    throw new InvalidOperationException(refused);
            Buffers = [(uint)_pool.Gl];
            Scene = new VulkanFrame.Target(1, [_images[0]], _images[2], false, Side, Side);
            Window = new VulkanFrame.Target(2, [_images[1]], null, false, Side, Side);
            _own = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, _own);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Side, Side, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, IntPtr.Zero);
            _ownFramebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _ownFramebuffer);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, _own, 0);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        }

        public int EngineNs { get; }
        public List<VulkanFrame.Shared> Shared { get; }
        public uint[] Buffers { get; }
        public VulkanFrame.Target Scene { get; }
        public VulkanFrame.Target Window { get; }

        // As the scene's draws post theirs, each after the engine's own work for it: the target prepared, then the draw's
        // commands, recorded on the recording thread. The engine's ticks, to leave out.
        public long Record(VulkanFrame frame, VulkanFrame.Target target, int draws)
        {
            var engine = 0L;
            for (var i = 0; i < draws; i++)
            {
                var start = Stopwatch.GetTimestamp();
                if (EngineNs > 0) Spin(start, EngineNs);
                engine += Stopwatch.GetTimestamp() - start;
                Assert.That(frame.Prepare(target, []), Is.True);
                frame.Viewport((0, 0, Side - i % 2, Side));
                frame.Post(Drawn);
            }

            return engine;
        }

        // OpenGL's work of its own (glthread queues it), then a clear of a shared image, as the tap reports it first
        public void OpenGl(VulkanFrame frame, int pass)
        {
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _ownFramebuffer);
            GL.Viewport(0, 0, Side, Side);
            for (var i = 0; i < GlCalls; i++)
            {
                GL.ClearColor(i / (float)GlCalls, pass, 0.5f, 1);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            var texture = (uint)_images[3 + pass].Texture;
            frame.GlTouched(GlTap.Touch.ClearTexture, texture);
            GL.ClearTexImage(texture, 0, PixelFormat.Rgba, PixelType.Float, [0.2f, 0.3f, 0.4f, 1f]);
        }

        private static unsafe void Drawn(IntPtr commands)
        {
            var start = Stopwatch.GetTimestamp();
            var viewport = new Vk.Viewport { Width = Side, Height = Side, MaxDepth = 1 };
            VkApi.CmdSetViewport(commands, 0, 1, &viewport);
            Spin(start, DrawNs);
        }

        public static void Spin(long start, int ns)
        {
            var spin = new SpinWait();
            for (var i = 0; i < 1_000_000 && Stopwatch.GetElapsedTime(start).Ticks * 100 < ns; i++) spin.SpinOnce(-1);
        }

        public void Dispose()
        {
            GL.DeleteFramebuffer(_ownFramebuffer);
            GL.DeleteTexture(_own);
            _pool.Dispose();
            foreach (var image in _images) image.Dispose();
        }
    }
}
