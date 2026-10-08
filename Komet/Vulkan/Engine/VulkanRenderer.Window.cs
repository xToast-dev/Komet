namespace Komet.Vulkan;

// A framebuffer on shared images stands in for OpenGL's window, so the final blit, AfterBlit, the GUI and Done are the
// scene's too and the frame ends at the buffer swap. Without Present, OpenGL copies the stand-in into the real window there.
internal static partial class VulkanRenderer
{
    private static bool _windowing; // the stand-in is in place this frame

    public static bool Window { get; set; } = true;
    public static bool Present { get; set; } = true;

    private static (IReadOnlyList<FrameBufferRef> Framebuffers, int[] Shared) Windowing(TerrainRenderer renderer,
        IRenderAPI render)
    {
        var shared = Shared();
        _windowing = Window && SceneOn && GlTap.Scene;
        _ = Assert(shared.Length <= EngineFramebuffers) && Assert(!_windowing || GlTap.Tapped);
        if (!NotNull(renderer) || !NotNull(render)) return (render?.FrameBuffers ?? [], shared);
        renderer.Presenting = Present;
        if (!_windowing || renderer.Window(render.FrameWidth, render.FrameHeight) is not { } window)
        {
            renderer.Unwindowed();
            _windowing = false;
            return (render.FrameBuffers, shared);
        }

        List<FrameBufferRef> framebuffers = [.. render.FrameBuffers, window];
        _ = Assert(framebuffers.Count < 64);
        return (framebuffers, [.. shared, framebuffers.Count - 1]);
    }

    private static bool Windowed(EnumRenderStage stage) =>
        _windowing && Assert(stage >= EnumRenderStage.Before) &&
        stage is EnumRenderStage.AfterBlit or EnumRenderStage.Ortho or EnumRenderStage.Done;
}
