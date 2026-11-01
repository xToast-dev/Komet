using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// What this module asks of modules above it. Each hook starts as the answer without that module; Modules.Wire (the mod
// project) fills it in. A module never names a module above it: the compiler refuses the reference.
internal static class Links
{
    // Vulkan: the GL tap (Komet's record of the engine's GL calls while Vulkan draws): whether it records, its version (a new
    // tap each world), and its view of the engine's state (null without a tap)
    public static System.Func<bool> Tapped { get; set; } = static () => false;
    public static System.Func<long> TapVersion { get; set; } = static () => -1;
    public static System.Func<GlStateView?> TapState { get; set; } = static () => null;

    // Vulkan: a step mark for its watch log, the frame pacer's beat, and the terrain rebuild's hand-over before the pools go
    public static Action<string> Mark { get; set; } = static _ => { };
    public static Action Paced { get; set; } = static () => { };
    public static Action Rebuilding { get; set; } = static () => { };

    // Chunks and Tessellation: what a terrain rebuild and a pool trim ask of the loading side
    public static System.Func<bool> LoadingBacklogged { get; set; } = static () => false;
    public static Action<ClientMain> SpareChunks { get; set; } = static _ => { };

    // Vulkan: a found mod's program read for its port (the sources on the main thread, the port as work for a worker), or
    // the error; without the Vulkan module nothing is ported
    public static System.Func<int, string, (Action? Port, string Error)> ReadPort { get; set; } = static (_, _) => (null, "no Vulkan module");

    // Ui: a shader reload through the options menu's batching (one reload for several changes), and a found shader mod that
    // started late (its adapter takes Komet's level and holds)
    public static Action<ICoreClientAPI> ReloadShaders { get; set; } = static capi => _ = capi.Shader.ReloadShaders();
    public static Action<ICoreClientAPI, string> ModStarted { get; set; } = static (_, _) => { };
    public static Action<ICoreClientAPI, string> ModStopped { get; set; } = static (_, _) => { };

    // Ui: the shader packs hook in, unhook, and have loaded (the overview page is built before the packs' pages)
    public static Action<ICoreClientAPI> ShadersHooked { get; set; } = static _ => { };
    public static Action ShadersUnhooked { get; set; } = static () => { };
    public static Action<ICoreClientAPI> ShadersLoaded { get; set; } = static _ => { };
}

// The engine's GL state as the tap recorded it: what the face sorting and the row culling ask
internal readonly record struct GlStateView(bool DepthTest, uint DepthCompare, bool DepthWrite, bool Blending, uint Culls,
    bool CounterClockwise, uint Depth);
