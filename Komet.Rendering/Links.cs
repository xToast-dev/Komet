using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// What this module asks of modules above it, one interface a concern. The composition root (Modules.Bind, the mod
// project) registers the implementations in Services; without one, Links answers with its null object: the answer
// without that module. A module never names a module above it: the compiler refuses the reference.
internal static class Links
{
    public static IVulkanSide Vulkan => Services.Find<IVulkanSide>() ?? NoVulkan.Instance;
    public static IGlTapView Tap => Services.Find<IGlTapView>() ?? NoTap.Instance;
    public static ILoadingSide Loading => Services.Find<ILoadingSide>() ?? NoLoading.Instance;
    public static IShaderUiHost ShaderUi => Services.Find<IShaderUiHost>() ?? NoShaderUi.Instance;
}

// Vulkan: whether its device fills lines and points (RenderDebug's views), a step mark for its watch log, the frame
// pacer's beat, the terrain rebuild's hand-over before the pools go, a mod pass's check against its pipeline, and a
// found mod's program read for its port (the sources on the main thread, the port as work for a worker) or the error
internal interface IVulkanSide
{
    bool NonSolidFill { get; }
    void Mark(string step);
    void Paced();
    void Rebuilding();
    string? ValidatePass(string vertex, string fragment);
    (Action? Port, string Error) ReadPort(int program, string pass);
}

// Vulkan: the GL tap (Komet's record of the engine's GL calls while Vulkan draws): whether it records, its version (a new
// tap each world), and its view of the engine's state (null without a tap)
internal interface IGlTapView
{
    bool Tapped { get; }
    long Version { get; }
    GlStateView? State();
}

// Chunks and Tessellation: what a terrain rebuild and a pool trim ask of the loading side
internal interface ILoadingSide
{
    bool Backlogged { get; }
    void SpareChunks(ClientMain game);
}

// Ui: a shader reload through the options menu's batching (one reload for several changes), a found shader mod that
// started or stopped late (its adapter takes Komet's level and holds; the features recheck), and the shader packs
// hooking in, unhooking and having loaded (the overview page is built before the packs' pages)
internal interface IShaderUiHost
{
    void ReloadShaders(ICoreClientAPI api);
    void ModStarted(ICoreClientAPI api, string mod);
    void ModStopped(string mod);
    void Hooked(ICoreClientAPI api);
    void Unhooked();
    void Loaded(ICoreClientAPI api);
}

// Without the Vulkan module: OpenGL fills every mode, nothing is marked, paced or ported. NonSolidFill is settable for
// the tests' stand-ins.
internal sealed class NoVulkan : IVulkanSide
{
    public static readonly NoVulkan Instance = new();

    public bool NonSolidFill { get; init; } = true;

    public void Mark(string step)
    {
    }

    public void Paced()
    {
    }

    public void Rebuilding()
    {
    }

    public string ValidatePass(string vertex, string fragment) => "Vulkan validator unavailable";

    public (Action? Port, string Error) ReadPort(int program, string pass) => (null, "no Vulkan module");
}

internal sealed class NoTap : IGlTapView
{
    public static readonly NoTap Instance = new();

    public bool Tapped => false;
    public long Version => -1;

    public GlStateView? State() => null;
}

internal sealed class NoLoading : ILoadingSide
{
    public static readonly NoLoading Instance = new();

    public bool Backlogged => false;

    public void SpareChunks(ClientMain game)
    {
    }
}

// Without the Ui: a reload goes straight to the engine, the rest is nobody's business
internal sealed class NoShaderUi : IShaderUiHost
{
    public static readonly NoShaderUi Instance = new();

    public void ReloadShaders(ICoreClientAPI api) => _ = api.Shader.ReloadShaders();

    public void ModStarted(ICoreClientAPI api, string mod)
    {
    }

    public void ModStopped(string mod)
    {
    }

    public void Hooked(ICoreClientAPI api)
    {
    }

    public void Unhooked()
    {
    }

    public void Loaded(ICoreClientAPI api)
    {
    }
}

// The engine's GL state as the tap recorded it: what the face sorting and the row culling ask
internal readonly record struct GlStateView(
    bool DepthTest,
    uint DepthCompare,
    bool DepthWrite,
    bool Blending,
    uint Culls,
    bool CounterClockwise,
    uint Depth);
