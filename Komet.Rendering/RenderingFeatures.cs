using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Rendering;

// OpenGL-side rendering: shaders, shadows, culling, the terrain pools and frame pacing.
// In install order; Core's Features places them among the other modules'.
internal static class RenderingFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget PotSeam = new(static () => PotCulling.Seams()[0]),
        HearthSeam = new(static () => HearthCulling.Seams()[0]);

    // The stage dispatch both shadow reuses skip a pass in
    private static readonly Func<MethodBase?[]> RenderStage = static () =>
        [AccessTools.DeclaredMethod(typeof(ClientMain), nameof(ClientMain.TriggerRenderStage), [typeof(EnumRenderStage), typeof(float)])];

    internal static readonly Feature[] All =
    [
        new("ShaderUseCache")
        {
            Install = c => ShaderUseCache.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ShaderUseCache", "render", "drawing", () => ShaderUseCache.Enabled,
                on => ShaderUseCache.Enabled = on)],
            Recheck = ShaderUseCache.Recheck,
            Stop = _ => ShaderUseCache.Forget(),
            Probe = () => Found(ShaderUseCache.Matched, ShaderUseCache.Blocked),
            Shapes = [new EngineBodies(ShaderUseCache.Seams, ShaderUseCache.Fingerprint)]
        },
        new("DistantShadows")
        {
            Install = c => DistantShadows.Install(c.Harmony, c.Logger),
            Knobs = [Switch("DistantShadows", "render", "shadows", () => DistantShadows.Enabled,
                on => DistantShadows.Enabled = on)
                    with { Weighed = true, Presets = new(Quality: 1, Balanced: 1, Speed: 0) }],
            Probe = () => Found(DistantShadows.Matched && DistantShadows.Injected, DistantShadows.Failed, Failed),
            Shapes = [new EngineBodies(DistantShadows.Seams, DistantShadows.Fingerprint)],
            Stop = _ => DistantShadows.Forget()
        },
        new("LeanVaryings")
        {
            Install = c => LeanVaryings.Install(c.Harmony, c.Api),
            Knobs = [Switch("LeanVaryings", "render", "drawing", () => LeanVaryings.Enabled,
                on => LeanVaryings.Enabled = on)],
            Stop = _ => ShaderEdits.Forget() // LeanVaryings installs the shader edits of the four
        },
        new("SharpUpscale")
        {
            Knobs = [Switch("SharpUpscale", "render", "drawing", () => SharpUpscale.Enabled, on => SharpUpscale.Enabled = on)],
            Probe = () => Found(ShaderEdits.Patched, false)
        },
        new("LeanGodRays")
        {
            Knobs = [Switch("LeanGodRays", "render", "drawing", () => LeanGodRays.Enabled, on => LeanGodRays.Enabled = on)],
            Probe = () => Found(ShaderEdits.Patched, false)
        },
        new("LeanPost")
        {
            Knobs = [Switch("LeanPost", "render", "drawing", () => LeanPost.Enabled, on => LeanPost.Enabled = on)],
            Probe = () => Found(ShaderEdits.Patched, false)
        },
        // other mods' shader settings, defines, uniforms, edits and passes (KometShaders, komet-shader.json); after
        // LeanGodRays: its defines and edits ride on the ShaderEdits load postfix LeanVaryings installs
        new("ModShaders")
        {
            Install = c =>
            {
                ModShaders.Install(c.Harmony, c.Api, c.Logger);
                ModPasses.Install(c.Api, c.Logger);
                ShaderPacks.Hook(c.Api);
                ShaderMods.Install(c.Harmony, c.Api); // after Hook: it knows which are switched off
            },
            Stop = _ =>
            {
                ShaderMods.Clear();
                ShaderPacks.Unhook();
                ModPasses.Clear();
                ModShaders.Clear();
            }
        },
        new("FrustumSweep")
        {
            Replaces = () => [AccessTools.Method(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull))],
            // the shadow passes' view too, which OcclusionCulling's rows read
            Install = c =>
            {
                FrustumSweep.Install(c.Harmony, c.Logger);
                ShadowView.Install(c.Harmony, c.Api, c.Logger);
            },
            Stop = _ => // its api, and with it the world, is not kept until the next one
            {
                ShadowView.Clear();
                FrustumSweep.Clear();
            },
            Knobs =
            [
                Switch("FrustumSweep", "render", "culling", () => FrustumSweep.Enabled,
                    on => FrustumSweep.Enabled = on)
            ]
        },
        new("FarShadowReuse")
        {
            Replaces = RenderStage,
            Install = c => FarShadowReuse.Install(c.Harmony),
            Stop = _ => FarShadowReuse.Clear(),
            Knobs = [Switch("FarShadowReuse", "render", "shadows", () => FarShadowReuse.Enabled,
                on => FarShadowReuse.Enabled = on) with { Weighed = true }]
        },
        new("NearShadowReuse")
        {
            Replaces = RenderStage,
            Install = c => NearShadowReuse.Install(c.Harmony),
            Stop = _ => NearShadowReuse.Clear(),
            Knobs = [Switch("NearShadowReuse", "render", "shadows", () => NearShadowReuse.Enabled,
                on => NearShadowReuse.Enabled = on)
                    with { Weighed = true, Presets = new(Quality: 0, Balanced: 1, Speed: 1) }]
        },
        // reads the view FrustumSweep's ShadowView made for the terrain of the same shadow stage
        new("EntityCasters")
        {
            Install = c => EntityCasters.Install(c.Harmony, c.Logger),
            Requires = ["FrustumSweep"],
            Knobs = [Switch("EntityCasters", "render", "culling", () => EntityCasters.Enabled,
                on => EntityCasters.Enabled = on)],
            Recheck = EntityCasters.Recheck,
            Probe = () => Found(EntityCasters.Matched, EntityCasters.Blocked),
            Shapes = [new EngineBodies(EntityCasters.Seams, EntityCasters.Fingerprint)]
        },
        new("IndirectDraw")
        {
            Replaces = () => [AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderMesh),
                [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)])],
            Install = c => IndirectDraw.Install(c.Harmony),
            Knobs = [Switch("IndirectDraw", "render", "drawing", () => IndirectDraw.Enabled,
                on => IndirectDraw.Enabled = on)],
            Probe = () => IndirectDraw.Detected && !IndirectDraw.Supported ? NotApplicable : Active
        },
        // after IndirectDraw, whose RenderMesh prefix hands it the draws
        new("OcclusionCulling")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.FrustumCull))],
            Install = c => OcclusionCulling.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [new("OcclusionCulling", "vulkan", "culling", 0, 2, 0, () => OcclusionCulling.Level,
                v => OcclusionCulling.Level = v) { Weighed = true, Needs = "VulkanOnly" }],
            Stop = _ => OcclusionCulling.Clear(),
            Probe = () => Found(OcclusionCulling.Installed, OcclusionCulling.Detected && !OcclusionCulling.Supported,
                NotApplicable)
        },
        // before VulkanTerrain, whose GlTap then wraps its entry points
        new("GlQueryCache")
        {
            Install = _ => GlQueryCache.Install(),
            Knobs = [Switch("GlQueryCache", "render", "drawing", () => GlQueryCache.Enabled,
                on => GlQueryCache.Enabled = on)],
            Probe = () => GlQueryCache.Installed ? Active : NotApplicable
        },
        // terrain rebuilt in-world (tools page, and switches that change what new pools hold)
        new("TerrainRebuild")
        {
            Install = c => TerrainRebuild.Install(c.Api),
            Stop = _ => TerrainRebuild.Stop()
        },
        // chunk pools empty for 10 s go back to the driver (run by TerrainRebuild's hook)
        new("PoolTrim")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(MeshDataPool), nameof(MeshDataPool.TryAdd))],
            Requires = ["TerrainRebuild"],
            Install = c => PoolTrim.Install(c.Harmony),
            // own name: "PoolTrim" was a switch, whose 1 now means the mildest level
            // little video memory: the chunk pools drained harder
            Knobs = [new("PoolTrim", "render", "memory", 0, PoolTrim.Max, 0, () => PoolTrim.Level, v => PoolTrim.Level = v,
                "PoolTrimLevel")
            {
                Weighed = true, Suggest = hw => hw.VramGb switch { > 0 and <= 6 => 4, > 6 and <= 10 => 3, _ => null }
            }]
        },
        new("AnimatableCulling")
        {
            Install = c => AnimatableCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("AnimatableCulling", "render", "culling", () => AnimatableCulling.Enabled,
                on => AnimatableCulling.Enabled = on)],
            Recheck = AnimatableCulling.Recheck,
            Probe = () => Found(AnimatableCulling.Matched, AnimatableCulling.Blocked),
            Shapes = [new EngineBodies(AnimatableCulling.Shaped, AnimatableCulling.Fingerprint)]
        },
        new("PotCulling")
        {
            Install = c => PotCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("PotCulling", "render", "culling", () => PotCulling.Enabled, on => PotCulling.Enabled = on)],
            Recheck = PotCulling.Recheck,
            Probe = () => PotSeam.Missing
                ? NotApplicable
                : Found(PotCulling.Matched, PotCulling.Blocked),
            Shapes = [new EngineBodies(PotCulling.Seams, PotCulling.Fingerprint)]
        },
        new("HearthCulling")
        {
            Install = c => HearthCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("HearthCulling", "render", "culling", () => HearthCulling.Enabled, on => HearthCulling.Enabled = on)],
            Recheck = HearthCulling.Recheck,
            Probe = () => HearthSeam.Missing
                ? NotApplicable
                : Found(HearthCulling.Matched, HearthCulling.Blocked),
            Shapes = [new EngineBodies(HearthCulling.Seams, HearthCulling.Fingerprint)]
        },
        new("IdleAnimators")
        {
            Replaces = () => [IdleAnimators.Trigger],
            Rivals = PatchKinds.Body,
            Install = c => IdleAnimators.Install(c.Harmony, c.Logger),
            Knobs = [Switch("IdleAnimators", "render", "culling", () => IdleAnimators.Enabled,
                on => IdleAnimators.Enabled = on)],
            Recheck = IdleAnimators.Recheck,
            Stop = _ => IdleAnimators.Clear(),
            Probe = () => Found(IdleAnimators.Matched && IdleAnimators.Rewritten, IdleAnimators.Blocked),
            Shapes = [new EngineBodies(IdleAnimators.Seams, IdleAnimators.Fingerprint)]
        },
        new("PoolScale")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.AddModel),
                [typeof(MeshData), typeof(Vec3i), typeof(int), typeof(Sphere)])],
            Rivals = PatchKinds.Body,
            Install = c => PoolScale.Install(c.Harmony),
            Knobs = [new("PoolScale", "render", "memory", PoolScale.Engine, PoolScale.MaxScale, PoolScale.Engine,
                () => PoolScale.Scale, v => PoolScale.Scale = v) { Unit = "x", Weighed = true }],
            Probe = () => PoolScale.Rewritten ? Active : EngineChanged
        },
        new("WindowSize")
        {
            Install = c => WindowSize.Install(c.Harmony),
            Knobs = [Switch("WindowSize", "render", "drawing", () => WindowSize.Enabled, on => WindowSize.Enabled = on)],
            Probe = () => WindowSize.Rewritten ? Active : EngineChanged
        },
        new("MeshRecycle")
        {
            Replaces = () =>
                [AccessTools.Method(typeof(MeshData), "CloneExtraData"), AccessTools.Method(typeof(MeshData), "DisposeExtraData")],
            Install = c => MeshRecycle.Install(c.Harmony),
            Knobs = [Switch("MeshRecycle", "render", "upload", () => MeshRecycle.Enabled,
                on => MeshRecycle.Enabled = on)]
        },
        new("FacePacking")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateSSBOMesh))],
            Install = c => FacePacking.Install(c.Harmony),
            Knobs = [Switch("FacePacking", "chunks", "arrival", () => FacePacking.Enabled,
                on => FacePacking.Enabled = on)]
        },
        new("FaceSorting")
        {
            Requires = ["FacePacking"],
            Install = c => FaceSorting.Install(c.Harmony), // switched by OcclusionCulling's level
            Stop = _ => FaceSorting.Clear()
        },
        new("FramePacer")
        {
            Install = c => FramePacer.Install(c.Harmony),
            Knobs = [Switch("FramePacer", "render", "pacing", () => FramePacer.Enabled, on => FramePacer.Enabled = on)
                    with { Weighed = true },
                Switch("FrameSmoothing", "render", "pacing", () => FramePacer.Smoothing, on => FramePacer.Smoothing = on)
                    with { Weighed = true, Needs = "FramePacer" }],
            Probe = () => Found(FramePacer.Patched, false)
        },
        new("GcIdle")
        {
            Install = c => GcIdle.Install(c.Harmony),
            Requires = ["FramePacer"],
            Knobs = [Switch("GcIdle", "misc", "garbage", () => GcIdle.Enabled, on => GcIdle.Enabled = on)],
            Probe = () => Found(GcIdle.Patched && FramePacer.Patched, false)
        },
        new("PointLights")
        {
            Install = c => PointLights.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("PointLights", "render", "drawing", () => PointLights.Enabled, on => PointLights.Enabled = on)],
            Probe = () => Found(PointLights.Patched && (PointLights.Injected || !PointLights.Enabled), false),
            Stop = _ => PointLights.Forget()
        },
        new("FrameScratch")
        {
            Replaces = FrameScratch.Seams,
            Install = c =>
            {
                FrameScratch.Install(c.Harmony, c.Logger);
                HandlerScratch.Install(c.Harmony);
                StringScratch.Install(c.Harmony);
            },
            Knobs = [Switch("FrameScratch", "misc", "garbage", () => FrameScratch.Enabled, on => FrameScratch.Enabled = on)],
            Probe = () => Found(FrameScratch.Patched == 2 || HandlerScratch.Rewritten > 0 || StringScratch.Rewritten > 0, false),
            Shapes = [new EngineBodies(FrameScratch.Seams, FrameScratch.Fingerprint)]
        }
    ];
}
