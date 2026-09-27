using System.Runtime.Intrinsics;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// Every feature in install order: KometModSystem installs, rechecks and stops them from here, and their knobs, each placed by Order
// in the settings dialog, form the knob table that komet-hud.json, the settings pages and the benchmark arms read (Knobs). Arms write
// the statics through Knobs.Write, never HudSettings, which persists what it sets. Stops run in reverse: PreJit's first, GraphicsMenu's
// before the HUD's window goes, TessWorkers' before the pool's.
internal static partial class Features
{
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
            Knobs = [Switch(27, "GraphicsMenu", "misc", "menu", () => GraphicsMenu.Enabled,
                on => GraphicsMenu.Enabled = on)],
            Stop = _ => GraphicsMenu.Clear(), // before the window it opens goes
            Probe = () => Found(GraphicsMenu.Installed, GraphicsMenu.StoodDown),
            Shapes = [new EngineBodies(GraphicsMenu.Shaped, GraphicsMenu.Fingerprint)]
        },
        new("MenuBlur")
        {
            Knobs = [Switch(28, "MenuBlur", "misc", "menu", () => Backdrop.Enabled, on => Backdrop.Enabled = on)]
        },
        new("ShaderUseCache")
        {
            Install = c => ShaderUseCache.Install(c.Harmony),
            Knobs = [Switch(2, "ShaderUseCache", "render", "drawing", () => ShaderUseCache.Enabled,
                on => ShaderUseCache.Enabled = on)]
        },
        new("DistantShadows")
        {
            Install = c => DistantShadows.Install(c.Harmony, c.Logger),
            Knobs = [Switch(39, "DistantShadows", "render", "drawing", () => DistantShadows.Enabled,
                on => DistantShadows.Enabled = on)],
            Probe = () => Found(DistantShadows.Matched && DistantShadows.Injected, DistantShadows.Failed, Failed),
            Shapes = [new EngineBodies(DistantShadows.Seams, DistantShadows.Fingerprint)],
            Stop = _ => DistantShadows.Forget()
        },
        new("FrustumSweep")
        {
            Install = c => FrustumSweep.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch(0, "FrustumSweep", "render", "culling", () => FrustumSweep.Enabled,
                    on => FrustumSweep.Enabled = on),
                Switch(32, "FrustumStages", "render", "culling", () => FrustumSweep.Stages,
                    on => FrustumSweep.Stages = on)
            ]
        },
        new("IndirectDraw")
        {
            Install = c => IndirectDraw.Install(c.Harmony),
            Knobs = [Switch(3, "IndirectDraw", "render", "drawing", () => IndirectDraw.Enabled,
                on => IndirectDraw.Enabled = on)],
            Probe = () => IndirectDraw.Detected && !IndirectDraw.Supported ? NotApplicable : Active
        },
        new("SunOcclusion")
        {
            Install = c => SunOcclusion.Install(c.Harmony),
            Knobs = [Switch(1, "SunOcclusion", "render", "culling", () => SunOcclusion.Enabled,
                on => SunOcclusion.Enabled = on)]
        },
        new("AnimatableCulling")
        {
            Install = c => AnimatableCulling.Install(c.Harmony, c.Logger),
            Knobs = [Switch(30, "AnimatableCulling", "render", "culling", () => AnimatableCulling.Enabled,
                on => AnimatableCulling.Enabled = on)],
            Recheck = AnimatableCulling.Recheck,
            Probe = () => Found(AnimatableCulling.Matched, AnimatableCulling.Blocked),
            Shapes = [new EngineBodies(AnimatableCulling.Shaped, AnimatableCulling.Fingerprint)]
        },
        new("IdleAnimators")
        {
            Install = c => IdleAnimators.Install(c.Harmony, c.Logger),
            Knobs = [Switch(34, "IdleAnimators", "render", "culling", () => IdleAnimators.Enabled,
                on => IdleAnimators.Enabled = on)],
            Recheck = IdleAnimators.Recheck,
            Probe = () => Found(IdleAnimators.Matched && IdleAnimators.Rewritten, IdleAnimators.Blocked),
            Shapes = [new EngineBodies(IdleAnimators.Seams, IdleAnimators.Fingerprint)]
        },
        new("WindowSizeCache")
        {
            Install = c => WindowSizeCache.Install(c.Harmony),
            Knobs = [Switch(4, "WindowSizeCache", "render", "drawing", () => WindowSizeCache.Enabled,
                on => WindowSizeCache.Enabled = on)]
        },
        new("PoolScale")
        {
            Install = c => PoolScale.Install(c.Harmony),
            Knobs = [new("PoolScale", "render", "drawing", PoolScale.Engine, PoolScale.MaxScale, PoolScale.Engine,
                () => PoolScale.Scale, v => PoolScale.Scale = v) { Unit = "x", Order = 37 }],
            Probe = () => PoolScale.Rewritten ? Active : EngineChanged
        },
        new("GlErrorPoll")
        {
            Install = c => GlErrorPoll.Install(c.Harmony),
            Knobs = [Switch(33, "GlErrorPoll", "render", "drawing", () => GlErrorPoll.Enabled,
                on => GlErrorPoll.Enabled = on)],
            Probe = () => GlErrorPoll.Rewritten ? Active : EngineChanged
        },
        new("ChunkLookup")
        {
            Install = c => ChunkLookup.Install(c.Harmony),
            Knobs = [Switch(8, "ChunkLookup", "chunks", "arrival", () => ChunkLookup.Enabled,
                on => ChunkLookup.Enabled = on)],
            Stop = _ => ChunkLookup.Clear()
        },
        new("MeshPool")
        {
            Install = c => MeshPool.Install(c.Harmony),
            Knobs = [Switch(5, "MeshPool", "render", "upload", () => MeshPool.Enabled, on => MeshPool.Enabled = on,
                "PoolFragments")]
        },
        new("MeshRecycle")
        {
            Install = c => MeshRecycle.Install(c.Harmony),
            Knobs = [Switch(6, "MeshRecycle", "render", "upload", () => MeshRecycle.Enabled,
                on => MeshRecycle.Enabled = on)]
        },
        new("ClimateCache")
        {
            Install = c => ClimateCache.Install(c.Harmony),
            Knobs = [Switch(23, "ClimateCache", "misc", "garbage", () => ClimateCache.Enabled,
                on => ClimateCache.Enabled = on)]
        },
        new("PartitionReuse")
        {
            Install = c => PartitionReuse.Install(c.Harmony),
            Knobs = [Switch(29, "PartitionReuse", "misc", "garbage", () => PartitionReuse.Enabled,
                on => PartitionReuse.Enabled = on)],
            Probe = () => PartitionReuse.Rewritten ? Active : EngineChanged
        },
        new("HandlerLists")
        {
            Install = c => HandlerLists.Install(c.Harmony),
            Knobs = [Switch(36, "HandlerLists", "misc", "garbage", () => HandlerLists.Enabled,
                on => HandlerLists.Enabled = on)],
            Probe = () => HandlerLists.Rewritten ? Active : EngineChanged,
            Stop = _ => HandlerLists.Clear()
        },
        new("CookingMatch")
        {
            Install = c => CookingMatch.Install(c.Harmony, c.Logger),
            Knobs = [Switch(35, "CookingMatch", "misc", "garbage", () => CookingMatch.Enabled,
                on => CookingMatch.Enabled = on)],
            Probe = () => CookingMatch.Matched && CookingMatch.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(CookingMatch.Seams, CookingMatch.Fingerprint)]
        },
        new("AnimationFrames")
        {
            Install = c => AnimationFrames.Install(c.Harmony, c.Logger),
            Knobs = [Switch(19, "AnimationFrames", "misc", "entities", () => AnimationFrames.Enabled,
                on => AnimationFrames.Enabled = on)],
            Recheck = AnimationFrames.Recheck,
            Stop = _ => AnimationFrames.Clear(),
            Probe = () => Found(AnimationFrames.Matched, AnimationFrames.Blocked),
            Shapes = [new EngineBodies(AnimationFrames.Shaped, AnimationFrames.Fingerprint)]
        },
        new("InitOnce")
        {
            Install = c => InitOnce.Install(c.Harmony, c.Logger),
            Knobs = [Switch(20, "InitOnce", "misc", "entities", () => InitOnce.Enabled, on => InitOnce.Enabled = on)],
            Recheck = InitOnce.Recheck,
            Probe = () => Found(InitOnce.Matched, InitOnce.Blocked),
            Shapes = [new EngineBodies(InitOnce.Shaped, InitOnce.Fingerprint)]
        },
        new("ShapeInitMemo")
        {
            Install = c => ShapeInitMemo.Install(c.Harmony, c.Logger),
            Knobs = [Switch(21, "ShapeInitMemo", "misc", "entities", () => ShapeInitMemo.Enabled,
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
                { Unit = "ms", Order = 22 }],
            Probe = () => EntityTessBudget.Substituted ? Active : EngineChanged
        },
        new("ChunkBudget")
        {
            Install = c => ChunkBudget.Install(c.Harmony),
            Knobs = [new("UploadCap", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis,
                ChunkBudget.Uncapped, () => ChunkBudget.CapMillis, v => ChunkBudget.CapMillis = v)
                { Unit = "ms", Order = 7 }],
            Probe = () => ChunkBudget.Capped ? Active : EngineChanged
        },
        new("ChunkThreadClosure") { Install = c => ChunkThreadClosure.Install(c.Harmony), ServerOnly = true },
        new("CloudTileScratch")
        {
            Install = c => CloudTileScratch.Install(c.Harmony),
            Knobs = [Switch(25, "CloudTileScratch", null, null, () => CloudTileScratch.Enabled,
                on => CloudTileScratch.Enabled = on)]
        },
        new("DecompressScratch")
        {
            Install = c => DecompressScratch.Install(c.Harmony),
            Knobs = [Switch(9, "DecompressScratch", "chunks", "arrival", () => DecompressScratch.Enabled,
                on => DecompressScratch.Enabled = on)]
        },
        new("LightScratch")
        {
            Install = c => LightScratch.Install(c.Harmony, c.Logger),
            Knobs = [Switch(17, "LightScratch", "chunks", "light", () => LightScratch.Enabled,
                on => LightScratch.Enabled = on)],
            Probe = () => LightScratch.StoodDown ? StoodDown : Active
        },
        new("LightRepair")
        {
            Install = c => LightRepair.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch(38, "LightRepair", "chunks", "light", () => LightRepair.Enabled,
                on => LightRepair.Enabled = on)],
            Recheck = LightRepair.Recheck,
            Probe = () => Found(LightRepair.Matched, LightRepair.Failed, Failed),
            Shapes = [new EngineBodies(LightRepair.Seams, LightRepair.Fingerprint)]
        },
        new("ParticleLight")
        {
            Install = c => ParticleLight.Install(c.Harmony),
            Knobs = [Switch(18, "ParticleLight", "chunks", "light", () => ParticleLight.Enabled,
                on => ParticleLight.Enabled = on)],
            Stop = _ => ParticleLight.Forget()
        },
        // worldgen
        new("ColumnNoiseScratch")
        {
            Install = c => ColumnNoiseScratch.Install(c.Harmony, c.Logger),
            ServerOnly = true,
            Knobs = [Switch(24, "ColumnNoiseScratch", "misc", "garbage", () => ColumnNoiseScratch.Enabled,
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
            Knobs = [Switch(11, "ExtendedRows", "chunks", "tessellation", () => ExtendedRows.Enabled,
                on => ExtendedRows.Enabled = on)],
            Shapes = [new EngineBodies(ExtendedRows.Shaped, ExtendedRows.Shape)]
        },
        new("VisibleFaces")
        {
            Install = c => VisibleFaces.Install(c.Harmony, c.Logger),
            Knobs = [Switch(12, "VisibleFaces", "chunks", "tessellation", () => VisibleFaces.Enabled,
                on => VisibleFaces.Enabled = on)],
            Shapes = [new EngineBodies(VisibleFaces.Shaped, VisibleFaces.Shape)]
        },
        new("FaceLight")
        {
            Install = c => FaceLight.Install(c.Harmony, c.Logger),
            Knobs = [Switch(13, "FaceLight", "chunks", "tessellation", () => FaceLight.Enabled,
                on => FaceLight.Enabled = on)],
            Probe = () => Vector128.IsHardwareAccelerated ? Active : NotApplicable,
            Shapes =
            [
                new EngineBodies(FaceLight.Shaped, FaceLight.Shape),
                new EngineBodies(FaceLight.FusedShaped, FaceLight.FusedShape)
            ]
        },
        new("TessBlockPos")
        {
            Install = c => TessBlockPos.Install(c.Harmony),
            Knobs = [Switch(31, "TessBlockPos", "misc", "garbage", () => TessBlockPos.Enabled,
                on => TessBlockPos.Enabled = on)],
            Probe = () => TessBlockPos.Rewritten ? Active : EngineChanged
        },
        new("TessAccounting") { Install = c => TessAccounting.Install(c.Harmony), Stop = _ => TessAccounting.Clear() },
        new("TessSchedule")
        {
            Install = c => TessSchedule.Install(c.Harmony),
            Requires = ["TessSafety"],
            Knobs = [Switch(10, "TessSchedule", "chunks", "tessellation", () => TessSchedule.Enabled,
                on => TessSchedule.Enabled = on, "NearFirst")],
            Stop = _ => TessSchedule.Clear()
        },
        new("WorkerPool")
        {
            Knobs = [new("WorkerThreads", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => WorkerPool.Wanted,
                v => WorkerPool.Wanted = v) { Order = 15 }],
            Stop = _ => StopPool(),
            Probe = () => WorkerPool.FrameOff || WorkerPool.BackgroundOff ? Failed : Active
        },
        // only with TessSafety and TessSchedule installed completely
        new("TessWorkers")
        {
            Install = c => TessWorkers.Install(c.Harmony),
            Requires = ["TessSafety", "TessSchedule", "WorkerPool"],
            Knobs = [new("TessJobs", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => TessWorkers.Jobs,
                v => TessWorkers.Jobs = v) { Order = 16 }],
            Stop = _ => TessWorkers.Stop()
        },
        new("OccludedChunks")
        {
            Install = c => OccludedChunks.Install(c.Harmony, c.Logger),
            Knobs = [Switch(14, "OccludedChunks", "chunks", "tessellation", () => OccludedChunks.Enabled,
                on => OccludedChunks.Enabled = on)],
            Shapes = [new EngineBodies(OccludedChunks.Shaped, OccludedChunks.Shape)]
        },
        new("FrameClock") { Install = c => FrameClock.Install(c.Harmony) },
        // inert unless KOMET_BENCH names a bench.json
        new("Benchmark") { Install = c => Benchmark.Install(c.Api), Stage = FeatureStage.Tail },
        // last: every patch above is in place and left alone
        new("PreJit")
        {
            Install = c => PreJit.Start(c.Logger),
            Stage = FeatureStage.Last,
            Knobs = [Switch(26, "PreJit", "misc", "garbage", () => PreJit.Enabled, on => PreJit.Enabled = on)],
            Stop = _ => PreJit.Stop()
        }
    ];

    // Every thread ends; one still inside a job after WorkerPool.JoinMs fails the first assertion
    private static void StopPool()
    {
        var ended = WorkerPool.Stop();
        _ = Assert(ended) && Assert(WorkerPool.Running == 0);
    }

    // A probe: EngineChanged until the feature matched the engine, then blockedAs while blocked (another mod's patch, a failure)
    private static FeatureState Found(bool matched, bool blocked, FeatureState blockedAs = StoodDown) =>
        (matched, blocked) switch
        {
            (false, _) => EngineChanged,
            (_, true) => blockedAs,
            _ => Active
        };

    // A switch is 1 or 0 and its engine value 0; order is its place in the settings dialog and the knob table
    private static Knob Switch(int order, string key, string? page, string? group, Func<bool> get, Action<bool> set,
        string? json = null) =>
        Assert(key.Length > 0) && NotNull(get) && NotNull(set)
            ? new Knob(key, page, group, 0, 1, 0, () => get() ? 1 : 0, value => set(value != 0), json) { Order = order }
            : throw new ArgumentException("knob without a name", nameof(key));
}
