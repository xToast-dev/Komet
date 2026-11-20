using HarmonyLib;
using Komet.Core;
using Komet.Rendering;
using OpenTK.Windowing.Desktop;
using static Komet.Api.KapiFeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Vulkan;

// The Vulkan device, its shader cache and timings, and the renderer choice (VulkanOnly: OpenGL, Vulkan-only or Vulkan compute).
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class VulkanFeatures
{
    internal static readonly Feature[] All =
    [
        // the device, started on the first frame after VulkanOnly goes on
        new("VulkanCore")
        {
            Order = 210,
            Install = c => VulkanCore.Install(c.Api, c.Logger),
            Stop = _ => VulkanCore.Stop(),
            Probe = () => VulkanCore.Failed ? NotApplicable : Active
        },
        // ported shaders, hot uniforms and pipeline cache on disk: known programs skip shaderc
        new("VulkanShaderCache")
        {
            Order = 220,
            Requires = ["VulkanCore"],
            Install = c => ShaderCache.Install(c.Logger, c.Api),
            Knobs =
            [
                Switch("VulkanShaderCache", "vulkan", "vulkan-advanced", () => ShaderCache.Enabled,
                        on => ShaderCache.Enabled = on) with
                    {
                        Needs = "VulkanOnly"
                    }
            ]
        },
        new("VulkanGpuTimes")
        {
            Order = 230,
            Requires = ["VulkanCore"],
            Knobs =
            [
                Switch("VulkanGpuTimes", "vulkan", "vulkan-advanced", () => VulkanFrame.Measuring,
                        on => VulkanFrame.Measuring = on) with
                    {
                        Weighed = true, Needs = "VulkanOnly"
                    }
            ]
        },
        // the renderer: VulkanOnly is the settings page's renderer choice (VulkanMode)
        new("VulkanTerrain")
        {
            Order = 260,
            After = ["GlQueryCache"], // its GlTap wraps GlQueryCache's entry points
            Replaces = () =>
                [.. PoolUpdates.Seams(), AccessTools.Method(typeof(GameWindow), "SwapBuffers")],
            Requires = ["VulkanCore"],
            Install = c => VulkanRenderer.Install(c.Harmony, c.Api, c.Logger),
            Shapes =
            [
                new EngineBodies(PoolUpdates.Seams, PoolUpdates.Fingerprint, PoolUpdates.Fingerprint123),
                new EngineBodies(VulkanRenderer.BirthSeams, VulkanRenderer.BirthFingerprint,
                    VulkanRenderer.BirthFingerprint123)
            ],
            Knobs =
            [
                // 0 OpenGL, 1 Vulkan-only, 2 Vulkan compute (the device runs, OpenGL presents). A three-value knob keeps
                // older true/false settings valid and stays one renderer choice in the options.
                new Knob("VulkanOnly", null, null, 0, 2, 0, () => VulkanRenderer.Mode,
                    value => VulkanRenderer.Mode = value)
                {
                    Suggest = VulkanRenderer.Suggested
                },
                Switch("VulkanPacing", "vulkan", "vulkan-advanced", () => VulkanRenderer.Pacing,
                        on => VulkanRenderer.Pacing = on) with
                    {
                        Needs = "VulkanOnly"
                    },
                Switch("VulkanOnlyLean", "vulkan", "vulkan-advanced", () => VulkanRenderer.Lean, on =>
                    {
                        var leaner = on && !VulkanRenderer.Lean;
                        VulkanRenderer.Lean = on;
                        if (leaner && VulkanRenderer.Only) TerrainRebuild.Request(); // OpenGL's copies of the pools go
                    }) with
                    {
                        Weighed = true, Needs = "VulkanOnly"
                    }, // a trade (memory against hand-back on a failure), not an exact saving
                // the bridge pays off on fast monitors; it copies every frame three times
                Switch("VulkanFrameBridge", "vulkan", "vulkan-advanced", () => VulkanRenderer.Bridging,
                        on => VulkanRenderer.Bridging = on)
                    with
                    {
                        Weighed = true, Suggest = hw => hw.RefreshHz >= 120 ? 1 : 0, Needs = "VulkanOnly"
                    },
                Switch("VulkanQuietUniforms", "vulkan", "vulkan-advanced", () => GlTap.QuietUniforms,
                        on => GlTap.QuietUniforms = on) with
                    {
                        Needs = "VulkanOnly"
                    }
            ],
            Stop = _ => VulkanRenderer.Stop(),
            Probe = () => VulkanRenderer.Unavailable ? NotApplicable : Active
        }
    ];
}
