namespace Komet.Diagnostics;

// What FramePacer spent before the frame's clock starts, so the time between frames is told apart: Vulkan's wait for the
// GPU, the smoothing pad, the rest is the engine's own (input, window events). Main thread; the last frame's values.
internal static class OutsideParts
{
    public static double GpuWaitMs { get; set; }
    public static double SmoothingMs { get; set; }
}
