using System.Runtime.Intrinsics;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Tessellation;

// Chunk tessellation: its safety and seams, Komet's own tessellator, the schedule and the worker threads.
// In install order; Core's Features places them among the other modules'.
internal static class TessellationFeatures
{
    internal static readonly Feature[] All =
    [
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
        new("NeighbourReads")
        {
            Install = c => NeighbourReads.Install(c.Harmony),
            Knobs =
            [
                Switch("NeighbourReads", "chunks", "tessellation", () => NeighbourReads.Enabled,
                    on => NeighbourReads.Enabled = on),
                Switch("PackedChunks", "chunks", "tessellation", () => NeighbourReads.Packed,
                    on => (NeighbourReads.Packed, ChunkViews.Enabled) = (on, on))
            ],
            Probe = () => Found(NeighbourReads.Rewritten, false)
        },
        new("FaceLight")
        {
            Install = c => FaceLight.Install(c.Harmony, c.Logger),
            Knobs = [Switch("FaceLight", "chunks", "tessellation", () => FaceLight.Enabled,
                on => FaceLight.Enabled = on)],
            Stop = _ => FaceLight.Forget(),
            Probe = () => Vector128.IsHardwareAccelerated ? Active : NotApplicable,
            Shapes =
            [
                new EngineBodies(FaceLight.Shaped, FaceLight.Shape),
                new EngineBodies(FaceLight.FusedShaped, FaceLight.FusedShape)
            ]
        },
        new("OwnTessellation")
        {
            Install = c => OwnTessellation.Install(c.Harmony, c.Logger, c.Api?.World as ClientMain),
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
        new("TessAccounting") { Install = c => TessAccounting.Install(c.Harmony), Stop = _ => TessAccounting.Clear() },
        new("TessSchedule")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager),
                nameof(ChunkTesselatorManager.OnSeperateThreadGameTick), [typeof(float)])],
            Install = c => TessSchedule.Install(c.Harmony),
            Requires = ["TessSafety"],
            Knobs = [Switch("TessSchedule", "chunks", "tessellation", () => TessSchedule.Enabled,
                on => TessSchedule.Enabled = on, "NearFirst")],
            Stop = _ => TessSchedule.Clear()
        },
        // only with TessSafety and TessSchedule installed completely
        new("TessWorkers")
        {
            Replaces = () => [TessSeams.Method(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.TesselateChunk),
                typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType())],
            Rivals = PatchKinds.Body,
            Install = c => TessWorkers.Install(c.Harmony),
            Requires = ["TessSafety", "TessSchedule", "WorkerPool"],
            Knobs = [new("TessPriority", "chunks", "threads", 0, 100, 0, () => TessWorkers.Priority,
                v => TessWorkers.Priority = v)
                {
                    Unit = "%", Weighed = true, Presets = new(Quality: 100, Balanced: 25, Speed: 10), Needs = "WorkerThreads"
                }],
            Stop = _ => TessWorkers.Stop()
        },
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
        // chunks deep under the surface, far from the player, kept without geometry
        new("DeepCulling")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.NowProcessChunk),
                [typeof(int), typeof(int), typeof(int), typeof(TesselatedChunk), typeof(bool)])],
            Requires = ["OccludedChunks"],
            Install = c => DeepCulling.Install(c.Harmony, c.Api, c.Logger),
            Knobs = [Switch("DeepCulling", "render", "culling", () => DeepCulling.Enabled, on => DeepCulling.Enabled = on)
                with { Weighed = true }],
            Stop = _ => DeepCulling.Stop(),
            Probe = () => Found(DeepCulling.Installed, !OccludedChunks.Installed || OccludedChunks.StoodDown)
        },
        new("FarLod")
        {
            Replaces = () => [AccessTools.DeclaredMethod(typeof(ChunkTesselator), "BuildDecorPolygons")],
            Install = c => FarLod.Install(c.Harmony, c.Api, c.Logger),
            // on from 2 px: far chunks at half resolution, the farthest at a quarter (measured: alike to the eye, −24 % frame
            // time in flight at view distance 1536)
            Knobs = [new("FarLod", "render", "drawing", 0, FarLod.MaxPixels, 0, () => FarLod.Pixels, v => FarLod.Pixels = v)
            {
                Unit = "px", Weighed = true, Presets = new(Quality: 0, Balanced: 2, Speed: 3)
            }],
            Stop = _ => FarLod.Stop(),
            Probe = () => FarLod.Installed ? Active : EngineChanged
        },
        new("FarFoliage")
        {
            Replaces = () => [TessSeams.Method(typeof(ChunkTesselator), "TesselateBlock", typeof(Block), typeof(int),
                typeof(int), typeof(int), typeof(int), typeof(int))],
            Install = c => FarFoliage.Install(c.Harmony, c.Api, c.Logger),
            // little video memory: distant foliage simplified from 2 px
            Knobs = [new("FarFoliage", "render", "drawing", 0, FarFoliage.MaxPixels, FarFoliage.Engine, () => FarFoliage.Pixels,
                v => FarFoliage.Pixels = v)
            {
                Unit = "px", Weighed = true, Presets = new(Quality: 0, Balanced: 1, Speed: 2),
                Suggest = hw => hw.VramGb is > 0 and <= 4 ? 2 : null
            }],
            Stop = _ => FarFoliage.Stop(),
            Probe = () => FarFoliage.Installed ? Active : EngineChanged
        }
    ];
}
