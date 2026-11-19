using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using static Komet.Api.KapiFeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.World;

// World-side garbage and lookups: MapTileCulling, MapTileUpload (the map tiles the GL side draws), ClimateCache,
// PartitionReuse, CookingMatch, AlloyMatch, JsonValues, EventBusSweep, ListenerSlots, BlockEntityCaches, CloudTileScratch,
// TreeKeys, MapPieceBudget, MapSaveScratch, LightScratch, LightRepair, ParticleLight, RelightRedraws,
// ColumnNoiseScratch, WorldGenScratch, SnowPeek, TickScratch, SoundScratch, SoundsOnDemand, TickerCommit and
// WeatherLock.
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class WorldFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget MapSeam = new(static () => MapTileCulling.Seams()[0]),
        MapUploadSeam = new(static () => MapTileUpload.Seams()[0]),
        MapSaveSeam = new(static () => MapSaveScratch.Seams()[0]),
        MapPixelsTarget = new(MapPixels.Target),
        WeatherTarget = new(WeatherLock.Target),
        CloudTarget = new(CloudUploads.Target);

    internal static readonly Feature[] All =
    [
        new("MapTileCulling")
        {
            Order = 310,
            Install = c => MapTileCulling.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("MapTileCulling", "render", "culling", () => MapTileCulling.Enabled,
                    on => MapTileCulling.Enabled = on)
            ],
            Recheck = MapTileCulling.Recheck,
            Probe = () => MapSeam.Missing
                ? NotApplicable
                : Found(MapTileCulling.Matched, MapTileCulling.Blocked),
            Shapes = [new EngineBodies(MapTileCulling.Seams, MapTileCulling.Fingerprint, MapTileCulling.Fingerprint123)]
        },
        new("MapTileUpload")
        {
            Order = 320,
            Install = c => MapTileUpload.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("MapTileUpload", "render", "upload", () => MapTileUpload.Enabled,
                    on => MapTileUpload.Enabled = on)
            ],
            Recheck = MapTileUpload.Recheck,
            Probe = () => MapUploadSeam.Missing
                ? NotApplicable
                : Found(MapTileUpload.Matched, MapTileUpload.Blocked),
            Shapes = [new EngineBodies(MapTileUpload.Seams, MapTileUpload.Fingerprint)]
        },
        new("ClimateCache")
        {
            Order = 400,
            Replaces = () =>
            [
                AccessTools.Method(typeof(ClientWorldMap), nameof(ClientWorldMap.TryLoadLerpedClimateMap),
                    [typeof(int), typeof(int)])
            ],
            Install = c => ClimateCache.Install(c.Harmony),
            Knobs =
            [
                Switch("ClimateCache", "misc", "garbage", () => ClimateCache.Enabled,
                    on => ClimateCache.Enabled = on)
            ],
            Stop = _ => ClimateCache.Forget()
        },
        new("PartitionReuse")
        {
            Order = 410,
            Replaces = () => PartitionReuse.Replaced,
            Rivals = KapiPatchKinds.Body,
            Install = c => PartitionReuse.Install(c.Harmony),
            Knobs =
            [
                Switch("PartitionReuse", "misc", "garbage", () => PartitionReuse.Enabled,
                    on => PartitionReuse.Enabled = on)
            ],
            Probe = () => PartitionReuse.Rewritten ? Active : EngineChanged
        },
        new("DirtyBlocks")
        {
            Order = 420,
            Install = c => DirtyBlocks.Install(c.Harmony),
            Knobs =
            [
                Switch("DirtyBlocks", "chunks", "arrival", () => DirtyBlocks.Enabled, on => DirtyBlocks.Enabled = on)
            ],
            Probe = () => DirtyBlocks.Installed ? Active : EngineChanged
        },
        new("CookingMatch")
        {
            Order = 430,
            Replaces = CookingMatch.Seams,
            Rivals = KapiPatchKinds.Body,
            Install = c => CookingMatch.Install(c.Harmony, c.Logger),
            Recheck = CookingMatch.Recheck,
            Knobs =
            [
                Switch("CookingMatch", "misc", "garbage", () => CookingMatch.Enabled,
                    on => CookingMatch.Enabled = on)
            ],
            Probe = () => CookingMatch.Matched && CookingMatch.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(CookingMatch.Seams, CookingMatch.Fingerprint, CookingMatch.Fingerprint123)]
        },
        new("AlloyMatch")
        {
            Order = 440,
            Install = c => AlloyMatch.Install(c.Harmony, c.Logger),
            Knobs = [Switch("AlloyMatch", "misc", "garbage", () => AlloyMatch.Enabled, on => AlloyMatch.Enabled = on)],
            Recheck = AlloyMatch.Recheck,
            Probe = () => Found(AlloyMatch.Matched && AlloyMatch.Ready, AlloyMatch.Blocked),
            Shapes = [new EngineBodies(AlloyMatch.Seams, AlloyMatch.Fingerprint)]
        },
        // equal attribute values of every block, item and entity type are one object once the level is final
        new("JsonValues")
        {
            Order = 540,
            Install = c => JsonValues.Install(c.Api, c.Logger),
            Knobs = [Switch("JsonValues", "misc", "garbage", () => JsonValues.Enabled, on => JsonValues.Enabled = on)],
            Stop = _ => JsonValues.Stop(),
            Probe = () => JsonValues.Installed ? Active : EngineChanged
        },
        new("EventBusSweep")
        {
            Order = 550,
            Install = c => EventBusSweep.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("EventBusSweep", "misc", "garbage", () => EventBusSweep.Enabled,
                    on => EventBusSweep.Enabled = on)
            ],
            Stop = _ => EventBusSweep.Clear(),
            Probe = () => EventBusSweep.Installed ? Active : EngineChanged,
            Shapes = [new EngineBodies(EventBusSweep.Seams, EventBusSweep.Fingerprint)]
        },
        new("ListenerSlots")
        {
            Order = 560,
            Install = c => ListenerSlots.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("ListenerSlots", "chunks", "blockentities", () => ListenerSlots.Enabled,
                    on => ListenerSlots.Enabled = on)
            ],
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
        new("BlockEntityCaches")
        {
            Order = 570,
            Install = c => BlockEntityCaches.Install(c.Harmony, c.Api, c.Logger),
            Knobs =
            [
                Switch("BlockEntityCaches", "chunks", "blockentities", () => BlockEntityCaches.Enabled,
                    on => BlockEntityCaches.Enabled = on)
            ],
            Recheck = BlockEntityCaches.Recheck,
            Stop = _ => BlockEntityCaches.Clear(),
            Probe = () => BlockEntityCaches.Survival
                ? Found(BlockEntityCaches.Matched, BlockEntityCaches.Blocked)
                : NotApplicable,
            Shapes =
            [
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Ignitable),
                    BlockEntityCaches.IgnitableFingerprint, BlockEntityCaches.IgnitableFingerprint123),
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Meshes),
                    BlockEntityCaches.MeshFingerprint, BlockEntityCaches.MeshFingerprint123),
                new EngineBodies(() => BlockEntityCaches.Seams(BlockEntityCaches.Pots),
                    BlockEntityCaches.PotFingerprint, BlockEntityCaches.PotFingerprint123)
            ]
        },
        new("CloudTileScratch")
        {
            Order = 590,
            Replaces = () => CloudTileScratch.Replaced,
            Rivals = KapiPatchKinds.Body,
            Install = c => CloudTileScratch.Install(c.Harmony),
            Knobs =
            [
                Switch("CloudTileScratch", "misc", "garbage", () => CloudTileScratch.Enabled,
                    on => CloudTileScratch.Enabled = on)
            ],
            Probe = () => CloudTileScratch.Rewritten ? Active : EngineChanged
        },
        new("CloudUploads")
        {
            Order = 600,
            Install = c => CloudUploads.Install(c.Harmony),
            Knobs =
            [
                Switch("CloudUploads", "render", "upload", () => CloudUploads.Enabled, on => CloudUploads.Enabled = on)
            ],
            Probe = () => CloudTarget.Missing ? NotApplicable : Found(CloudUploads.Rewritten, false)
        },
        new("MapPixels")
        {
            Order = 650,
            Install = c => MapPixels.Install(c.Harmony),
            Knobs = [Switch("MapPixels", "misc", "garbage", () => MapPixels.Enabled, on => MapPixels.Enabled = on)],
            Stop = _ => MapPixels.Clear(),
            Probe = () => MapPixelsTarget.Missing ? NotApplicable : Found(MapPixels.Rewritten, false)
        },
        new("TreeKeys")
        {
            Order = 660,
            Replaces = () =>
            [
                AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes), [typeof(BinaryReader)]),
                AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.GetString),
                    [typeof(string), typeof(string)])
            ],
            Rivals = KapiPatchKinds.Body,
            Install = c => TreeKeys.Install(c.Harmony),
            Knobs = [Switch("TreeKeys", "chunks", "arrival", () => TreeKeys.Enabled, on => TreeKeys.Enabled = on)],
            Probe = () => TreeKeys.Rewritten || TreeKeys.Reads ? Active : EngineChanged
        },
        new("MapPieceBudget")
        {
            Order = 670,
            Replaces = () => AccessTools.TypeByName("Vintagestory.GameContent.ChunkMapLayer") is { } layer
                ? [AccessTools.DeclaredMethod(layer, "OnTick", [typeof(float)])]
                : [],
            Rivals = KapiPatchKinds.Body,
            Install = c => MapPieceBudget.Install(c.Harmony),
            Knobs =
            [
                Switch("MapPieceBudget", "chunks", "arrival", () => MapPieceBudget.Enabled,
                    on => MapPieceBudget.Enabled = on)
            ],
            Probe = () => MapPieceBudget.Rewritten ? Active : EngineChanged
        },
        new("MapSaveScratch")
        {
            Order = 680,
            Install = c => MapSaveScratch.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("MapSaveScratch", "chunks", "arrival", () => MapSaveScratch.Enabled,
                    on => MapSaveScratch.Enabled = on)
            ],
            Recheck = MapSaveScratch.Recheck,
            Probe = () => MapSaveSeam.Missing
                ? NotApplicable
                : Found(MapSaveScratch.Matched, MapSaveScratch.Blocked),
            Shapes = [new EngineBodies(MapSaveScratch.Seams, MapSaveScratch.Fingerprint)]
        },
        new("LightScratch")
        {
            Order = 690,
            Install = c => LightScratch.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("LightScratch", "chunks", "light", () => LightScratch.Enabled,
                    on => LightScratch.Enabled = on)
            ],
            Probe = () => (LightScratch.StoodDown, LightScratch.Rewritten) switch
            {
                (true, _) => StoodDown,
                (_, true) => Active,
                _ => EngineChanged
            }
        },
        new("LightRepair")
        {
            Order = 700,
            Install = c => LightRepair.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs =
            [
                Switch("LightRepair", "chunks", "light", () => LightRepair.Enabled,
                        on => LightRepair.Enabled = on) with
                    {
                        Weighed = true
                    }
            ],
            Recheck = LightRepair.Recheck,
            Probe = () => Found(LightRepair.Matched, LightRepair.Failed, Failed),
            Shapes = [new EngineBodies(LightRepair.Seams, LightRepair.Fingerprint, LightRepair.Fingerprint123)]
        },
        new("ParticleLight")
        {
            Order = 710,
            Replaces = () =>
            [
                AccessTools.Method(typeof(BlockAccessorReadLockfree),
                    nameof(BlockAccessorReadLockfree.GetLightRGBsAsInt), [typeof(int), typeof(int), typeof(int)])
            ],
            Install = c => ParticleLight.Install(c.Harmony),
            Knobs =
            [
                Switch("ParticleLight", "chunks", "light", () => ParticleLight.Enabled,
                    on => ParticleLight.Enabled = on)
            ],
            Stop = _ => ParticleLight.Forget(),
            Probe = () => ParticleLight.Installed ? Active : EngineChanged
        },
        new("RelightRedraws")
        {
            Order = 720,
            Replaces = () => [RelightRedraws.Target()],
            Install = c => RelightRedraws.Install(c.Harmony),
            Knobs =
            [
                Switch("RelightRedraws", "chunks", "light", () => RelightRedraws.Enabled,
                    on => RelightRedraws.Enabled = on)
            ],
            Probe = () => RelightRedraws.Installed ? Active : EngineChanged
        },
        new("ColumnNoiseScratch")
        {
            Order = 730,
            Install = c => ColumnNoiseScratch.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs =
            [
                Switch("ColumnNoiseScratch", "misc", "garbage", () => ColumnNoiseScratch.Enabled,
                    on => ColumnNoiseScratch.Enabled = on)
            ],
            Probe = () => (ColumnNoiseScratch.StoodDown, ColumnNoiseScratch.Rewritten) switch
            {
                (true, _) => StoodDown,
                (_, true) => Active,
                _ => EngineChanged
            }
        },
        new("WorldGenScratch")
        {
            Order = 910,
            Install = c => WorldGenScratch.Install(c.Harmony),
            ServerOnly = true,
            Knobs =
            [
                Switch("WorldGenScratch", "misc", "garbage", () => WorldGenScratch.Enabled,
                    on => WorldGenScratch.Enabled = on)
            ],
            Probe = () => (WorldGenScratch.Foreign != 0, WorldGenScratch.Rewritten == WorldGenScratch.AllBits) switch
            {
                (true, _) => StoodDown,
                (_, true) => Active,
                _ => EngineChanged
            }
        },
        new("SnowPeek")
        {
            Order = 930,
            Replaces = () => SnowPeek.Replaced,
            Rivals = KapiPatchKinds.Body,
            Install = c => SnowPeek.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch("SnowPeek", "misc", "garbage", () => SnowPeek.Enabled, on => SnowPeek.Enabled = on)],
            Probe = () => SnowPeek.Rewritten ? Active : EngineChanged
        },
        new("TickScratch")
        {
            Order = 940,
            Install = c => TickScratch.Install(c.Harmony),
            ServerOnly = true,
            Knobs =
            [
                Switch("TickScratch", "misc", "garbage", () => TickScratch.Enabled, on => TickScratch.Enabled = on)
            ],
            Stop = _ => TickScratch.Clear(),
            Probe = () => (TickScratch.Rewritten > 0, TickScratch.Absent) switch
            {
                (true, _) => Active,
                (_, true) => NotApplicable,
                _ => EngineChanged
            }
        },
        new("SoundScratch")
        {
            Order = 950,
            Install = c => SoundScratch.Install(c.Harmony),
            Knobs =
            [
                Switch("SoundScratch", "misc", "garbage", () => SoundScratch.Enabled, on => SoundScratch.Enabled = on)
            ],
            Probe = () => Found(SoundScratch.Rewritten, false)
        },
        // sounds decoded when first played, not all at the join
        new("SoundsOnDemand")
        {
            Order = 960,
            Install = c => SoundsOnDemand.Install(c.Harmony),
            Knobs =
            [
                Switch("SoundsOnDemand", "misc", "garbage", () => SoundsOnDemand.Enabled,
                    on => SoundsOnDemand.Enabled = on)
            ],
            Probe = () => Found(SoundsOnDemand.Rewritten, false)
        },
        new("TickerCommit")
        {
            Order = 970,
            Install = c => TickerCommit.Install(c.Harmony),
            Knobs =
            [
                Switch("TickerCommit", "misc", "frames", () => TickerCommit.Enabled, on => TickerCommit.Enabled = on)
            ],
            Stop = _ => TickerCommit.Forget(),
            Probe = () => Found(TickerCommit.Rewritten, false)
        },
        new("WeatherLock")
        {
            Order = 990,
            Install = c => WeatherLock.Install(c.Harmony),
            Stop = _ => WeatherLock.Forget(),
            Probe = () => WeatherTarget.Missing ? NotApplicable : Found(WeatherLock.Patched, false)
        }
    ];
}
