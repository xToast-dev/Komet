using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using Komet.Tessellation;
using Komet.Vulkan;
using Vintagestory.Client.NoObf;

namespace Komet;

// The answers the higher modules give the lower ones' service interfaces (Modules.Bind registers one of each). Each
// only forwards to the statics of the module that owns the answer.

/// <summary>
/// Vulkan, for Core (who draws), Rendering (its device, watch, pacing, ports and GL tap) and Diagnostics (GPU times and
/// the thread report)
/// </summary>
internal sealed class VulkanBridge : IRenderPath, IVulkanSide, IGlTapView, IGpuTimes, IThreadReport
{
    public static readonly VulkanBridge Instance = new();

    public bool VulkanDraws => VulkanRenderer.GlIdle;
    public bool VulkanWanted => VulkanRenderer.Requested;
    public bool NonSolidFill => VulkanRenderer.NonSolidFill;
    public bool Tapped => GlTap.Tapped;
    public long Version => GlTap.Version;
    public double FrameGpuMs => VulkanRenderer.FrameGpuMs;
    public bool TimesTerrain => VulkanRenderer.TimesTerrain;

    public void Mark(string step) => VulkanWatch.Mark(step);

    public void Paced() => VulkanRenderer.Paced();

    public string Lock => VulkanRenderer.Locked;

    public void Retry() => VulkanRenderer.Retry();

    public void Rebuilding() => VulkanRenderer.Rebuilding();

    public string? ValidatePass(string vertex, string fragment) => VulkanPorts.ValidatePass(vertex, fragment);

    public (Action? Port, string Error) ReadPort(int program, string pass) => VulkanPorts.Read(program, pass);

    public GlStateView? State() => GlTap.View();

    public bool TerrainGpuMs(Span<double> groups) => VulkanRenderer.TerrainGpuMs(groups);

    public string Threads() => VulkanWatch.Threads();
}

/// <summary>
/// Chunks and Tessellation, for Rendering's terrain rebuild and pool trim
/// </summary>
internal sealed class LoadingBridge : ILoadingSide
{
    public static readonly LoadingBridge Instance = new();

    public bool Backlogged => TessSeams.Backlogged;

    public void SpareChunks(ClientMain game) => ChunkPacking.Spares(game);
}

/// <summary>
/// Ui, for Rendering's shader packs and mods: the options menu's reload batching, the mod adapters, the overview page
/// </summary>
internal sealed class ShaderUiBridge : IShaderUiHost
{
    public static readonly ShaderUiBridge Instance = new();

    public void ReloadShaders(ICoreClientAPI api) => EngineOptions.ReloadShaders(api);

    /// <summary>
    /// A late shader mod's adapter takes Komet's level and holds; its patches may change what the guards see
    /// </summary>
    /// <param name="api"></param>
    /// <param name="mod"></param>
    public void ModStarted(ICoreClientAPI api, string mod)
    {
        ModAdapters.Started(api, mod);
        Features.Recheck();
    }

    public void ModStopped(string mod)
    {
        ModAdapters.Stopped(mod);
        Features.Recheck();
    }

    public void Hooked(ICoreClientAPI api) => ShaderOverview.Hook(api);

    public void Unhooked() => ShaderOverview.Unhook();

    public void Loaded(ICoreClientAPI api) => ShaderOverview.Build(api);
}

/// <summary>
/// Rendering and Tessellation, for Diagnostics' mod times (the idle animators' skips) and frame clock (the tessellation
/// governor follows every frame)
/// </summary>
internal sealed class RenderFrameBridge : IRenderSkips, IFrameFollower
{
    public static readonly RenderFrameBridge Instance = new();

    public bool SkipIdle(List<RenderHandler?> list, int index) => IdleAnimators.Skip(list, index);

    public bool SkipRenderer(IRenderer? renderer) => IdleAnimators.TrySkip(renderer);

    public void FrameBegun() => TessGovernor.Frame();
}
