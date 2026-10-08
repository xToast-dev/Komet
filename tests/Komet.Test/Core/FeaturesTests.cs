using Komet.Host;
using Komet.Vulkan;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Core;

// The built-in table pins what KometModSystem hardcoded before it: the install order, the knob table in dialog order (komet-hud.json,
// the settings pages and bench.json read it) and the stops.
[NonParallelizable]
public sealed class FeaturesTests
{
    private static readonly string[] InstallOrder =
    [
        "ModTimes", "RenderCost", "Occlusion", "Hud", "GraphicsMenu", "MenuBlur", "ShaderUseCache", "DistantShadows",
        "FrustumSweep", "IndirectDraw", "OcclusionCulling", "GlQueryCache", "VulkanCore", "VulkanShaderCache", "VulkanGpuTimes",
        "VulkanTerrain", "SunOcclusion", "AnimatableCulling", "PotCulling", "MapTileCulling", "MapTileUpload",
        "IdleAnimators", "WindowSizeCache", "PoolScale", "GlErrorPoll", "ChunkLookup", "MeshPool", "MeshRecycle",
        "FacePacking", "FaceSorting", "ClimateCache", "PartitionReuse", "HandlerLists", "CookingMatch", "AnimationFrames", "InitOnce",
        "ShapeInitMemo", "EntityTessBudget", "EntityTimes", "ChunkBudget", "ChunkLoadBudget", "BlockEntityBudget", "EventBusSweep",
        "ListenerSlots", "BlockEntityCaches", "ChunkThreadClosure", "CloudTileScratch",
        "DecompressScratch", "ProtoListCache", "BushShapes", "InsideTextures", "ClutterMeshes", "CullerRays", "MapReads", "Ascii85Text", "TreeKeys", "MapPixels", "MapSaveScratch", "LightScratch", "LightRepair", "ParticleLight",
        "ColumnNoiseScratch", "TessSafety", "TessSeams", "ExtendedRows", "VisibleFaces", "FaceLight", "OwnTessellation",
        "TessBlockPos", "PartRecycling",
        "TessAccounting", "TessSchedule", "WorkerPool", "TessWorkers", "JitWarm", "OccludedChunks", "GcLatency", "ServerProcess", "InventoryShapes", "WorldGenScratch", "IconBudget", "WeatherLock", "FrameClock", "HangWatch", "Benchmark",
        "PreJit"
    ];

    // Key, komet-hud.json name, page/group, range and engine value, owner
    private static readonly string[] KnobTable =
    [
        "GraphicsMenu GraphicsMenu misc/menu 0..1 engine 0 GraphicsMenu",
        "MenuBlur MenuBlur misc/menu 0..1 engine 0 MenuBlur",
        "ShaderUseCache ShaderUseCache render/drawing 0..1 engine 0 ShaderUseCache",
        "DistantShadows DistantShadows render/drawing 0..1 engine 0 DistantShadows",
        "FrustumSweep FrustumSweep render/culling 0..1 engine 0 FrustumSweep",
        "FrustumStages FrustumStages render/culling 0..1 engine 0 FrustumSweep",
        "ShadowCasters ShadowCasters render/culling 0..1 engine 0 FrustumSweep",
        "IndirectDraw IndirectDraw render/drawing 0..1 engine 0 IndirectDraw",
        "OcclusionCulling OcclusionCulling render/culling 0..1 engine 0 OcclusionCulling",
        "GpuTerrainCulling GpuTerrainCulling render/culling 0..1 engine 0 OcclusionCulling",
        "GlQueryCache GlQueryCache render/drawing 0..1 engine 0 GlQueryCache",
        "VulkanCore VulkanCore vulkan/vulkan-advanced 0..1 engine 0 VulkanCore",
        "VulkanShaderCache VulkanShaderCache vulkan/vulkan-advanced 0..1 engine 0 VulkanShaderCache",
        "VulkanGpuTimes VulkanGpuTimes vulkan/vulkan-advanced 0..1 engine 0 VulkanGpuTimes",
        "VulkanTerrain VulkanTerrain vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanOpaque VulkanOpaque vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanShadows VulkanShadows vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanLiquidDepth VulkanLiquidDepth vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanTransparent VulkanTransparent vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanWaterPlants VulkanWaterPlants vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanScene VulkanScene vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanSky VulkanSky vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanEntities VulkanEntities vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanParticles VulkanParticles vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanPost VulkanPost vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanOther VulkanOther vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanWindow VulkanWindow vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanPresent VulkanPresent vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanLazyHandoff VulkanLazyHandoff vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanOwnDepth VulkanOwnDepth vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "VulkanPoolBirths VulkanPoolBirths vulkan/vulkan-advanced 0..1 engine 0 VulkanTerrain",
        "SunOcclusion SunOcclusion render/culling 0..1 engine 0 SunOcclusion",
        "AnimatableCulling AnimatableCulling render/culling 0..1 engine 0 AnimatableCulling",
        "PotCulling PotCulling render/culling 0..1 engine 0 PotCulling",
        "MapTileCulling MapTileCulling render/culling 0..1 engine 0 MapTileCulling",
        "MapTileUpload MapTileUpload render/upload 0..1 engine 0 MapTileUpload",
        "IdleAnimators IdleAnimators render/culling 0..1 engine 0 IdleAnimators",
        "WindowSizeCache WindowSizeCache render/drawing 0..1 engine 0 WindowSizeCache",
        "PoolScale PoolScale render/drawing 1..8x engine 1 PoolScale",
        "GlErrorPoll GlErrorPoll render/drawing 0..1 engine 0 GlErrorPoll",
        "ChunkLookup ChunkLookup chunks/arrival 0..1 engine 0 ChunkLookup",
        "MeshPool PoolFragments render/upload 0..1 engine 0 MeshPool",
        "MeshRecycle MeshRecycle render/upload 0..1 engine 0 MeshRecycle",
        "FacePacking FacePacking chunks/arrival 0..1 engine 0 FacePacking",
        "FaceSorting FaceSorting render/culling 0..1 engine 0 FaceSorting",
        "FaceSplits FaceSplits render/culling 0..1 engine 0 FaceSorting",
        "ClimateCache ClimateCache misc/garbage 0..1 engine 0 ClimateCache",
        "PartitionReuse PartitionReuse misc/garbage 0..1 engine 0 PartitionReuse",
        "HandlerLists HandlerLists misc/garbage 0..1 engine 0 HandlerLists",
        "CookingMatch CookingMatch misc/garbage 0..1 engine 0 CookingMatch",
        "AnimationFrames AnimationFrames misc/entities 0..1 engine 0 AnimationFrames",
        "InitOnce InitOnce misc/entities 0..1 engine 0 InitOnce",
        "ShapeInitMemo ShapeInitMemo misc/entities 0..1 engine 0 ShapeInitMemo",
        "EntityTessBudget EntityTessBudget misc/entities 0..50ms engine 0 EntityTessBudget",
        "UploadCap UploadCap render/upload 0..20ms engine 0 ChunkBudget",
        "UploadCeiling UploadCeiling render/upload 0..20ms engine 0 ChunkBudget",
        "UploadYield UploadYield chunks/arrival 0..1 engine 0 ChunkBudget",
        "ChunkLoadBudget ChunkLoadBudget chunks/arrival 0..20ms engine 0 ChunkLoadBudget",
        "BlockEntityBudget BlockEntityBudget chunks/blockentities 0..1 engine 0 BlockEntityBudget",
        "EventBusSweep EventBusSweep misc/garbage 0..1 engine 0 EventBusSweep",
        "ListenerSlots ListenerSlots chunks/blockentities 0..1 engine 0 ListenerSlots",
        "BlockEntityCaches BlockEntityCaches chunks/blockentities 0..1 engine 0 BlockEntityCaches",
        "CloudTileScratch CloudTileScratch -/- 0..1 engine 0 CloudTileScratch",
        "DecompressScratch DecompressScratch chunks/arrival 0..1 engine 0 DecompressScratch",
        "ProtoListCache ProtoListCache chunks/arrival 0..1 engine 0 ProtoListCache",
        "BushShapes BushShapes chunks/tessellation 0..1 engine 0 BushShapes",
        "InsideTextures InsideTextures misc/garbage 0..1 engine 0 InsideTextures",
        "ClutterMeshes ClutterMeshes misc/garbage 0..1 engine 0 ClutterMeshes",
        "CullerRays CullerRays render/culling 0..1 engine 0 CullerRays",
        "MapReads MapReads chunks/arrival 0..1 engine 0 MapReads",
        "Ascii85Text Ascii85Text misc/garbage 0..1 engine 0 Ascii85Text",
        "TreeKeys TreeKeys chunks/arrival 0..1 engine 0 TreeKeys",
        "MapPixels MapPixels chunks/arrival 0..1 engine 0 MapPixels",
        "MapSaveScratch MapSaveScratch chunks/arrival 0..1 engine 0 MapSaveScratch",
        "LightScratch LightScratch chunks/light 0..1 engine 0 LightScratch",
        "LightRepair LightRepair chunks/light 0..1 engine 0 LightRepair",
        "ParticleLight ParticleLight chunks/light 0..1 engine 0 ParticleLight",
        "ColumnNoiseScratch ColumnNoiseScratch misc/garbage 0..1 engine 0 ColumnNoiseScratch",
        "ExtendedRows ExtendedRows chunks/tessellation 0..1 engine 0 ExtendedRows",
        "VisibleFaces VisibleFaces chunks/tessellation 0..1 engine 0 VisibleFaces",
        "FaceLight FaceLight chunks/tessellation 0..1 engine 0 FaceLight",
        "OwnTessellation OwnTessellation chunks/tessellation 0..1 engine 0 OwnTessellation",
        "TessBlockPos TessBlockPos misc/garbage 0..1 engine 0 TessBlockPos",
        "PartRecycling PartRecycling chunks/tessellation 0..1 engine 0 PartRecycling",
        "TessSchedule NearFirst chunks/tessellation 0..1 engine 0 TessSchedule",
        "WorkerThreads WorkerThreads chunks/threads 0..8 engine 0 WorkerPool",
        "TessPriority TessPriority chunks/threads 0..100% engine 0 TessWorkers",
        "JitWarm JitWarm chunks/tessellation 0..1 engine 0 JitWarm",
        "OccludedChunks OccludedChunks chunks/tessellation 0..1 engine 0 OccludedChunks",
        "GcLatency GcLatency misc/garbage 0..1 engine 0 GcLatency",
        "ServerProcess ServerProcess misc/server 0..1 engine 0 ServerProcess",
        "InventoryShapes InventoryShapes misc/garbage 0..1 engine 0 InventoryShapes",
        "WorldGenScratch WorldGenScratch misc/garbage 0..1 engine 0 WorldGenScratch",
        "IconBudget IconBudget misc/garbage 0..1 engine 0 IconBudget",
        "PreJit PreJit misc/garbage 0..1 engine 0 PreJit"
    ];

    // Each knob's static, read without the table
    private static readonly Dictionary<string, Func<int>> Statics = new()
    {
        ["FrustumSweep"] = () => On(FrustumSweep.Enabled), ["SunOcclusion"] = () => On(SunOcclusion.Enabled),
        ["ShaderUseCache"] = () => On(ShaderUseCache.Enabled), ["IndirectDraw"] = () => On(IndirectDraw.Enabled),
        ["WindowSizeCache"] = () => On(WindowSizeCache.Enabled), ["MeshPool"] = () => On(MeshPool.Enabled),
        ["MeshRecycle"] = () => On(MeshRecycle.Enabled), ["FacePacking"] = () => On(FacePacking.Enabled),
        ["VulkanWindow"] = () => On(VulkanRenderer.Window),
        ["VulkanPresent"] = () => On(VulkanRenderer.Present), ["VulkanShaderCache"] = () => On(ShaderCache.Enabled),
        ["PotCulling"] = () => On(PotCulling.Enabled), ["VulkanLazyHandoff"] = () => On(VulkanRenderer.LazyHandoff),
        ["GpuTerrainCulling"] = () => On(OcclusionCulling.RowsEnabled),
        ["VulkanGpuTimes"] = () => On(VulkanFrame.Measuring), ["FaceSorting"] = () => On(FaceSorting.Enabled),
        ["VulkanOwnDepth"] = () => On(VulkanRenderer.OwnDepth), ["MapTileCulling"] = () => On(MapTileCulling.Enabled),
        ["MapSaveScratch"] = () => On(MapSaveScratch.Enabled), ["MapTileUpload"] = () => On(MapTileUpload.Enabled),
        ["GlQueryCache"] = () => On(GlQueryCache.Enabled),
        ["ProtoListCache"] = () => On(ProtoListCache.Enabled), ["BushShapes"] = () => On(BushShapes.Enabled),
        ["InsideTextures"] = () => On(InsideTextures.Enabled),
        ["ClutterMeshes"] = () => On(ClutterMeshes.Enabled), ["CullerRays"] = () => On(CullerRays.Enabled),
        ["MapReads"] = () => On(MapReads.Enabled), ["Ascii85Text"] = () => On(Ascii85Text.Enabled),
        ["TreeKeys"] = () => On(TreeKeys.Enabled), ["MapPixels"] = () => On(MapPixels.Enabled),
        ["ChunkLoadBudget"] = () => ChunkLoadBudget.Millis, ["EventBusSweep"] = () => On(EventBusSweep.Enabled),
        ["BlockEntityBudget"] = () => On(BlockEntityBudget.Enabled), ["BlockEntityCaches"] = () => On(BlockEntityCaches.Enabled),
        ["ListenerSlots"] = () => On(ListenerSlots.Enabled),
        ["FaceSplits"] = () => On(FaceSorting.Splitting), ["UploadCeiling"] = () => ChunkBudget.CeilingMillis,
        ["VulkanPoolBirths"] = () => On(VulkanRenderer.Births), ["GcLatency"] = () => On(GcLatency.Enabled),
        ["InventoryShapes"] = () => On(InventoryShapes.Enabled), ["ServerProcess"] = () => On(HostLaunch.Enabled),
        ["WorldGenScratch"] = () => On(WorldGenScratch.Enabled),
        ["ShadowCasters"] = () => On(FrustumSweep.Casters), ["UploadYield"] = () => On(ChunkBudget.Yield),
        ["IconBudget"] = () => On(IconBudget.Enabled),
        ["UploadCap"] = () => ChunkBudget.CapMillis, ["ChunkLookup"] = () => On(ChunkLookup.Enabled),
        ["DecompressScratch"] = () => On(DecompressScratch.Enabled), ["TessSchedule"] = () => On(TessSchedule.Enabled),
        ["ExtendedRows"] = () => On(ExtendedRows.Enabled), ["VisibleFaces"] = () => On(VisibleFaces.Enabled),
        ["FaceLight"] = () => On(FaceLight.Enabled), ["OccludedChunks"] = () => On(OccludedChunks.Enabled), ["JitWarm"] = () => On(JitWarm.Enabled),
        ["OwnTessellation"] = () => On(OwnTessellation.Enabled), ["PartRecycling"] = () => On(PartRecycling.Enabled),
        ["WorkerThreads"] = () => WorkerPool.Wanted, ["TessPriority"] = () => TessWorkers.Priority,
        ["LightScratch"] = () => On(LightScratch.Enabled), ["ParticleLight"] = () => On(ParticleLight.Enabled),
        ["AnimationFrames"] = () => On(AnimationFrames.Enabled), ["InitOnce"] = () => On(InitOnce.Enabled),
        ["ShapeInitMemo"] = () => On(ShapeInitMemo.Enabled), ["EntityTessBudget"] = () => EntityTessBudget.Millis,
        ["ClimateCache"] = () => On(ClimateCache.Enabled),
        ["ColumnNoiseScratch"] = () => On(ColumnNoiseScratch.Enabled),
        ["CloudTileScratch"] = () => On(CloudTileScratch.Enabled), ["PreJit"] = () => On(PreJit.Enabled),
        ["GraphicsMenu"] = () => On(GraphicsMenu.Enabled), ["MenuBlur"] = () => On(Backdrop.Enabled),
        ["PartitionReuse"] = () => On(PartitionReuse.Enabled),
        ["AnimatableCulling"] = () => On(AnimatableCulling.Enabled), ["TessBlockPos"] = () => On(TessBlockPos.Enabled),
        ["FrustumStages"] = () => On(FrustumSweep.Stages), ["GlErrorPoll"] = () => On(GlErrorPoll.Enabled),
        ["IdleAnimators"] = () => On(IdleAnimators.Enabled), ["CookingMatch"] = () => On(CookingMatch.Enabled),
        ["HandlerLists"] = () => On(HandlerLists.Enabled), ["PoolScale"] = () => PoolScale.Scale,
        ["LightRepair"] = () => On(LightRepair.Enabled), ["DistantShadows"] = () => On(DistantShadows.Enabled),
        ["OcclusionCulling"] = () => On(OcclusionCulling.Enabled), ["VulkanCore"] = () => On(VulkanCore.Enabled),
        ["VulkanTerrain"] = () => On(VulkanRenderer.Enabled), ["VulkanOpaque"] = () => On(VulkanRenderer.Opaque),
        ["VulkanShadows"] = () => On(VulkanRenderer.Shadows),
        ["VulkanLiquidDepth"] = () => On(VulkanRenderer.LiquidDepth),
        ["VulkanTransparent"] = () => On(VulkanRenderer.Transparent),
        ["VulkanWaterPlants"] = () => On(VulkanRenderer.WaterPlants), ["VulkanScene"] = () => On(VulkanRenderer.Scene),
        ["VulkanSky"] = () => On(VulkanRenderer.Sky), ["VulkanEntities"] = () => On(VulkanRenderer.Entities),
        ["VulkanParticles"] = () => On(VulkanRenderer.Particles),
        ["VulkanPost"] = () => On(VulkanRenderer.PostProcessing), ["VulkanOther"] = () => On(VulkanRenderer.Other)
    };

    // komet-hud.json as Komet 1.x wrote it: the display settings, then every knob under its saved name, a switch as a bool
    private static readonly string[] SettingsFile =
    [
        "Visible:Boolean", "Corner:String", "Opacity:Float", "Interval:Float", "BenchSeconds:Float", "ShowFps:Boolean",
        "ShowLows:Boolean", "ShowFrametime:Boolean", "ShowGraph:Boolean", "ShowMods:Boolean", "ShowPins:Boolean",
        "ToastMs:Float", "PinnedMods:Array", "WindowTab:String", "WindowX:String", "WindowY:String", "DebugLanguage:String",
        "UpdateCheck:Boolean", "UpdateAsked:Boolean", "FontScale:Float", "ShowAdvanced:Boolean", "GraphicsMenu:Boolean",
        "MenuBlur:Boolean", "ShaderUseCache:Boolean", "DistantShadows:Boolean", "FrustumSweep:Boolean",
        "FrustumStages:Boolean", "ShadowCasters:Boolean", "IndirectDraw:Boolean", "OcclusionCulling:Boolean",
        "GpuTerrainCulling:Boolean", "GlQueryCache:Boolean", "VulkanCore:Boolean", "VulkanShaderCache:Boolean",
        "VulkanGpuTimes:Boolean", "VulkanTerrain:Boolean", "VulkanOpaque:Boolean", "VulkanShadows:Boolean",
        "VulkanLiquidDepth:Boolean", "VulkanTransparent:Boolean", "VulkanWaterPlants:Boolean", "VulkanScene:Boolean",
        "VulkanSky:Boolean", "VulkanEntities:Boolean", "VulkanParticles:Boolean", "VulkanPost:Boolean",
        "VulkanOther:Boolean", "VulkanWindow:Boolean", "VulkanPresent:Boolean", "VulkanLazyHandoff:Boolean",
        "VulkanOwnDepth:Boolean", "VulkanPoolBirths:Boolean", "SunOcclusion:Boolean", "AnimatableCulling:Boolean",
        "PotCulling:Boolean", "MapTileCulling:Boolean", "MapTileUpload:Boolean", "IdleAnimators:Boolean",
        "WindowSizeCache:Boolean", "PoolScale:Integer", "GlErrorPoll:Boolean", "ChunkLookup:Boolean",
        "PoolFragments:Boolean", "MeshRecycle:Boolean", "FacePacking:Boolean", "FaceSorting:Boolean", "FaceSplits:Boolean",
        "ClimateCache:Boolean", "PartitionReuse:Boolean", "HandlerLists:Boolean", "CookingMatch:Boolean",
        "AnimationFrames:Boolean", "InitOnce:Boolean", "ShapeInitMemo:Boolean", "EntityTessBudget:Integer",
        "UploadCap:Integer", "UploadCeiling:Integer", "UploadYield:Boolean", "ChunkLoadBudget:Integer",
        "BlockEntityBudget:Boolean", "EventBusSweep:Boolean", "ListenerSlots:Boolean", "BlockEntityCaches:Boolean",
        "CloudTileScratch:Boolean", "DecompressScratch:Boolean", "ProtoListCache:Boolean", "BushShapes:Boolean",
        "InsideTextures:Boolean", "ClutterMeshes:Boolean", "CullerRays:Boolean", "MapReads:Boolean", "Ascii85Text:Boolean",
        "TreeKeys:Boolean", "MapPixels:Boolean", "MapSaveScratch:Boolean", "LightScratch:Boolean", "LightRepair:Boolean",
        "ParticleLight:Boolean", "ColumnNoiseScratch:Boolean", "ExtendedRows:Boolean", "VisibleFaces:Boolean",
        "FaceLight:Boolean", "OwnTessellation:Boolean", "TessBlockPos:Boolean", "PartRecycling:Boolean",
        "NearFirst:Boolean", "WorkerThreads:Integer", "TessPriority:Integer", "JitWarm:Boolean", "OccludedChunks:Boolean",
        "GcLatency:Boolean", "ServerProcess:Boolean", "InventoryShapes:Boolean", "WorldGenScratch:Boolean",
        "IconBudget:Boolean", "PreJit:Boolean"
    ];

    private static readonly string[] Stops =
    [
        "PreJit", "HangWatch", "GcLatency", "TessWorkers", "WorkerPool", "TessSchedule", "TessAccounting", "OwnTessellation",
        "ParticleLight", "BushShapes", "BlockEntityCaches", "ListenerSlots", "EventBusSweep", "BlockEntityBudget", "AnimationFrames",
        "HandlerLists", "FaceSorting", "ChunkLookup", "IdleAnimators", "VulkanTerrain", "VulkanCore", "OcclusionCulling",
        "DistantShadows", "GraphicsMenu", "Hud", "Occlusion", "RenderCost"
    ];

    private static readonly string[] ServerOnly = ["ChunkThreadClosure", "LightRepair", "ColumnNoiseScratch", "WorldGenScratch"],
        Tail = ["Benchmark"], Last = ["PreJit"], Unpatched = ["TessSafety"];

    private static readonly string[] Shaped =
    [
        "ModTimes", "GraphicsMenu", "DistantShadows", "VulkanTerrain", "VulkanTerrain", "AnimatableCulling", "PotCulling",
        "MapTileCulling", "MapTileUpload", "IdleAnimators", "CookingMatch", "AnimationFrames", "InitOnce",
        "ShapeInitMemo", "ChunkBudget", "ChunkLoadBudget", "BlockEntityBudget", "EventBusSweep", "ListenerSlots", "ListenerSlots",
        "ListenerSlots", "BlockEntityCaches", "BlockEntityCaches", "BlockEntityCaches", "CullerRays", "MapSaveScratch",
        "LightRepair", "ExtendedRows", "VisibleFaces", "FaceLight", "FaceLight", "OwnTessellation", "OwnTessellation", "PartRecycling",
        "OccludedChunks"
    ];

    [OneTimeSetUp]
    public void LoadALanguage() => GameLang.EnsureLoaded();

    [TearDown]
    public void Close() => Features.Close();

    private static int On(bool on) => on ? 1 : 0;

    private static Feature[] All() => Features.All.ToArray();

    [Test]
    public void TheFeaturesInstallInTheirOrder()
    {
        Assert.That(All().Select(f => f.Id), Is.EqualTo(InstallOrder));
    }

    // The HUD switches these on while it is built, from komet-hud.json; each one's Install clears it, so it has to come first
    [TestCase("ModTimes")]
    [TestCase("RenderCost")]
    [TestCase("Occlusion")]
    public void WhatTheHudSwitchesOnInstallsBeforeIt(string id)
    {
        Assert.That(Array.IndexOf(InstallOrder, id), Is.LessThan(Array.IndexOf(InstallOrder, "Hud")));
    }

    [Test]
    public void EveryRequiredFeatureComesEarlier()
    {
        var ids = All().Select(f => f.Id).ToList();
        var requires = All().SelectMany((f, i) => f.Requires.Select(r => (f.Id, Required: r, At: i))).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(requires, Is.Not.Empty);
            foreach (var (id, required, at) in requires)
                Assert.That(ids.IndexOf(required), Is.InRange(0, at - 1), $"{id} requires {required}");
        });
    }

    // Server code only where the server runs; the bench after every feature it measures, PreJit after the HUD's renderers
    [Test]
    public void ServerFeaturesAndTheLateStagesAreFew()
    {
        var all = All();
        Assert.Multiple(() =>
        {
            Assert.That(all.Where(f => f.ServerOnly).Select(f => f.Id), Is.EqualTo(ServerOnly));
            Assert.That(all.Where(f => f.Stage == FeatureStage.Tail).Select(f => f.Id), Is.EqualTo(Tail));
            Assert.That(all.Where(f => f.Stage == FeatureStage.Last).Select(f => f.Id), Is.EqualTo(Last));
            Assert.That(all.Select(f => f.Stage), Is.Ordered, "the stages install in table order");
        });
    }

    [Test]
    public void TheKnobTableKeepsItsOrderNamesAndRanges()
    {
        var knobs = Knobs.BuiltIn.ToArray().Select(k => string.Create(CultureInfo.InvariantCulture,
            $"{k.Key} {k.Persisted} {k.Page ?? "-"}/{k.Group ?? "-"} {k.Min}..{k.Max}{k.Unit}") +
            string.Create(CultureInfo.InvariantCulture, $" engine {k.Engine} {All()[k.Owner].Id}"));
        Assert.Multiple(() =>
        {
            Assert.That(knobs, Is.EqualTo(KnobTable));
            Assert.That(Knobs.BuiltIn.ToArray().Select(k => k.Key).Distinct().Count(), Is.EqualTo(Knobs.BuiltInCount));
            Assert.That(Knobs.Count, Is.EqualTo(Knobs.BuiltInCount));
        });
    }

    // Stops run in reverse install order before UnpatchAll: PreJit's first, TessWorkers' before the pool's, GraphicsMenu's before
    // the HUD's window goes; TessSafety clears after the unpatch
    [Test]
    public void TheStopsRunInReverseInstallOrder()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enumerable.Reverse(All()).Where(f => f.Stop is not null).Select(f => f.Id), Is.EqualTo(Stops));
            Assert.That(All().Where(f => f.Unpatched is not null).Select(f => f.Id), Is.EqualTo(Unpatched));
        });
    }

    [Test]
    public void AFeatureIsFoundByItsIdOrItsKnob()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Features.Find("UploadCap"), Is.EqualTo(Features.Find("ChunkBudget")).And.GreaterThan(0));
            Assert.That(Features.Find("MenuBlur"), Is.EqualTo(Array.IndexOf(InstallOrder, "MenuBlur")));
            Assert.That(Features.Find("Nothing"), Is.EqualTo(-1));
        });
    }

    // Each feature pins the bodies it reproduces, skips or replays to those of Vintage Story 1.22.7: a game update that fails here
    // needs the feature's proof checked again or its golden tests re-run (the rebuilt graphics tabs looked at again: new controls,
    // other handlers) and the constant renewed
    [TestCaseSource(nameof(Fingerprints))]
    public void TheFingerprintIsThatOfTheInstalledEngine(string id, int index)
    {
        var bodies = All()[Features.Find(id)].Shapes[index];
        var shape = EngineShape.Of(bodies.Methods());
        Assert.That(shape, Is.EqualTo(bodies.Expected), $"{id} changed: 0x{shape:X16}UL");
    }

    private static IEnumerable<TestCaseData> Fingerprints() =>
        All().SelectMany(f => f.Shapes.Select((_, i) =>
            new TestCaseData(f.Id, i).SetArgDisplayNames(i == 0 ? f.Id : f.Id + i)));

    [Test]
    public void EveryPinnedFeatureIsTested()
    {
        Assert.That(All().SelectMany(f => f.Shapes.Select(_ => f.Id)), Is.EqualTo(Shaped));
    }

    // Write reaches the knob's own static, and only a difference is written
    [TestCaseSource(nameof(KnobKeys))]
    public void AWriteReachesTheKnobsStatic(string key)
    {
        var (index, read) = (Knobs.Find(key), Statics[key]);
        var knob = Knobs.At(index);
        var before = knob.Get();
        var other = before == knob.Engine ? Other(knob) : knob.Engine;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(read(), Is.EqualTo(before));
                Assert.That(Knobs.Write(index, before), Is.False, "the same value is not written");
                Assert.That(Knobs.Write(index, other), Is.True);
                Assert.That((knob.Get(), read(), Knobs.Snapshot()[index]), Is.EqualTo((other, other, other)));
            });
        }
        finally
        {
            _ = Knobs.Write(index, before);
        }
    }

    private static IEnumerable<string> KnobKeys() => KnobTable.Select(line => line.Split(' ')[0]);

    // A value that is not the engine's
    private static int Other(Knob knob) => knob.Engine == knob.Max ? knob.Min : knob.Max;

    // Two holders: the knobs stay at the engine's values until the second lets go, a write meanwhile is what comes back, and the
    // settings still show what was wanted
    [TestCaseSource(nameof(Holdable))]
    public void AHoldKeepsTheEngineUntilTheLastRelease(string id)
    {
        var feature = Features.Find(id);
        var knobs = All()[feature].Knobs.Select(k => Knobs.Find(k.Key)).ToArray();
        var before = Knobs.Snapshot();
        try
        {
            foreach (var k in knobs) _ = Knobs.Write(k, Other(Knobs.At(k)));
            Assert.That((Features.Hold(feature), Features.Hold(feature)), Is.EqualTo((true, true)));
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)));
                Assert.That(knobs.Select(k => Knobs.Snapshot()[k]), Is.EqualTo(knobs.Select(k => Other(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Not.Null);
            });
            foreach (var k in knobs)
            {
                Assert.That(Knobs.Write(k, Knobs.At(k).Engine), Is.False);
                Assert.That(Knobs.Snapshot()[k], Is.EqualTo(Knobs.At(k).Engine), "recorded");
                Assert.That(Knobs.Write(k, Later(Knobs.At(k))), Is.False);
            }

            Assert.That(Features.Release(feature), Is.True);
            Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Knobs.At(k).Engine)),
                "one holds");
            Assert.That(Features.Release(feature), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(k => Knobs.At(k).Get()), Is.EqualTo(knobs.Select(k => Later(Knobs.At(k)))));
                Assert.That(knobs.Select(Features.LockText), Has.All.Null);
                Assert.That(Features.Release(feature), Is.False, "nothing left to release");
            });
        }
        finally
        {
            Features.Close();
            _ = Knobs.Apply(before);
        }
    }

    private static IEnumerable<string> Holdable() => All().Where(f => f.Knobs.Length > 0).Select(f => f.Id);

    // Another value than the engine's, below the first one where the range has room
    private static int Later(Knob knob)
    {
        var other = Other(knob);
        return other - 1 > knob.Engine ? other - 1 : other;
    }

    [Test]
    public void AFeatureWithoutAKnobCannotBeHeld()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Features.Hold(Features.Find("TessSafety")), Is.False);
            Assert.That(Features.HoldsText(), Is.EqualTo("none"));
        });
    }

    [Test]
    public void ClosingEndsTheHolds()
    {
        var (feature, knob) = (Features.Find("FrustumSweep"), Knobs.Find("FrustumSweep"));
        Assert.That(FrustumSweep.Enabled, Is.True);
        _ = Features.Hold(feature);
        _ = Features.Hold(feature);
        Assert.Multiple(() =>
        {
            Assert.That((FrustumSweep.Enabled, Features.Held(knob)), Is.EqualTo((false, true)));
            Assert.That(Features.HoldsText(), Is.EqualTo("FrustumSweep (2)"));
        });
        Features.Close();
        Assert.That((FrustumSweep.Enabled, Features.Held(knob), Features.HoldsText()),
            Is.EqualTo((true, false, "none")));
    }

    // The settings page shows the player's value, dimmed, with the reason above the hint
    [Test]
    public void AHeldKnobsRowIsLocked()
    {
        var pages = new KometPages(new HudSettings { ShowAdvanced = true }, () => { }, _ => { }, () => { }, () => { }, () => { })
            .Build();
        var render = pages.Single(p => p.Id == "komet-render");
        var row = Enumerable.Range(0, render.Count).Select(i => render[i])
            .Single(r => r.Label == KometPages.T("frustumsweep"));
        Assert.That((row.IsEnabled, row.Locked?.Invoke()), Is.EqualTo((true, (string?)null)));
        _ = Features.Hold(Features.Find("FrustumSweep"));
        Assert.Multiple(() =>
        {
            Assert.That(row.IsEnabled, Is.False);
            Assert.That(row.Locked?.Invoke(), Is.EqualTo(HudText.Translate("settings-held")));
            Assert.That(row.Get(), Is.EqualTo(1), "the player's value");
        });
    }

    // An installed feature at its knob's engine value is off, held it is held off; the HUD lists what is not active
    [Test]
    public void TheHudListsWhatIsNotActive()
    {
        using var rig = new FeatureRig("MenuBlur");
        var before = Backdrop.Enabled;
        try
        {
            Backdrop.Enabled = true;
            Features.Poll();
            var active = Features.NotActive;
            Assert.That((rig.State, Features.StateOf(rig.Feature)), Is.EqualTo((FeatureState.Active, FeatureState.Active)));
            Backdrop.Enabled = false;
            Features.Poll();
            Assert.That((rig.State, Features.NotActive), Is.EqualTo((FeatureState.Off, active + 1)));
            _ = Features.Hold(rig.Feature);
            Features.Poll();
            Assert.That((Features.StateOf(rig.Feature), Features.FirstNotActive().Length > 0), Is.EqualTo((FeatureState.HeldOff, true)));
        }
        finally
        {
            Features.Close();
            Backdrop.Enabled = before;
        }
    }

    // A transpiler of another mod on the body the feature replaces stands it down at the recheck, and it comes back once that is gone
    [TestCase("AnimationFrames")]
    [TestCase("InitOnce")]
    [TestCase("ShapeInitMemo")]
    [TestCase("AnimatableCulling")]
    [TestCase("IdleAnimators")]
    public void AProbeSeesAnotherModOnTheFirstSeam(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        using var rig = new FeatureRig(id);
        Assert.That(rig.State, Is.EqualTo(FeatureState.Active));
        var seam = All()[rig.Feature].Shapes[0].Methods()[0];
        using (var foreign = new TestHarmony("komet-test-feature-foreign"))
        {
            _ = foreign.Patch(seam, transpiler: Foreign.Transpiler);
            Features.Recheck();
            Assert.That(rig.State, Is.EqualTo(FeatureState.StoodDown));
        }

        Features.Recheck();
        Assert.That(rig.State, Is.EqualTo(FeatureState.Active));
    }

    [Test]
    public void AServerFeatureDoesNotApplyOnARemoteServer()
    {
        using var harmony = new TestHarmony("komet-test-feature-remote");
        var feature = Features.Find("ColumnNoiseScratch");
        Features.InstallAt(new FeatureContext(harmony, null!, new QuietLogger(), false), feature);
        Assert.Multiple(() =>
        {
            Assert.That(Features.Evaluate(feature), Is.EqualTo(FeatureState.NotApplicable));
            Assert.That(harmony.GetPatchedMethods(), Is.Empty);
        });
    }

    // Every knob under the name it is saved as, a switch as a bool; a file read back and saved again is the same file
    [Test]
    public void TheSettingsFileKeepsItsKeys()
    {
        var json = JsonConvert.SerializeObject(new HudSettings());
        var loaded = JsonConvert.DeserializeObject<HudSettings>(json)!;
        var logger = new CapturingLogger();
        loaded.ApplyKnobs(logger);
        Assert.Multiple(() =>
        {
            Assert.That(JObject.Parse(json).Properties().Select(p => p.Name + ":" + p.Value.Type),
                Is.EqualTo(SettingsFile));
            Assert.That(JsonConvert.SerializeObject(loaded), Is.EqualTo(json));
            Assert.That(logger.Lines, Is.Empty);
        });
    }
}
