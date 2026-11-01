using Vintagestory.Client.NoObf;

namespace Komet.Diagnostics;

// What this module asks of modules above it. Each hook starts as the answer without that module; Modules.Wire (the mod
// project) fills it in. A module never names a module above it: the compiler refuses the reference.
internal static class Links
{
    // Rendering: the idle-animator feature's questions, so ModTimes' replay skips what the frame skipped
    public static System.Func<List<RenderHandler>, int, bool> SkipIdle { get; set; } = static (_, _) => false;
    public static System.Func<IRenderer?, bool> SkipRenderer { get; set; } = static _ => false;

    // Tessellation: a frame begins (every frame, HUD or not: the tessellation workers follow the main thread)
    public static Action FrameBegun { get; set; } = static () => { };

    // Vulkan: its GPU times while it draws (NaN, false: it does not), and the kernel's view of every thread for a hang report
    public static System.Func<double> VulkanFrameGpuMs { get; set; } = static () => double.NaN;
    public static System.Func<bool> VulkanTimesTerrain { get; set; } = static () => false;
    public static TerrainTimes VulkanTerrainGpuMs { get; set; } = static _ => false;
    public static System.Func<string> ThreadsReport { get; set; } = static () => "";
}

// The Vulkan renderer's GPU time per terrain group of the last frame, when it timed one
internal delegate bool TerrainTimes(Span<double> groups);
