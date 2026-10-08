using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Vulkan;

// A framebuffer object the size of the window stands in for OpenGL's default framebuffer, so the engine's last passes and
// GUI are Vulkan's to draw too.
internal sealed partial class TerrainRenderer
{
    private FrameBufferRef? _window;
    private WindowSwapchain? _swapchain;
    private bool _noSwapchain;

    public bool Presenting { get; set; }

    public bool Showing { get; private set; }

    // Frames Vulkan could not show while it shows the frames, which OpenGL showed instead
    public long Unshown { get; private set; }

    public FrameBufferRef? Window(int width, int height)
    {
        if (!Assert(width >= 0 && height >= 0) || !Assert(width <= 1 << 15 && height <= 1 << 15)) return null;
        if (_window is { } made && made.Width == width && made.Height == height) return made;
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
        _swapchain?.Dispose();
        (_swapchain, _window, Showing, _presented) = (null, null, false, false);
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

    // True: Vulkan showed the frame, the engine's SwapBuffers must not run. A frame Vulkan cannot show (no segment left,
    // no image acquired, a submission failed) OpenGL shows, the swapchain kept for the next one: dropped instead, no frame
    // reached the window while the cause lasted (a GUI that used up every frame's segments left it black).
    public bool Present(NativeWindow? window)
    {
        if (_window is not { } stand || !Assert(stand.Width > 0) || !Assert(stand.Height > 0)) return false;
        VulkanWatch.Mark("presenting");
        var image = stand.ColorTextureIds is [var color, ..] ? Targets.Find(color) : null;
        var shown = Presenting && window is not null && image is not null ? Shown(image, window) : Showed.Refused;
        Showing = shown == Showed.Shown;
        _presented |= Showing;
        if (Showing) return true;
        if (shown == Showed.Refused && _presented) Unpresented(); // Vulkan no longer shows (switched off): OpenGL does
        Frame.Close("the frame shown by OpenGL");
        using var quiet = GlTap.Quietly();
        GlTap.Present(stand.Width, stand.Height);
        return false;
    }

    private enum Showed
    {
        Shown,
        Dropped, // not this frame (no segment, no image): OpenGL shows it, the next may be Vulkan's
        Refused // no swapchain for the window
    }

    private bool _presented;

    // The segment is open (holding the stand-in) before an image is acquired, as an acquired image must be shown (one
    // that was not is the next acquire's again); the frame keeps its last segment for it
    private Showed Shown(SharedImage color, NativeWindow window)
    {
        if (Swapchain(window) is not { } chain || !NotNull(color) || !Assert(color.Width > 0)) return Showed.Refused;
        if (Frame.Open && !Frame.Holds(color)) Frame.Close("the window's stand-in is new");
        if (!Frame.Open && !Frame.Begin(SegmentSync.Ordered, out var refused, last: true)) return Dropped(refused);
        if (!Frame.Holds(color)) return Dropped("the segment does not hold the window's image");
        var index = chain.Acquire(color.Width, color.Height, (int)window.VSync, out var why);
        if (index < 0) return Dropped(why);
        if (!Frame.Show(color, chain.Image(index), (chain.Width, chain.Height))) return Dropped("the frame did not show");
        Frame.Close("the frame shown by Vulkan", () => chain.Present(index)); // presented on the recording thread
        return Frame.Shown ? Showed.Shown : Dropped("the submission failed");
    }

    private Showed Dropped(string why)
    {
        Unshown++;
        if (!NotNull(why) || !Assert(why.Length > 0)) return Showed.Dropped;
        Frame.Close("a frame Vulkan could not show");
        if (Unshown <= MaxDropsLogged)
            _logger?.Warning("Komet: Vulkan could not show a frame, OpenGL shows it: {0}", why);
        return Showed.Dropped;
    }

    private const int MaxDropsLogged = 8;

    private void Unpresented()
    {
        _ = Assert(_window is not null) && Assert(Device.Handle != IntPtr.Zero);
        _swapchain?.Dispose();
        (_swapchain, _presented) = (null, false);
    }

    private unsafe WindowSwapchain? Swapchain(NativeWindow window)
    {
        if (_swapchain is not null || _noSwapchain || !NotNull(window) || !Assert(Device.Handle != IntPtr.Zero))
            return _swapchain;
        var platform = GLFW.GetPlatform();
        WindowSwapchain.Native native = platform switch
        {
            Platform.Wayland => new(true, GLFW.GetWaylandDisplay(), (nuint)GLFW.GetWaylandWindow(window.WindowPtr)),
            Platform.X11 => new(false, GLFW.GetX11Display(), GLFW.GetX11Window(window.WindowPtr)),
            _ => default
        };
        _ = Assert(native.Wayland == (platform == Platform.Wayland)) && Assert(Device.Handle != IntPtr.Zero);
        _swapchain = WindowSwapchain.Create(Device, native, out var why);
        _noSwapchain = _swapchain is null;
        if (_noSwapchain) _logger?.Warning("Komet: OpenGL shows the frames: {0} ({1})", why, platform);
        else _logger?.Notification("Komet: Vulkan shows the frames itself ({0})", platform);
        return _swapchain;
    }
}
