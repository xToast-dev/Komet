using Komet.Api;
using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Hud;
using Komet.Options;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Komet.Vulkan;
using Komet.World;

namespace Komet;

// The one place that names Komet's modules: which feature lists make up the mod, in which order their features install,
// and the few links that run from a lower module up to a higher one. A module project never names a project above it
// (the compiler refuses the reference); what it needs from above it takes through a hook that Wire sets here.
//
// To remove a module: delete its project, its line in All, its feature ids in InstallOrder and its lines in Wire; the
// compiler then names every other place that still used it.
internal static class Modules
{
    // One list a module, each in its own install order
    private static readonly Feature[][] All =
    [
        DiagnosticsFeatures.All, HudFeatures.All, OptionsFeatures.All, RenderingFeatures.All, VulkanFeatures.All, ChunkFeatures.All,
        WorldFeatures.All, ShapeFeatures.All, TessellationFeatures.All, CoreFeatures.All
    ];

    // The install order across the modules, which is also the knobs' order in komet.json and on the settings pages. A feature
    // installs after those it Requires; ModTimes and RenderCost before the HUD, whose settings switch them on; IndirectDraw
    // before OcclusionCulling, whose draws its RenderMesh prefix hands over; GlQueryCache before VulkanTerrain, whose GlTap wraps
    // its entry points; TessSafety before TessSchedule and TessWorkers.
    private static readonly string[] InstallOrder =
    [
        "ModTimes", "RenderCost", "Hud", "GraphicsMenu", "MenuBlur", "ShaderUseCache", "DistantShadows", "LeanVaryings",
        "SharpUpscale", "LeanGodRays", "LeanPost", "ModShaders", "FrustumSweep", "FarShadowReuse", "NearShadowReuse", "EntityCasters",
        "IndirectDraw", "OcclusionCulling", "GlQueryCache", "VulkanCore", "VulkanShaderCache", "VulkanGpuTimes",
        "TerrainRebuild", "PoolTrim", "VulkanTerrain", "AnimatableCulling",
        "PotCulling", "HearthCulling", "HelpCompose", "MapTileCulling", "MapTileUpload", "IdleAnimators", "PoolScale", "WindowSize", "ChunkLookup", "MeshRecycle", "FacePacking", "FaceSorting", "ClimateCache",
        "PartitionReuse", "DirtyBlocks", "CookingMatch", "AlloyMatch", "AnimationFrames", "ShapeInitMemo", "HeldItemsLast", "EntityTimes", "ChunkBudget", "ChunkLoadBudget", "JoinPacer", "ChunkPacking",
        "EventBusSweep", "ListenerSlots", "BlockEntityCaches", "ChunkThreadClosure",
        "CloudTileScratch", "CloudUploads", "DecompressScratch", "DirectChunks", "BushShapes", "MapPixels",
        "TreeKeys", "MapPieceBudget", "MapSaveScratch", "LightScratch", "LightRepair", "ParticleLight", "RelightRedraws",
        "ColumnNoiseScratch", "TessSafety", "TessSeams", "NeighbourReads", "FaceLight", "OwnTessellation", "TessAccounting", "TessSchedule", "WorkerPool", "TessWorkers", "JitWarm",
        "OccludedChunks", "DeepCulling", "FarLod", "FarFoliage", "BackgroundPriority", "GcLatency", "InventoryShapes",
        "WorldGenScratch", "IconBudget", "SnowPeek", "TickScratch", "SoundScratch", "ProfilerPool", "WeatherLock", "FramePacer", "GcIdle", "PointLights", "FrameScratch", "FrameClock", "HangWatch"
    ];

    private static bool _wired;

    // Before anything reads the feature table: the mod systems' StartPre, the tests' set-up
    public static void Register()
    {
        Features.Use(All, InstallOrder);
        Wire();
    }

    // The links upward, each a hook a lower module declares and a higher one fills:
    private static void Wire()
    {
        if (_wired) return;
        _wired = true;
        // Core asks: the shader module behind the public shader API, and who draws
        KometShaders.Backend = ShaderBackend.Instance;
        KometShaders.VulkanDraws = static () => VulkanRenderer.GlIdle;

        // Diagnostics asks Rendering, Tessellation and Vulkan
        Diagnostics.Links.SkipIdle = IdleAnimators.Skip;
        Diagnostics.Links.SkipRenderer = IdleAnimators.TrySkip;
        Diagnostics.Links.FrameBegun = TessGovernor.Frame;
        Diagnostics.Links.VulkanFrameGpuMs = static () => VulkanRenderer.FrameGpuMs;
        Diagnostics.Links.VulkanTimesTerrain = static () => VulkanRenderer.TimesTerrain;
        Diagnostics.Links.VulkanTerrainGpuMs = VulkanRenderer.TerrainGpuMs;
        Diagnostics.Links.ThreadsReport = VulkanWatch.Threads;

        // Rendering asks Vulkan
        Rendering.Links.Tapped = static () => GlTap.Tapped;
        Rendering.Links.TapVersion = static () => GlTap.Version;
        Rendering.Links.TapState = GlTap.View;
        Rendering.Links.Mark = static step => VulkanWatch.Mark(step);
        Rendering.Links.Paced = VulkanRenderer.Paced;
        Rendering.Links.Rebuilding = VulkanRenderer.Rebuilding;
        Rendering.Links.ReadPort = VulkanPorts.Read;

        // Rendering asks Chunks, Tessellation and the Ui
        Rendering.Links.LoadingBacklogged = static () => TessSeams.Backlogged;
        Rendering.Links.SpareChunks = ChunkPacking.Spares;
        Rendering.Links.ReloadShaders = EngineOptions.ReloadShaders;
        Rendering.Links.ModStarted = ModAdapters.Started;
        Rendering.Links.ModStopped = ModAdapters.Stopped;
        Rendering.Links.ShadersHooked = ShaderOverview.Hook;
        Rendering.Links.ShadersUnhooked = ShaderOverview.Unhook;
        Rendering.Links.ShadersLoaded = ShaderOverview.Build;
    }
}
