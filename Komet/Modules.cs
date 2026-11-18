using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Hud;
using Komet.Api;
using Komet.Options;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Komet.Vulkan;
using Komet.World;

namespace Komet;

/// <summary>
/// The composition root: the one place that names Komet's modules. All: which feature lists make up the mod (in any order:
/// each feature declares its place, FeatureOrder sorts them; Order keeps the knobs' order in komet.json and on the
/// settings pages). Bind: the services a lower module asks of a higher one (its Links), each answered by a bridge
/// (Bridges.cs). A module project never names a project above it: the compiler refuses the reference.
/// 
/// To remove a module: delete its project, its line in All and its bridge registrations in Bind; the compiler then names
/// every other place that still used it, FeatureOrder every feature that still names one of its features.
/// </summary>
internal static class Modules
{
    private static readonly Feature[][] All =
    [
        DiagnosticsFeatures.All, HudFeatures.All, OptionsFeatures.All, RenderingFeatures.All, VulkanFeatures.All,
        ChunkFeatures.All, WorldFeatures.All, ShapeFeatures.All, TessellationFeatures.All, CoreFeatures.All
    ];

    /// <summary>
    /// Before anything reads the feature table or a service: the mod system's StartPre, the tests' set-up. Again after
    /// Release (the next world), it registers the same.
    /// </summary>
    public static void Register()
    {
        Features.Use(All);
        Bind();
    }

    /// <summary>
    /// The world closed: the services go with it (KometModSystem.Dispose, last)
    /// </summary>
    public static void Release() => Services.Clear();

    private static void Bind()
    {
        // Core asks the shader module behind the public shader API, and the Vulkan module who draws
        Services.Register<IShaderBackend>(ShaderBackend.Instance);
        Services.Register<IRenderStepHost>(ModSteps.Instance);
        Services.Register<IRenderPath>(VulkanBridge.Instance);

        // Diagnostics asks Rendering and Tessellation (frames) and Vulkan (GPU times, threads)
        Services.Register<IRenderSkips>(RenderFrameBridge.Instance);
        Services.Register<IFrameFollower>(RenderFrameBridge.Instance);
        Services.Register<IGpuTimes>(VulkanBridge.Instance);
        Services.Register<IThreadReport>(VulkanBridge.Instance);

        // Rendering asks Vulkan, the loading side (Chunks, Tessellation) and the Ui
        Services.Register<IVulkanSide>(VulkanBridge.Instance);
        Services.Register<IGlTapView>(VulkanBridge.Instance);
        Services.Register<ILoadingSide>(LoadingBridge.Instance);
        Services.Register<IShaderUiHost>(ShaderUiBridge.Instance);
    }
}
