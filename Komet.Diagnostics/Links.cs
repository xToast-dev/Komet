using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Diagnostics;

// What this module asks of modules above it, one interface a concern. The composition root (Modules.Bind, the mod
// project) registers the implementations in Services; without one, Links answers with its null object: the answer
// without that module. A module never names a module above it: the compiler refuses the reference.
internal static class Links
{
    public static IRenderSkips Skips => Services.Find<IRenderSkips>() ?? NoSkips.Instance;
    public static IFrameFollower Frames => Services.Find<IFrameFollower>() ?? NoFrames.Instance;
    public static IGpuTimes Gpu => Services.Find<IGpuTimes>() ?? NoGpuTimes.Instance;
    public static IThreadReport Threads => Services.Find<IThreadReport>() ?? NoThreads.Instance;
}

// Rendering: the idle-animator feature's questions, so ModTimes' replay skips what the frame skipped
internal interface IRenderSkips
{
    bool SkipIdle(List<RenderHandler?> list, int index);
    bool SkipRenderer(IRenderer? renderer);
}

// Tessellation: a frame begins (every frame, HUD or not: the tessellation workers follow the main thread)
internal interface IFrameFollower
{
    void FrameBegun();
}

// Vulkan: its GPU times while it draws (NaN, false: it does not); TerrainGpuMs fills the time per terrain group of the
// last frame, when it timed one
internal interface IGpuTimes
{
    double FrameGpuMs { get; }
    bool TimesTerrain { get; }
    bool TerrainGpuMs(Span<double> groups);
}

// Vulkan: the kernel's view of every thread for a hang report
internal interface IThreadReport
{
    string Threads();
}

internal sealed class NoSkips : IRenderSkips
{
    public static readonly NoSkips Instance = new();

    public bool SkipIdle(List<RenderHandler?> list, int index) => false;

    public bool SkipRenderer(IRenderer? renderer) => false;
}

internal sealed class NoFrames : IFrameFollower
{
    public static readonly NoFrames Instance = new();

    public void FrameBegun()
    {
    }
}

internal sealed class NoGpuTimes : IGpuTimes
{
    public static readonly NoGpuTimes Instance = new();

    public double FrameGpuMs => double.NaN;
    public bool TimesTerrain => false;

    public bool TerrainGpuMs(Span<double> groups) => false;
}

internal sealed class NoThreads : IThreadReport
{
    public static readonly NoThreads Instance = new();

    public string Threads() => "";
}
