using System.Reflection;
using System.Runtime.Intrinsics;
using HarmonyLib;
using Komet.Host;
using Komet.Vulkan;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// Every feature in install order: KometModSystem installs, rechecks and stops them from here, and their knobs, in declaration order,
// form the knob table that komet-hud.json, the settings pages and the benchmark arms read (Knobs). Arms write
// the statics through Knobs.Write, never HudSettings, which persists what it sets. Stops run in reverse: PreJit's first, GraphicsMenu's
// before the HUD's window goes, TessWorkers' before the pool's.
internal static partial class Features
{
    // The seams of PotCulling and the map features once found: Seams() searches the loaded assemblies by name, and the probe runs on
    // every 250 ms poll
    private static MethodBase? _potSeam, _mapSeam, _mapSaveSeam, _mapUploadSeam;

    private static readonly Feature[] Table =
    [
        // before the HUD, whose settings may switch the mod times on
        new("ModTimes")
        {
            Install = c => ModTimes.Install(c.Harmony, c.Api.ModLoader, c.Logger),
            Probe = () => ModTimes.Replays ? Active : EngineChanged,
            Shapes =
            [
                new EngineBodies(() => [AccessTools.Method(typeof(ClientEventManager),
                    nameof(ClientEventManager.TriggerRenderStage))], ModTimes.Shape)
            ]
        },
        // the render cost and occlusion panels' measurements, which patch once their panel is first shown; before the HUD as well:
        // its settings may switch them on as it is built, and an Install after that would switch them off again
        new("RenderCost") { Install = c => RenderCost.Install(c.Harmony, c.Logger), Stop = _ => RenderCost.Clear() },
        new("Occlusion")
        {
            Install = c => Occlusion.Install(c.Harmony, c.Api, c.Logger),
            Stop = _ => Occlusion.Clear(),
            Probe = () => Occlusion.Detected && !Occlusion.Supported ? NotApplicable : Active
        },
        new("Hud")
        {
            Install = c => c.Overlay = new HudOverlay(c.Api),
            Stop = c =>
            {
                c?.Overlay?.Dispose();
                KometOptions.Clear(); // the mods' pages belong to the world that closes
            }
        },
        new("GraphicsMenu")
        {
            Install = c =>
            {
                if (NotNull(c.Overlay)) GraphicsMenu.Install(c.Harmony, c.Api, c.Overlay.Options, c.Logger);
            },
            Requires = ["Hud"],
            Knobs = [Switch("GraphicsMenu", "misc", "menu", () => GraphicsMenu.Enabled,
                on => GraphicsMenu.Enabled = on)],
            Stop = _ => GraphicsMenu.Clear(),
            Probe = () => Found(GraphicsMenu.Installed, GraphicsMenu.StoodDown),
            Shapes = [new EngineBodies(GraphicsMenu.Shaped, GraphicsMenu.Fingerprint)]
        },
        new("MenuBlur")
        {
            Knobs = [Switch("MenuBlur", "misc", "menu", () => Backdrop.Enabled, on => Backdrop.Enabled = on)]
        },
        new("ShaderUseCache")
        {
            Install = c => ShaderUseCache.Install(c.Harmony),
            Knobs = [Switch("ShaderUseCache", "render", "drawing", () => ShaderUseCache.Enabled,
                on => ShaderUseCache.Enabled = on)]
        },
        new("DistantShadows")
        {
            Install = c => DistantShadows.Install(c.Harmony, c.Logger),
            Knobs = [Switch("DistantShadows", "render", "drawing", () => DistantShadows.Enabled,
                on => DistantShadows.Enabled = on)],
            Probe = () => Found(DistantShadows.Matched && DistantShadows.Injected, DistantShadows.Failed, Failed),
            Shapes = [new EngineBodies(DistantShadows.Seams, DistantShadows.Fingerprint)],
            Stop = _ => DistantShadows.Forget()
        },
        new("FrustumSweep")
        {
            // the shadow passes' view too, which OcclusionCulling's rows read
            Install = c =>
            {
                FrustumSweep.Install(c.Harmony, c.Logger);
                ShadowView.Install(c.Harmony, c.Api, c.Logger);
            },
            Knobs =
            [
                Switch("FrustumSweep", "render", "culling", () => FrustumSweep.Enabled,
                    on => FrustumSweep.Enabled = on),
                Switch("FrustumStages", "render", "culling", () => FrustumSweep.Stages,
                    on => FrustumSweep.Stages = on),
                Switch("ShadowCasters", "render", "culling", () => FrustumSweep.Casters,
                    on => FrustumSweep.Casters = on)
            ]
        },
        new("IndirectDraw")
        {
            Install = c => IndirectDraw.Install(c.Harmony),
            Knobs = [Switch("IndirectDraw", "render", "drawing", () => IndirectDraw.Enabled,
                on => IndirectDraw.Enabled = on)],
            Probe = () => IndirectDraw.Detected && !IndirectDraw.Supported ? NotApplicable : Active
        },
        // after IndirectDraw, whose RenderMesh prefix hands it the draws
        new("OcclusionCulling")
        {
            Install = c => OcclusionCulling.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("OcclusionCulling", "render", "culling", () => OcclusionCulling.Enabled,
                    on => OcclusionCulling.Enabled = on),
                Switch("GpuTerrainCulling", "render", "culling", () => OcclusionCulling.RowsEnabled,
                    on => OcclusionCulling.RowsEnabled = on)],
            Stop = _ => OcclusionCulling.Clear(),
            Probe = () => (OcclusionCulling.Installed, OcclusionCulling.Detected && !OcclusionCulling.Supported) switch
            {
                (false, _) => EngineChanged,
                (_, true) => NotApplicable,
                _ => Active
            }
        },
        // before VulkanTerrain, whose GlTap then wraps its entry points
        new("GlQueryCache")
        {
            Install = _ => GlQueryCache.Install(),
            Knobs = [Switch("GlQueryCache", "render", "drawing", () => GlQueryCache.Enabled,
                on => GlQueryCache.Enabled = on)],
            Probe = () => GlQueryCache.Installed ? Active : NotApplicable
        },
        // the Vulkan device beside OpenGL, the ground for drawing with Vulkan; started on the first frame after the switch goes on
        new("VulkanCore")
        {
            Install = c => VulkanCore.Install(c.Api, c.Logger),
            Knobs = [Switch("VulkanCore", "vulkan", "vulkan-advanced", () => VulkanCore.Enabled,
                on => VulkanCore.Enabled = on)],
            Stop = _ => VulkanCore.Stop(),
            Probe = () => VulkanCore.Failed ? NotApplicable : Active
        },
        // the ported shaders, the hot uniforms the scene settled on and the driver's pipeline cache kept on disk for the next start:
        // a program seen before costs no shaderc and less of the driver's compiler on the render thread
        new("VulkanShaderCache")
        {
            Requires = ["VulkanCore"],
            Install = c => ShaderCache.Install(c.Logger),
            Knobs = [Switch("VulkanShaderCache", "vulkan", "vulkan-advanced", () => ShaderCache.Enabled,
                on => ShaderCache.Enabled = on)]
        },
        // the GPU's time per stage and kind of work in the Vulkan frame, from timestamps, in the report
        new("VulkanGpuTimes")
        {
            Requires = ["VulkanCore"],
            Knobs = [Switch("VulkanGpuTimes", "vulkan", "vulkan-advanced", () => VulkanFrame.Measuring,
                on => VulkanFrame.Measuring = on)]
        },
        // the frame drawn by Vulkan: the terrain pass by pass (liquid depth, shadow maps, opaque, transparent, water plants) and the
        // rest of the 3D frame part by part (sky, entities, particles, post-processing, the rest). The settings page sets them
        // together through one mode (KometPages.VulkanMode) and shows each below it
        new("VulkanTerrain")
        {
            Requires = ["VulkanCore"],
            Install = c => VulkanRenderer.Install(c.Harmony, c.Api, c.Logger),
            Shapes =
            [
                new EngineBodies(PoolUpdates.Seams, PoolUpdates.Fingerprint),
                new EngineBodies(VulkanRenderer.BirthSeams, VulkanRenderer.BirthFingerprint)
            ],
            Knobs = [Switch("VulkanTerrain", "vulkan", "vulkan-advanced", () => VulkanRenderer.Enabled,
                    on => VulkanRenderer.Enabled = on),
                Switch("VulkanOpaque", "vulkan", "vulkan-advanced", () => VulkanRenderer.Opaque,
                    on => VulkanRenderer.Opaque = on),
                Switch("VulkanShadows", "vulkan", "vulkan-advanced", () => VulkanRenderer.Shadows,
                    on => VulkanRenderer.Shadows = on),
                Switch("VulkanLiquidDepth", "vulkan", "vulkan-advanced", () => VulkanRenderer.LiquidDepth,
                    on => VulkanRenderer.LiquidDepth = on),
                Switch("VulkanTransparent", "vulkan", "vulkan-advanced", () => VulkanRenderer.Transparent,
                    on => VulkanRenderer.Transparent = on),
                Switch("VulkanWaterPlants", "vulkan", "vulkan-advanced", () => VulkanRenderer.WaterPlants,
                    on => VulkanRenderer.WaterPlants = on),
                Switch("VulkanScene", "vulkan", "vulkan-advanced", () => VulkanRenderer.Scene,
                    on => VulkanRenderer.Scene = on),
                Switch("VulkanSky", "vulkan", "vulkan-advanced", () => VulkanRenderer.Sky, on => VulkanRenderer.Sky = on),
                Switch("VulkanEntities", "vulkan", "vulkan-advanced", () => VulkanRenderer.Entities,
                    on => VulkanRenderer.Entities = on),
                Switch("VulkanParticles", "vulkan", "vulkan-advanced", () => VulkanRenderer.Particles,
                    on => VulkanRenderer.Particles = on),
                Switch("VulkanPost", "vulkan", "vulkan-advanced", () => VulkanRenderer.PostProcessing,
                    on => VulkanRenderer.PostProcessing = on),
                Switch("VulkanOther", "vulkan", "vulkan-advanced", () => VulkanRenderer.Other,
                    on => VulkanRenderer.Other = on),
                Switch("VulkanWindow", "vulkan", "vulkan-advanced", () => VulkanRenderer.Window,
                    on => VulkanRenderer.Window = on),
                Switch("VulkanPresent", "vulkan", "vulkan-advanced", () => VulkanRenderer.Present,
                    on => VulkanRenderer.Present = on),
                Switch("VulkanLazyHandoff", "vulkan", "vulkan-advanced", () => VulkanRenderer.LazyHandoff,
                    on => VulkanRenderer.LazyHandoff = on),
                Switch("VulkanOwnDepth", "vulkan", "vulkan-advanced", () => VulkanRenderer.OwnDepth,
                    on => VulkanRenderer.OwnDepth = on),
                Switch("VulkanPoolBirths", "vulkan", "vulkan-advanced", () => VulkanRenderer.Births,
                    on => VulkanRenderer.Births = on)],
            Stop = _ => VulkanRenderer.Stop(),
            Probe = () => VulkanRenderer.Unavailable ? NotApplicable : Active
        },
        new("SunOcclusion")
        {
            Install = c => SunOcclusion.Install(c.Harmony),
            Knobs = [Switch("SunOcclusion", "render", "culling", () => SunOcclusion.Enabled,
                on => SunOcclusion.Enabled = on)]
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
            Probe = () => (_potSeam ??= PotCulling.Seams()[0]) is null
                ? NotApplicable
                : Found(PotCulling.Matched, PotCulling.Blocked),
            Shapes = [new EngineBodies(PotCulling.Seams, PotCulling.Fingerprint)]
        },
        new("MapTileCulling")
        {
            Install = c => MapTileCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("MapTileCulling", "render", "culling", () => MapTileCulling.Enabled,
                on => MapTileCulling.Enabled = on)],
            Recheck = MapTileCulling.Recheck,
            Probe = () => (_mapSeam ??= MapTileCulling.Seams()[0]) is null
                ? NotApplicable
                : Found(MapTileCulling.Matched, MapTileCulling.Blocked),
            Shapes = [new EngineBodies(MapTileCulling.Seams, MapTileCulling.Fingerprint)]
        },
        new("MapTileUpload")
        {
            Install = c => MapTileUpload.Install(c.Harmony, c.Logger),
            Knobs = [Switch("MapTileUpload", "render", "upload", () => MapTileUpload.Enabled,
                on => MapTileUpload.Enabled = on)],
            Recheck = MapTileUpload.Recheck,
            Probe = () => (_mapUploadSeam ??= MapTileUpload.Seams()[0]) is null
                ? NotApplicable
                : Found(MapTileUpload.Matched, MapTileUpload.Blocked),
            Shapes = [new EngineBodies(MapTileUpload.Seams, MapTileUpload.Fingerprint)]
        },
        new("IdleAnimators")
        {
            Install = c => IdleAnimators.Install(c.Harmony, c.Logger),
            Knobs = [Switch("IdleAnimators", "render", "culling", () => IdleAnimators.Enabled,
                on => IdleAnimators.Enabled = on)],
            Recheck = IdleAnimators.Recheck,
            Stop = _ => IdleAnimators.Clear(),
            Probe = () => Found(IdleAnimators.Matched && IdleAnimators.Rewritten, IdleAnimators.Blocked),
            Shapes = [new EngineBodies(IdleAnimators.Seams, IdleAnimators.Fingerprint)]
        },
        new("WindowSizeCache")
        {
            Install = c => WindowSizeCache.Install(c.Harmony),
            Knobs = [Switch("WindowSizeCache", "render", "drawing", () => WindowSizeCache.Enabled,
                on => WindowSizeCache.Enabled = on)]
        },
        new("PoolScale")
        {
            Install = c => PoolScale.Install(c.Harmony),
            Knobs = [new("PoolScale", "render", "drawing", PoolScale.Engine, PoolScale.MaxScale, PoolScale.Engine,
                () => PoolScale.Scale, v => PoolScale.Scale = v) { Unit = "x" }],
            Probe = () => PoolScale.Rewritten ? Active : EngineChanged
        },
        new("GlErrorPoll")
        {
            Install = c => GlErrorPoll.Install(c.Harmony),
            Knobs = [Switch("GlErrorPoll", "render", "drawing", () => GlErrorPoll.Enabled,
                on => GlErrorPoll.Enabled = on)],
            Probe = () => GlErrorPoll.Rewritten ? Active : EngineChanged
        },
        new("ChunkLookup")
        {
            Install = c => ChunkLookup.Install(c.Harmony),
            Knobs = [Switch("ChunkLookup", "chunks", "arrival", () => ChunkLookup.Enabled,
                on => ChunkLookup.Enabled = on)],
            Stop = _ => ChunkLookup.Clear()
        },
        new("MeshPool")
        {
            Install = c => MeshPool.Install(c.Harmony),
            Knobs = [Switch("MeshPool", "render", "upload", () => MeshPool.Enabled, on => MeshPool.Enabled = on,
                "PoolFragments")]
        },
        new("MeshRecycle")
        {
            Install = c => MeshRecycle.Install(c.Harmony),
            Knobs = [Switch("MeshRecycle", "render", "upload", () => MeshRecycle.Enabled,
                on => MeshRecycle.Enabled = on)]
        },
        new("FacePacking")
        {
            Install = c => FacePacking.Install(c.Harmony),
            Knobs = [Switch("FacePacking", "chunks", "arrival", () => FacePacking.Enabled,
                on => FacePacking.Enabled = on)]
        },
        // the opaque and topsoil chunk meshes sorted by face direction, so the GPU culling leaves out what faces away
        new("FaceSorting")
        {
            Requires = ["FacePacking"],
            Install = c => FaceSorting.Install(c.Harmony),
            Knobs = [Switch("FaceSorting", "render", "culling", () => FaceSorting.Enabled,
                on => FaceSorting.Enabled = on),
                Switch("FaceSplits", "render", "culling", () => FaceSorting.Splitting, on => FaceSorting.Splitting = on)],
            Stop = _ => FaceSorting.Clear()
        },
        new("ClimateCache")
        {
            Install = c => ClimateCache.Install(c.Harmony),
            Knobs = [Switch("ClimateCache", "misc", "garbage", () => ClimateCache.Enabled,
                on => ClimateCache.Enabled = on)]
        },
        new("PartitionReuse")
        {
            Install = c => PartitionReuse.Install(c.Harmony),
            Knobs = [Switch("PartitionReuse", "misc", "garbage", () => PartitionReuse.Enabled,
                on => PartitionReuse.Enabled = on)],
            Probe = () => PartitionReuse.Rewritten ? Active : EngineChanged
        },
        new("HandlerLists")
        {
            Install = c => HandlerLists.Install(c.Harmony),
            Knobs = [Switch("HandlerLists", "misc", "garbage", () => HandlerLists.Enabled,
                on => HandlerLists.Enabled = on)],
            Probe = () => HandlerLists.Rewritten ? Active : EngineChanged,
            Stop = _ => HandlerLists.Clear()
        },
        new("CookingMatch")
        {
            Install = c => CookingMatch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("CookingMatch", "misc", "garbage", () => CookingMatch.Enabled,
                on => CookingMatch.Enabled = on)],
            Probe = () => CookingMatch.Matched && CookingMatch.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(CookingMatch.Seams, CookingMatch.Fingerprint)]
        },
        new("AnimationFrames")
        {
            Install = c => AnimationFrames.Install(c.Harmony, c.Logger),
            Knobs = [Switch("AnimationFrames", "misc", "entities", () => AnimationFrames.Enabled,
                on => AnimationFrames.Enabled = on)],
            Recheck = AnimationFrames.Recheck,
            Stop = _ => AnimationFrames.Clear(),
            Probe = () => Found(AnimationFrames.Matched, AnimationFrames.Blocked),
            Shapes = [new EngineBodies(AnimationFrames.Shaped, AnimationFrames.Fingerprint)]
        },
        new("InitOnce")
        {
            Install = c => InitOnce.Install(c.Harmony, c.Logger),
            Knobs = [Switch("InitOnce", "misc", "entities", () => InitOnce.Enabled, on => InitOnce.Enabled = on)],
            Recheck = InitOnce.Recheck,
            Probe = () => Found(InitOnce.Matched, InitOnce.Blocked),
            Shapes = [new EngineBodies(InitOnce.Shaped, InitOnce.Fingerprint)]
        },
        new("ShapeInitMemo")
        {
            Install = c => ShapeInitMemo.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ShapeInitMemo", "misc", "entities", () => ShapeInitMemo.Enabled,
                on => ShapeInitMemo.Enabled = on)],
            Recheck = ShapeInitMemo.Recheck,
            Probe = () => Found(ShapeInitMemo.Matched, ShapeInitMemo.Blocked),
            Shapes = [new EngineBodies(ShapeInitMemo.Shaped, ShapeInitMemo.Fingerprint)]
        },
        new("EntityTessBudget")
        {
            Install = c => EntityTessBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("EntityTessBudget", "misc", "entities", EntityTessBudget.Engine, EntityTessBudget.MaxMillis,
                EntityTessBudget.Engine, () => EntityTessBudget.Millis, v => EntityTessBudget.Millis = v)
                { Unit = "ms" }],
            Probe = () => EntityTessBudget.Substituted ? Active : EngineChanged
        },
        // the entity panel's times: each entity's tick, animation and render preparation less the GC pauses, while the HUD counts
        new("EntityTimes")
        {
            Install = c => EntityTimes.Install(c.Harmony),
            Probe = () => EntityTimes.Installed ? Active : EngineChanged
        },
        new("ChunkBudget")
        {
            Install = c => ChunkBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("UploadCap", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis,
                    ChunkBudget.Uncapped, () => ChunkBudget.CapMillis, v => ChunkBudget.CapMillis = v)
                    { Unit = "ms" },
                new("UploadCeiling", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis,
                    ChunkBudget.Uncapped, () => ChunkBudget.CeilingMillis, v => ChunkBudget.CeilingMillis = v)
                    { Unit = "ms" },
                Switch("UploadYield", "chunks", "arrival", () => ChunkBudget.Yield, on => ChunkBudget.Yield = on)],
            Probe = () => ChunkBudget.Capped ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkBudget.Seams, ChunkBudget.Fingerprint)]
        },
        new("ChunkLoadBudget")
        {
            Install = c => ChunkLoadBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("ChunkLoadBudget", "chunks", "arrival", ChunkLoadBudget.Engine, ChunkLoadBudget.MaxMillis,
                ChunkLoadBudget.Engine, () => ChunkLoadBudget.Millis, v => ChunkLoadBudget.Millis = v)
                { Unit = "ms" }],
            Probe = () => ChunkLoadBudget.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkLoadBudget.Seams, ChunkLoadBudget.Fingerprint)]
        },
        // a far chunk's block entities spread over frames on the arrival budget's clock, the queue held behind it
        new("BlockEntityBudget")
        {
            Install = c => BlockEntityBudget.Install(c.Harmony, c.Logger),
            Requires = ["ChunkLoadBudget"],
            Knobs = [Switch("BlockEntityBudget", "chunks", "blockentities", () => BlockEntityBudget.Enabled,
                on => BlockEntityBudget.Enabled = on)],
            Recheck = BlockEntityBudget.Recheck,
            Stop = _ => BlockEntityBudget.Leave(),
            Probe = () => Found(BlockEntityBudget.Matched && BlockEntityBudget.Rewritten && ChunkLoadBudget.Rewritten,
                BlockEntityBudget.Blocked),
            Shapes = [new EngineBodies(BlockEntityBudget.Seams, BlockEntityBudget.Fingerprint)]
        },
        new("EventBusSweep")
        {
            Install = c => EventBusSweep.Install(c.Harmony, c.Logger),
            Knobs = [Switch("EventBusSweep", "misc", "garbage", () => EventBusSweep.Enabled,
                on => EventBusSweep.Enabled = on)],
            Stop = _ => EventBusSweep.Clear(),
            Probe = () => EventBusSweep.Installed ? Active : EngineChanged,
            Shapes = [new EngineBodies(EventBusSweep.Seams, EventBusSweep.Fingerprint)]
        },
        // tick listener slots, renderers and event bus listeners placed without searching their whole list
        new("ListenerSlots")
        {
            Install = c => ListenerSlots.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ListenerSlots", "chunks", "blockentities", () => ListenerSlots.Enabled,
                on => ListenerSlots.Enabled = on)],
            Recheck = ListenerSlots.Recheck,
            Stop = _ => ListenerSlots.Clear(),
            Probe = () => Found(ListenerSlots.Matched, ListenerSlots.Blocked),
            Shapes =
            [
                new EngineBodies(ListenerSlots.TickSeams, ListenerSlots.TickFingerprint),
                new EngineBodies(ListenerSlots.RendererSeams, ListenerSlots.RendererFingerprint),
                new EngineBodies(ListenerSlots.BusSeams, ListenerSlots.BusFingerprint)
            ]
        },
        // the ground storage's ignitable JSON, the firepit's meshes, its pot shapes and render props, each worked out once
        new("BlockEntityCaches")
        {
            Install = c => BlockEntityCaches.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("BlockEntityCaches", "chunks", "blockentities", () => BlockEntityCaches.Enabled,
                on => BlockEntityCaches.Enabled = on)],
            Recheck = BlockEntityCaches.Recheck,
            Stop = _ => BlockEntityCaches.Clear(),
            Probe = () => BlockEntityCaches.Survival
                ? Found(BlockEntityCaches.Matched, BlockEntityCaches.Blocked)
                : NotApplicable,
            Shapes =
            [
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Ignitable),
                    BlockEntityCaches.IgnitableFingerprint),
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Meshes), BlockEntityCaches.MeshFingerprint),
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Pots), BlockEntityCaches.PotFingerprint)
            ]
        },
        new("ChunkThreadClosure") { Install = c => ChunkThreadClosure.Install(c.Harmony), ServerOnly = true },
        new("CloudTileScratch")
        {
            Install = c => CloudTileScratch.Install(c.Harmony),
            Knobs = [Switch("CloudTileScratch", null, null, () => CloudTileScratch.Enabled,
                on => CloudTileScratch.Enabled = on)]
        },
        new("DecompressScratch")
        {
            Install = c => DecompressScratch.Install(c.Harmony),
            Knobs = [Switch("DecompressScratch", "chunks", "arrival", () => DecompressScratch.Enabled,
                on => DecompressScratch.Enabled = on)]
        },
        new("ProtoListCache")
        {
            Install = c => ProtoListCache.Install(c.Harmony),
            Knobs = [Switch("ProtoListCache", "chunks", "arrival", () => ProtoListCache.Enabled,
                on => ProtoListCache.Enabled = on)],
            Probe = () => ProtoListCache.Patched ? Active : NotApplicable
        },
        new("BushShapes")
        {
            Install = c => BushShapes.Install(c.Harmony),
            Knobs = [Switch("BushShapes", "chunks", "tessellation", () => BushShapes.Enabled,
                on => BushShapes.Enabled = on)],
            Stop = _ => BushShapes.Clear(),
            Probe = () => BushShapes.Builder() is null ? NotApplicable : Found(BushShapes.Rewritten, false)
        },
        new("InsideTextures")
        {
            Install = c => InsideTextures.Install(c.Harmony),
            Knobs = [Switch("InsideTextures", "misc", "garbage", () => InsideTextures.Enabled,
                on => InsideTextures.Enabled = on)],
            Probe = () => InsideTextures.Target() is null ? NotApplicable : Found(InsideTextures.Rewritten, false)
        },
        new("ClutterMeshes")
        {
            Install = c => ClutterMeshes.Install(c.Harmony),
            Knobs = [Switch("ClutterMeshes", "misc", "garbage", () => ClutterMeshes.Enabled,
                on => ClutterMeshes.Enabled = on)],
            Probe = () => ClutterMeshes.Target() is null ? NotApplicable : Found(ClutterMeshes.Patched, false)
        },
        new("CullerRays")
        {
            Install = c => CullerRays.Install(c.Harmony, c.Logger),
            Knobs = [Switch("CullerRays", "render", "culling", () => CullerRays.Enabled, on => CullerRays.Enabled = on)],
            Recheck = CullerRays.Recheck,
            Probe = () => Found(CullerRays.Matched, CullerRays.Blocked),
            Shapes = [new EngineBodies(CullerRays.Seams, CullerRays.Fingerprint)]
        },
        new("MapReads")
        {
            Install = c => MapReads.Install(c.Harmony),
            Knobs = [Switch("MapReads", "chunks", "arrival", () => MapReads.Enabled, on => MapReads.Enabled = on)],
            Probe = () => MapReads.Target() is null ? NotApplicable : Found(MapReads.Rewritten, false)
        },
        new("Ascii85Text")
        {
            Install = c => Ascii85Text.Install(c.Harmony),
            Knobs = [Switch("Ascii85Text", "misc", "garbage", () => Ascii85Text.Enabled, on => Ascii85Text.Enabled = on)],
            Probe = () => Found(Ascii85Text.Patched, false)
        },
        new("TreeKeys")
        {
            Install = c => TreeKeys.Install(c.Harmony),
            Knobs = [Switch("TreeKeys", "chunks", "arrival", () => TreeKeys.Enabled, on => TreeKeys.Enabled = on)],
            Probe = () => TreeKeys.Rewritten ? Active : EngineChanged
        },
        new("MapPixels")
        {
            Install = c => MapPixels.Install(c.Harmony),
            Knobs = [Switch("MapPixels", "chunks", "arrival", () => MapPixels.Enabled, on => MapPixels.Enabled = on)],
            Probe = () => MapPixels.Rewritten ? Active : EngineChanged
        },
        new("MapSaveScratch")
        {
            Install = c => MapSaveScratch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("MapSaveScratch", "chunks", "arrival", () => MapSaveScratch.Enabled,
                on => MapSaveScratch.Enabled = on)],
            Recheck = MapSaveScratch.Recheck,
            Probe = () => (_mapSaveSeam ??= MapSaveScratch.Seams()[0]) is null
                ? NotApplicable
                : Found(MapSaveScratch.Matched, MapSaveScratch.Blocked),
            Shapes = [new EngineBodies(MapSaveScratch.Seams, MapSaveScratch.Fingerprint)]
        },
        new("LightScratch")
        {
            Install = c => LightScratch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("LightScratch", "chunks", "light", () => LightScratch.Enabled,
                on => LightScratch.Enabled = on)],
            Probe = () => LightScratch.StoodDown ? StoodDown : Active
        },
        new("LightRepair")
        {
            Install = c => LightRepair.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch("LightRepair", "chunks", "light", () => LightRepair.Enabled,
                on => LightRepair.Enabled = on)],
            Recheck = LightRepair.Recheck,
            Probe = () => Found(LightRepair.Matched, LightRepair.Failed, Failed),
            Shapes = [new EngineBodies(LightRepair.Seams, LightRepair.Fingerprint)]
        },
        new("ParticleLight")
        {
            Install = c => ParticleLight.Install(c.Harmony),
            Knobs = [Switch("ParticleLight", "chunks", "light", () => ParticleLight.Enabled,
                on => ParticleLight.Enabled = on)],
            Stop = _ => ParticleLight.Forget()
        },
        // worldgen
        new("ColumnNoiseScratch")
        {
            Install = c => ColumnNoiseScratch.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch("ColumnNoiseScratch", "misc", "garbage", () => ColumnNoiseScratch.Enabled,
                on => ColumnNoiseScratch.Enabled = on)],
            Probe = () => ColumnNoiseScratch.StoodDown ? StoodDown : Active
        },
        // before TessSchedule and TessWorkers, which run passes on the worker pool
        new("TessSafety")
        {
            Install = c => TessSafety.Install(c.Harmony, c.Logger),
            Unpatched = TessSafety.Clear, // after the unpatch: the engine's palette table is the one read again
            Probe = () => (TessSafety.Broken, TessSafety.Installed) switch
            {
                (true, _) => Failed,
                (_, false) => EngineChanged,
                _ => Active
            }
        },
        new("TessSeams") { Install = c => TessSeams.Install(c.Harmony, c.Logger) },
        new("ExtendedRows")
        {
            Install = c => ExtendedRows.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ExtendedRows", "chunks", "tessellation", () => ExtendedRows.Enabled,
                on => ExtendedRows.Enabled = on)],
            Shapes = [new EngineBodies(ExtendedRows.Shaped, ExtendedRows.Shape)]
        },
        new("VisibleFaces")
        {
            Install = c => VisibleFaces.Install(c.Harmony, c.Logger),
            Knobs = [Switch("VisibleFaces", "chunks", "tessellation", () => VisibleFaces.Enabled,
                on => VisibleFaces.Enabled = on)],
            Shapes = [new EngineBodies(VisibleFaces.Shaped, VisibleFaces.Shape)]
        },
        new("FaceLight")
        {
            Install = c => FaceLight.Install(c.Harmony, c.Logger),
            Knobs = [Switch("FaceLight", "chunks", "tessellation", () => FaceLight.Enabled,
                on => FaceLight.Enabled = on)],
            Probe = () => Vector128.IsHardwareAccelerated ? Active : NotApplicable,
            Shapes =
            [
                new EngineBodies(FaceLight.Shaped, FaceLight.Shape),
                new EngineBodies(FaceLight.FusedShaped, FaceLight.FusedShape)
            ]
        },
        // Komet's tesselators for cubes, layers, topsoil, crosses and plain JSON blocks (leaves and plants among them) in the engine's
        // slots, the engine's kept for every other block
        new("OwnTessellation")
        {
            Install = c => OwnTessellation.Install(c.Harmony, c.Logger, c.Api.World as ClientMain),
            Knobs = [Switch("OwnTessellation", "chunks", "tessellation", () => OwnTessellation.Enabled,
                on => OwnTessellation.Enabled = on)],
            Stop = _ => OwnTessellation.Installed = false,
            Probe = () => Found(OwnTessellation.Installed && OwnTessellation.JsonInstalled,
                OwnTessellation.StoodDown || OwnTessellation.JsonStoodDown),
            Shapes =
            [
                new EngineBodies(OwnTessellation.Shaped, OwnTessellation.Shape),
                new EngineBodies(OwnTessellation.JsonShaped, OwnTessellation.JsonShape)
            ]
        },
        new("TessBlockPos")
        {
            Install = c => TessBlockPos.Install(c.Harmony),
            Knobs = [Switch("TessBlockPos", "misc", "garbage", () => TessBlockPos.Enabled,
                on => TessBlockPos.Enabled = on)],
            Probe = () => TessBlockPos.Rewritten ? Active : EngineChanged
        },
        // small chunk parts from the mesh recycler, the decor dictionary, ocean corners and upload lists reused
        new("PartRecycling")
        {
            Install = c => PartRecycling.Install(c.Harmony, c.Logger),
            Knobs = [Switch("PartRecycling", "chunks", "tessellation", () => PartRecycling.Enabled,
                on => PartRecycling.Enabled = on)],
            Recheck = PartRecycling.Recheck,
            Probe = () => Found(PartRecycling.Rewritten, PartRecycling.StoodDown),
            Shapes = [new EngineBodies(PartRecycling.Shaped, PartRecycling.Shape)]
        },
        new("TessAccounting") { Install = c => TessAccounting.Install(c.Harmony), Stop = _ => TessAccounting.Clear() },
        new("TessSchedule")
        {
            Install = c => TessSchedule.Install(c.Harmony),
            Requires = ["TessSafety"],
            Knobs = [Switch("TessSchedule", "chunks", "tessellation", () => TessSchedule.Enabled,
                on => TessSchedule.Enabled = on, "NearFirst")],
            Stop = _ => TessSchedule.Clear()
        },
        new("WorkerPool")
        {
            Knobs = [new("WorkerThreads", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => WorkerPool.Wanted,
                v => WorkerPool.Wanted = v)],
            Stop = _ => StopPool(),
            Probe = () => WorkerPool.FrameOff || WorkerPool.BackgroundOff ? Failed : Active
        },
        // only with TessSafety and TessSchedule installed completely
        new("TessWorkers")
        {
            Install = c => TessWorkers.Install(c.Harmony),
            Requires = ["TessSafety", "TessSchedule", "WorkerPool"],
            Knobs = [new("TessPriority", "chunks", "threads", 0, 100, 0, () => TessWorkers.Priority,
                v => TessWorkers.Priority = v) { Unit = "%" }],
            Stop = _ => TessWorkers.Stop()
        },
        // the large tessellation methods optimized from their first call instead of quick-jitted for a world's first seconds
        new("JitWarm")
        {
            Install = c => JitWarm.Install(c.Harmony, c.Logger),
            Knobs = [Switch("JitWarm", "chunks", "tessellation", () => JitWarm.Enabled, on => JitWarm.Enabled = on)]
        },
        new("OccludedChunks")
        {
            Install = c => OccludedChunks.Install(c.Harmony, c.Logger),
            Knobs = [Switch("OccludedChunks", "chunks", "tessellation", () => OccludedChunks.Enabled,
                on => OccludedChunks.Enabled = on)],
            Shapes = [new EngineBodies(OccludedChunks.Shaped, OccludedChunks.Shape)]
        },
        new("GcLatency")
        {
            Install = _ => GcLatency.Install(),
            Knobs = [Switch("GcLatency", "misc", "garbage", () => GcLatency.Enabled, on => GcLatency.Enabled = on)],
            Stop = _ => GcLatency.Stop()
        },
        // the singleplayer world's server in a process of its own: the patch also goes in from the in-process server's StartPre (HostSystem)
        new("ServerProcess")
        {
            Install = _ => HostLaunch.Install(),
            Knobs = [Switch("ServerProcess", "misc", "server", () => HostLaunch.Enabled, on => HostLaunch.Enabled = on)]
        },
        // creature items and wearables parse their shapes without the animations when their inventory mesh is built
        new("InventoryShapes")
        {
            Install = c => InventoryShapes.Install(c.Harmony),
            Knobs = [Switch("InventoryShapes", "misc", "garbage", () => InventoryShapes.Enabled,
                on => InventoryShapes.Enabled = on)],
            Probe = () => InventoryShapes.Method("Vintagestory.GameContent.ItemCreature", "CreateOverlaidMeshRef") is null
                ? NotApplicable
                : Found(InventoryShapes.Creatures && InventoryShapes.Wearables, false)
        },
        // garbage on the integrated server: sunlight flood positions, rock weights, code parts, chunk request lists
        new("WorldGenScratch")
        {
            Install = c => WorldGenScratch.Install(c.Harmony),
            ServerOnly = true,
            Knobs = [Switch("WorldGenScratch", "misc", "garbage", () => WorldGenScratch.Enabled, on => WorldGenScratch.Enabled = on)],
            Probe = () => WorldGenScratch.Rewritten == WorldGenScratch.AllBits ? Active : StoodDown
        },
        // the creative inventory draws items it never drew before within a time budget a frame, the rest a few frames later
        new("IconBudget")
        {
            Install = c => IconBudget.Install(c.Harmony),
            Knobs = [Switch("IconBudget", "misc", "garbage", () => IconBudget.Enabled, on => IconBudget.Enabled = on)],
            Probe = () => Found(IconBudget.Patched, false)
        },
        // the weather's region walk under its writers' lock: a join no longer crashes on "Collection was modified"
        new("WeatherLock")
        {
            Install = c => WeatherLock.Install(c.Harmony),
            Probe = () => WeatherLock.Target() is null ? NotApplicable : Found(WeatherLock.Patched, false)
        },
        new("FrameClock") { Install = c => FrameClock.Install(c.Harmony) },
        // a main thread that finishes no frame for 10 s: logged, with every thread's stack where dotnet-stack is installed
        new("HangWatch") { Install = c => HangWatch.Start(c.Logger), Stop = _ => HangWatch.Stop() },
        // inert unless KOMET_BENCH names a bench.json
        new("Benchmark") { Install = c => Benchmark.Install(c.Api), Stage = FeatureStage.Tail },
        // last: every patch above is in place and left alone
        new("PreJit")
        {
            Install = c => PreJit.Start(c.Logger),
            Stage = FeatureStage.Last,
            Knobs = [Switch("PreJit", "misc", "garbage", () => PreJit.Enabled, on => PreJit.Enabled = on)],
            Stop = _ => PreJit.Stop()
        }
    ];

    // Every thread ends; one still inside a job after WorkerPool.JoinMs fails the first assertion
    private static void StopPool() => _ = Assert(WorkerPool.Stop()) && Assert(WorkerPool.Running == 0);

    private static FeatureState Found(bool matched, bool blocked, FeatureState blockedAs = StoodDown) =>
        (matched, blocked) switch
        {
            (false, _) => EngineChanged,
            (_, true) => blockedAs,
            _ => Active
        };

    private static Knob Switch(string key, string? page, string? group, Func<bool> get, Action<bool> set,
        string? json = null) =>
        Assert(key.Length > 0) && NotNull(get) && NotNull(set)
            ? new Knob(key, page, group, 0, 1, 0, () => get() ? 1 : 0, value => set(value != 0), json)
            : throw new ArgumentException("knob without a name", nameof(key));
}
