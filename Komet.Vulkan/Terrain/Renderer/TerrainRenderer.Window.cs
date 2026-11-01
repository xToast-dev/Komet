using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Vulkan;

// A window-sized framebuffer stands in for OpenGL's default one, so the last passes and GUI are Vulkan's too.
internal sealed partial class TerrainRenderer
{
    private const int MaxDropsLogged = 8;

    private FrameBufferRef? _window;
    private WindowSwapchain? _swapchain;
    private bool _noSwapchain;
    private FrameBridge? _bridge;
    private FrameBridge.Shot? _bridging; // this frame's scene copied, before the GUI
    private long _bridgedBefore; // by bridges let go (a resize or the switch remakes them): Bridged only grows
    private int _refreshHz;
    private long _refreshAsked; // Stopwatch ticks of the last look at the monitor's rate
    private readonly Core.BoundedPool<Presentation> _presentations = new(4);

    private enum Showed
    {
        Shown,
        Dropped, // not this frame (no segment, no image): the window keeps the last one, the next may be shown
        Refused // no swapchain for the window
    }

    public bool Showing { get; private set; }

    // Frames Vulkan could not show (the window kept the one before)
    public long Unshown { get; private set; }

    // Vulkan that cannot present: VulkanRenderer lets go and OpenGL (which got every upload) draws.
    public string Unshowable { get; private set; } = "";

    public long Bridged => _bridgedBefore + (_bridge?.Bridged ?? 0);

    // At the GUI's start (VulkanRenderer.Bridging): the window's picture and the scene's depth copied for FrameBridge,
    // with the camera they were drawn from. A copy no frame showed (Present did not run) gives its slot back.
    public void BridgeScene(float[] view, double[] projection, float kept)
    {
        if (_bridging is { } unshown) _bridge?.Dropped(unshown.Slot);
        _bridging = null;
        if (_bridge is not { } bridge || !Frame.Open || _window is not { ColorTextureIds: [var color, ..] } ||
            Targets.Find(color) is not { } image || !Frame.Holds(image) || !NotNull(view) || !NotNull(projection)) return;
        var depth = Targets.Framebuffers is { Count: > 0 } framebuffers && framebuffers[0] is { DepthTextureId: > 0 } primary
            ? SampledFind(primary.DepthTextureId)
            : null;
        if (depth is not null && !Frame.Holds(depth)) depth = null;
        var slot = bridge.Take(image, depth);
        if (slot < 0) return;
        if (!Frame.CopyRaw(image, bridge.Scene(slot)))
        {
            bridge.Dropped(slot);
            return;
        }

        var depthed = depth is not null && bridge.Depth(slot) != 0 && Frame.CopyRaw(depth, bridge.Depth(slot));
        _bridging = FrameBridge.Shoot(slot, view, projection, depthed, kept);
    }

    // The bridge as VulkanRenderer.Bridging says, for the swapchain shown into
    private void Bridging(WindowSwapchain chain)
    {
        if (!VulkanRenderer.Bridging)
        {
            Unbridged();
            return;
        }

        if (_bridge is not null) return;
        _bridge = FrameBridge.Create(Device, chain, _refreshHz, _logger, out var why);
        if (_bridge is null) _logger?.Warning("Komet: Vulkan cannot bridge late frames: {0}", why);
        else _logger?.Notification("Komet: Vulkan bridges frames the engine misses at {0} Hz", _refreshHz);
    }

    private void Unbridged()
    {
        if (_bridge is { Bridged: > 0 } bridged)
            _logger?.Notification("Komet: Vulkan bridged {0} frames the engine missed; {1} shown frames stepped against the turn",
                bridged.Bridged, bridged.Reversed);
        _bridgedBefore += _bridge?.Bridged ?? 0;
        _bridge?.Dispose();
        (_bridge, _bridging) = (null, null);
    }

    public FrameBufferRef? Window(int width, int height)
    {
        if (!Assert(width >= 0 && height >= 0) || !Assert(width <= 1 << 15 && height <= 1 << 15)) return null;
        if (_window is { } made && made.Width == width && made.Height == height) return made;
        if (_window is null && (width == 0 || height == 0)) return null; // minimized: nothing to mark each frame
        VulkanWatch.Mark($"window stand-in made at {width}x{height}");
        Unwindowed();
        if (width == 0 || height == 0) return null;
        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int color);
        GL.TextureStorage2D(color, 1, SizedInternalFormat.Rgba8, width, height);
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int depth);
        GL.TextureStorage2D(depth, 1, (SizedInternalFormat)0x8CAC, width, height); // GL_DEPTH_COMPONENT32F
        foreach (var texture in (ReadOnlySpan<int>)[color, depth])
        {
            GL.TextureParameter(texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }

        GL.CreateFramebuffers(1, out int framebuffer);
        GL.NamedFramebufferTexture(framebuffer, FramebufferAttachment.ColorAttachment0, color, 0);
        GL.NamedFramebufferTexture(framebuffer, FramebufferAttachment.DepthAttachment, depth, 0);
        GL.NamedFramebufferDrawBuffer(framebuffer, DrawBufferMode.ColorAttachment0);
        _window = new FrameBufferRef
        {
            FboId = framebuffer, ColorTextureIds = [color], DepthTextureId = depth, Width = width, Height = height
        };
        GlTap.Window = (uint)framebuffer;
        return Assert(framebuffer > 0) ? _window : null;
    }

    public void Unwindowed()
    {
        if (_window is not { } window || !Assert(window.FboId > 0) || !Assert(GlTap.Window == window.FboId)) return;
        Frame.Close("the window's stand-in goes");
        Unbridged(); // its thread presents into the swapchain
        _swapchain?.Dispose();
        (_swapchain, _window, Showing) = (null, null, false);
        ForgetTargets();
        _ = Device.WaitIdle();
        using var quiet = GlTap.Quietly();
        var bound = (GlTap.DrawFramebuffer, GlTap.ReadFramebuffer);
        GlTap.Window = 0;
        if (bound.DrawFramebuffer == window.FboId) GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        if (bound.ReadFramebuffer == window.FboId) GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        Targets.Release(window);
        GL.DeleteFramebuffer(window.FboId);
        foreach (var texture in (window.ColorTextureIds ?? []).Bounded(4))
            if (texture > 0) GL.DeleteTexture(texture);
        if (window.DepthTextureId > 0) GL.DeleteTexture(window.DepthTextureId);
    }

    // True: Vulkan presented or dropped the frame (no segment left, no image, submit failed: the window keeps the last
    // one, swapchain kept), SwapBuffers must not run. False when Vulkan cannot show frames at all (Unshowable).
    public bool Present(NativeWindow? window)
    {
        if (_window is not { } stand) return false;
        VulkanWatch.Mark("presenting");
        var image = stand.ColorTextureIds is [var color, ..] ? Targets.Find(color) : null;
        var shown = window is not null && image is not null ? Shown(image, window) : Showed.Refused;
        Showing = shown == Showed.Shown;
        if (shown != Showed.Refused) return true;
        return Unshowing(_noSwapchain ? "Vulkan has no swapchain for the window" : "the window's image is not Vulkan's");
    }

    // Segment opened before acquiring, as an acquired image must be presented; the frame keeps its last segment for
    // this
    private Showed Shown(SharedImage color, NativeWindow window)
    {
        if (Swapchain(window) is not { } chain) return Showed.Refused;
        if (Frame.Open && !Frame.Holds(color)) Frame.Close("the window's stand-in is new");
        if (!Frame.Open && !Frame.Begin(out var refused)) return Dropped(refused);
        if (!Frame.Holds(color)) return Dropped("the segment does not hold the window's image");
        Retune(window);
        Bridging(chain);
        var index = chain.Acquire(color.Width, color.Height, (int)window.VSync, out var why);
        if (index < 0) return Dropped(why);
        var bridge = _bridge;
        var shot = _bridging is { } scene && bridge is not null && bridge.Final(scene.Slot) != 0 &&
                   Frame.CopyRaw(color, bridge.Final(scene.Slot))
            ? scene
            : (FrameBridge.Shot?)null; // the GUI drawn: the frame's own picture beside its scene
        if (_bridging is { } copied && shot is null) bridge?.Dropped(copied.Slot);
        _bridging = null;
        if (!Frame.Show(color, chain.Image(index), (chain.Width, chain.Height)))
        {
            if (shot is { } unshown) bridge!.Dropped(unshown.Slot); // a shot was taken from the bridge
            return Dropped("the frame did not show");
        }

        var presenting = _presentations.Take() ?? new Presentation(_presentations);
        (presenting.Chain, presenting.Index, presenting.Bridge, presenting.Shot) = (chain, index, bridge, shot);
        Frame.Close("the frame shown by Vulkan", presenting.Run, endsFrame: true);
        return Frame.Shown ? Showed.Shown : Dropped("the submission failed");
    }

    // Presented on the recording thread after the frame's submission, then published to the bridge; given back to the
    // pool once run, so a frame makes no closure (one the submission failed for is left to the collector)
    private sealed class Presentation
    {
        private readonly Core.BoundedPool<Presentation> _pool;

        public Presentation(Core.BoundedPool<Presentation> pool)
        {
            _pool = pool;
            Run = Present;
        }

        public Action Run { get; }
        public WindowSwapchain? Chain { get; set; }
        public int Index { get; set; }
        public FrameBridge? Bridge { get; set; }
        public FrameBridge.Shot? Shot { get; set; }

        private void Present()
        {
            if (Chain is { } chain)
            {
                _ = chain.Present(Index);
                Bridge?.Shown(Shot, chain.Presents);
            }

            (Chain, Bridge, Shot) = (null, null, null);
            _pool.Give(this);
        }
    }

    private Showed Dropped(string why)
    {
        Unshown++;
        if (!NotNull(why) || !Assert(why.Length > 0)) return Showed.Dropped;
        Frame.Close("a frame Vulkan could not show", endsFrame: true);
        if (Unshown <= MaxDropsLogged)
            _logger?.Warning("Komet: Vulkan could not show a frame, the window keeps the last one: {0}", why);
        return Showed.Dropped;
    }

    private bool Unshowing(string why)
    {
        Unshowable = why;
        Frame.Close("a frame Vulkan-only cannot show", endsFrame: true);
        return false;
    }

    private unsafe WindowSwapchain? Swapchain(NativeWindow window)
    {
        if (_swapchain is not null || _noSwapchain || !Assert(Device.Handle != IntPtr.Zero))
            return _swapchain;
        var platform = GLFW.GetPlatform();
        WindowSwapchain.Native native = platform switch
        {
            Platform.Wayland => new(WindowSwapchain.WindowKind.Wayland, GLFW.GetWaylandDisplay(),
                (nuint)GLFW.GetWaylandWindow(window.WindowPtr)),
            Platform.X11 => new(WindowSwapchain.WindowKind.X11, GLFW.GetX11Display(), GLFW.GetX11Window(window.WindowPtr)),
            Platform.Win32 => new(WindowSwapchain.WindowKind.Win32, ProcessModule(), (nuint)GLFW.GetWin32Window(window.WindowPtr)),
            _ => default
        };
        _refreshHz = Refresh(window);
        _swapchain = WindowSwapchain.Create(Device, native, out var why);
        _noSwapchain = _swapchain is null;
        if (_noSwapchain)
            _logger?.Warning("Komet: Vulkan has no swapchain: {0} ({1}); it cannot run without one", why, platform);
        else _logger?.Notification("Komet: Vulkan shows the frames itself ({0})", platform);
        return _swapchain;
    }

    // Twice a second: the window's monitor and its rate again, as players move the window or change the mode; 0 (unknown)
    // keeps the last rate known
    private void Retune(NativeWindow window)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _refreshAsked < System.Diagnostics.Stopwatch.Frequency / 2) return;
        _refreshAsked = now;
        var hz = Refresh(window);
        if (hz <= 0 || hz == _refreshHz || !Assert(hz < 100000)) return;
        _refreshHz = hz;
        if (_bridge is not { } bridge) return;
        bridge.Retuned(hz);
        _logger?.Notification("Komet: Vulkan bridges frames the engine misses at {0} Hz now", hz);
    }

    // The refresh rate of the window's monitor: its own in fullscreen, else the one under the window's centre (borderless
    // windows have none of their own), else the primary one's; 0 where GLFW does not know it
    private static unsafe int Refresh(NativeWindow window)
    {
        var monitor = GLFW.GetWindowMonitor(window.WindowPtr);
        if (monitor == null) monitor = Under(window);
        if (monitor == null) monitor = GLFW.GetPrimaryMonitor();
        var mode = monitor == null ? null : GLFW.GetVideoMode(monitor);
        return mode == null || !Assert(mode->RefreshRate >= 0) ? 0 : mode->RefreshRate;
    }

    private static unsafe OpenTK.Windowing.GraphicsLibraryFramework.Monitor* Under(NativeWindow window)
    {
        GLFW.GetWindowPos(window.WindowPtr, out var x, out var y);
        GLFW.GetWindowSize(window.WindowPtr, out var width, out var height);
        var (cx, cy) = (x + width / 2, y + height / 2);
        var monitors = GLFW.GetMonitorsRaw(out var count);
        for (var i = 0; monitors != null && i < Math.Min(count, 16); i++)
        {
            GLFW.GetMonitorPos(monitors[i], out var mx, out var my);
            var mode = GLFW.GetVideoMode(monitors[i]);
            if (mode != null && cx >= mx && cx < mx + mode->Width && cy >= my && cy < my + mode->Height) return monitors[i];
        }

        return null;
    }

    // The game's HINSTANCE, which a Win32 surface names with the HWND
    private static IntPtr ProcessModule() => OperatingSystem.IsWindows() ? GetModuleHandle(null) : IntPtr.Zero;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static partial IntPtr GetModuleHandle(string? module);
}
