using System.Globalization;
using System.Runtime;
using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Komet.Vulkan;
using Komet.World;
using Vintagestory.Client;

namespace Komet.Hud;

// The report's panels (HudPanel), built once over the sampler's collectors: the rows the bench averages and the
// protocol's HUD section quotes
internal sealed class HudPanels(ICoreClientAPI capi, HudSampler sampler, HudBuild build)
{
    public const int Count = 9;
    private const string Gen0Size = "DOTNET_GCgen0size";
    private const double Mebibyte = 1.0 / 1024 / 1024;

    // Vulkan's opaque terrain pipeline statistics, in its query order; NaN while Vulkan does not draw it
    private const int Vertices = 0, Fragments = 1, Primitives = 2, Rasterized = 3;

    private static readonly Func<string> VulkanOff = KometText.Once("hud-vulkan-off");
    private readonly FrameStats _frames = sampler.Frames;
    private readonly GpuStats _gpu = sampler.Gpu;
    private readonly List<HudPanel> _panels = [];
    private readonly RenderPassStats _passes = sampler.Passes;
    private readonly ResourceStats _resources = sampler.Resources;
    private readonly SpikeLedger _spikes = sampler.Spikes;
    private readonly ModTimes _timings = sampler.Timings;

    // The GC's mode and gen0 budget in the player's language
    private static (string Mode, string Budget) Gc { get; } = GcConfig(KometText.Translate);

    // The GC reads its config at startup: Komet reports effective values, mode and budget on separate lines (column
    // width).
    public static (string Mode, string Budget) GcConfig(System.Func<string, object[], string> t)
    {
        var config = GC.GetConfigurationVariables();
        var mode = GCSettings.IsServerGC
            ? t("hud-gc-server", [
                ConfigValue(config, "HeapCount"),
                ConfigValue(config, "GCDynamicAdaptationMode")
            ])
            : t("hud-gc-workstation", []);
        var concurrent = t(config.TryGetValue("ConcurrentGC", out var bgc) && bgc is true
            ? "hud-gc-concurrent"
            : "hud-gc-blocking", []);
        var budget = ConfigValue(config, "GCGen0MaxBudget");
        var size = Environment.GetEnvironmentVariable(Gen0Size) ?? "";
        if (!Assert(size.Length < 64) || !Assert(config.Count > 0)) size = "";

        return (mode + ", " + concurrent,
            (budget > 0 ? KometText.Format(budget * Mebibyte, "N0") + " MB" : "?") +
            (size.Length > 0 ? $", {Gen0Size}={size}" : ""));
    }

    private static long ConfigValue(IReadOnlyDictionary<string, object> config, string key) =>
        config.TryGetValue(key, out var value) &&
               value is IConvertible number
            ? number.ToInt64(CultureInfo.InvariantCulture)
            : -1;

    private string SpikeCause(int place) =>
        Index(place, SpikeLedger.Shown) &&
               _spikes.Ranked(place) is { Name.Length: > 0 } cause
            ? KometText.Translate("hud-spike-cause", HudParts.Describe(cause.Name), cause.Count,
                KometText.Format(cause.MaxMs, "F1"))
            : "";

    // An unplaced cap would leave the engine's budget unchanged silently
    public static string UploadCapText()
    {
        var millis = ChunkBudget.CapMillis;
        if (!Assert(millis is >= ChunkBudget.Uncapped and <= ChunkBudget.MaxCapMillis)) return "";

        if (millis == ChunkBudget.Uncapped) return KometText.Translate("hud-uploadcap-off");

        return KometText.Translate(ChunkBudget.Capped ? "hud-uploadcap-active" : "hud-uploadcap-unpatched", millis);
    }

    public HudPanel[] Build()
    {
        if (!Assert(_panels.Count == 0) || !NotNull(capi.ModLoader)) return [];

        FramesPanel();
        GraphPanel();
        SystemPanel();
        WorldCounters(Panel());
        PassesPanel();
        RenderCostPanel();
        OcclusionPanel();
        RenderCounters(Panel());
        ModTimesPanel();
        _ = Assert(_panels.Count == Count);
        return [.. _panels];
    }

    private void FramesPanel() =>
        _ = Panel()
            .Title("title", () => build.EditionBadge().Text)
            .Line(() => build.Update is { } check ? KometText.Translate(check.Report.Notice().Key, check.Report.Detail) : "",
                sub: true)
            .Value("fps", () => _frames.Fps)
            .Value("low1", () => _frames.Low1Fps, final: true)
            .Value("low01", () => _frames.Low01Fps, final: true)
            .Value("frametime-avg", () => _frames.AverageMs, "ms")
            .Value("frametime-worst", () => _frames.WorstMs, "ms", final: true)
            .Value("frametime-worst-gc", () => _frames.WorstGcMs, "ms", true, true)
            .Value("gpu", () => RenderCost.FrameGpuMs(_gpu.Ms), "ms")
            .Value("draw-calls", () => _frames.DrawCallsPerFrame);

    private void GraphPanel()
    {
        var graph = Panel();
        _ = graph.Section("spikes", SpikeLedger.MinMs, SpikeLedger.MeanFactor)
            .Value("spikes-count", () => _spikes.Count)
            .Value("spikes-threshold", () => _spikes.ThresholdMs, "ms", true)
            .Rows(SpikeLedger.Shown,
                i => graph.Line(() => SpikeCause(i), () => _spikes.Ranked(i).AverageMs, "ms", sub: true));
    }

    private void SystemPanel() =>
        _ = Panel()
            .Section("renderer")
            .Line(VulkanMode.Renderer, sub: true)
            .Section("world")
            .Value("tris-rendered", () => _frames.RenderedTriangles)
            .Value("tris-allocated", () => _frames.AvailableTriangles)
            .Value("chunks-loaded", () => RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded)
            .Value("chunks-tesselate", () => RuntimeStats.chunksAwaitingTesselation)
            .Value("chunks-upload", () => RuntimeStats.chunksAwaitingPooling)
            .Value("entities-rendered", () => RuntimeStats.renderedEntities)
            .Value("entities-loaded", () => capi.World.LoadedEntities.Count)
            .Section("cpu")
            .Bar("cpu-process", () => _resources.CpuPercent)
            .Bar("cpu-system", () => _resources.SystemCpuPercent)
            .Section("memory")
            .Bar("ram-used", () => _resources.PercentOfRam(_resources.UsedRamMb), () => _resources.UsedRamMb, "MB")
            .Value("ram-available", () => _resources.AvailableRamMb, "MB")
            .Value("ram-total", () => _resources.TotalRamMb, "MB")
            .Bar("working-set", () => _resources.PercentOfRam(_resources.WorkingSetMb), () => _resources.WorkingSetMb,
                "MB")
            .Value("heap-used", () => _resources.ManagedUsedMb, "MB", true, true)
            .Value("heap-committed", () => _resources.ManagedCommittedMb, "MB", true, true)
            .Value("native", () => _resources.NativeMb, "MB", true, true)
            .Bar("vram", () => 100 * _gpu.VramUsedMb / _gpu.VramTotalMb, () => _gpu.VramUsedMb, "MB")
            .Value("vram-total", () => _gpu.VramTotalMb, "MB")
            .Value("vram-evictions", () => _gpu.EvictionsPerSec, "/s", true, true)
            .Bar("gc-pause", () => _resources.GcPausePercent)
            .Value("gc-alloc", () => _resources.AllocatedMbPerSec, "MB")
            .Value("gc0", () => _resources.Gen0PerSec, "/s")
            .Value("gc-idle", sampler.PerSecond(() => GcIdle.Moved), "/s", true)
            .Value("gc2", () => _resources.Gen2PerSec, "/s")
            .Value("climate-cache", () => ClimateCache.Capacity, sub: true)
            .Line(() => Gc.Mode, sub: true)
            .Line(() => KometText.Translate("hud-gc-latency", Gc.Budget, GCSettings.LatencyMode), sub: true);

    private void PassesPanel()
    {
        var passes = Panel().Section("passes");
        _ = passes.Rows(RenderPassStats.Count, i => passes
                .Line(() => KometText.Cached("hud-pass-", _passes.Key(i)), () => _passes.AverageMs(i), "ms",
                    () => _passes.Percent(i))
                .Rows(RenderPassStats.DetailCount,
                    j => passes.Line(() => HudParts.Describe(_passes.DetailName(i, j)), () => _passes.DetailMs(i, j), "ms",
                        sub: true)))
            .Section("total")
            .Value("profiler-marks", sampler.PerFrame(() => _passes.MarkCalls))
            .Value("frame", () => _passes.AverageTotalMs, "ms")
            .Section("worstframe")
            .Value("worstframe-total", () => _passes.WorstFrameMs, "ms")
            .Rows(RenderPassStats.DetailCount,
                i => passes.Line(() => HudParts.Describe(_passes.WorstMarkName(i)), () => _passes.WorstMarkMs(i), "ms",
                    sub: true));
    }

    private void ModTimesPanel()
    {
        var times = Panel().Section("modtimes");
        _ = times.Rows(ModTimes.MaxMods, i => times
            .Line(() => _timings.ModName(i), () => _timings.ModMs(i), "ms",
                () => 100 * _timings.ModMs(i) / _frames.AverageMs)
            .Rows(ModTimes.DetailCount,
                j => times.Line(() => _timings.DetailName(i, j), () => _timings.DetailMs(i, j), "ms", sub: true)));
    }

    // GPU rows are one frame three frames back; the rest are interval means.
    private void RenderCostPanel()
    {
        var cost = Panel();
        _ = cost.Section("rendercost")
            .Warn("debug-timings", () => HudBuild.DebugBuild)
            .Value("rc-cpu", sampler.PerFrame(() => RenderCost.TotalTicks, FrameClock.TickMs), "ms")
            .Rows(RenderCost.Groups, i => cost.Value("rc-" + RenderCost.GroupKey(i),
                sampler.PerFrame(() => RenderCost.GroupTicks(i), FrameClock.TickMs), "ms", true))
            .Value("rc-submit", sampler.PerFrame(() => RenderCost.SubmitTicks, FrameClock.TickMs), "ms")
            .Value("rc-draws", sampler.PerFrame(() => RenderCost.Draws))
            .Value("rc-ranges", sampler.PerFrame(() => RenderCost.Ranges))
            .Section("rc-gpu-section")
            .Value("rc-gpu", () => RenderCost.GpuMs, "ms")
            .Rows(RenderCost.Groups, i => cost.Value("rc-" + RenderCost.GroupKey(i), () => RenderCost.GroupGpuMs(i),
                "ms", true))
            .Value("gpu", () => RenderCost.FrameGpuMs(_gpu.Ms), "ms")
            .Section("vulkan")
            .Line(() => VulkanCore.Enabled ? VulkanCore.Status : VulkanOff(), sub: true)
            .Line(() => VulkanRenderer.Only ? VulkanRenderer.Status : "", sub: true);
    }

    // What GPU terrain culling hid, and opaque terrain GPU work (Vulkan's pipeline statistics)
    private void OcclusionPanel()
    {
        var panel = Panel();
        _ = panel.Section("occ-culling")
            .Warn("occ-culling-off", () => !OcclusionCulling.Enabled)
            .Bar("occ-culled", () => OcclusionCulling.CulledPercent,
                static () => OcclusionCulling.CulledTriangles / 1e6, "M")
            .Value("occ-drawn-late", () => OcclusionCulling.LateRanges)
            .Section("pipeline")
            .Value("pipe-vertices", static () => TerrainStatistic(Vertices) / 1e6, "M")
            .Value("pipe-primitives", static () => TerrainStatistic(Primitives) / 1e6, "M")
            .Value("pipe-rasterized", static () => TerrainStatistic(Rasterized) / 1e6, "M")
            .Value("pipe-fragments", static () => TerrainStatistic(Fragments) / 1e6, "M")
            .Value("pipe-per-pixel",
                static () => TerrainStatistic(Fragments) / Math.Max(1, VulkanRenderer.TerrainPixels), "x");
    }

    private static double TerrainStatistic(int at) => VulkanRenderer.TerrainStatistic(at);

    private void RenderCounters(HudPanel panel)
    {
        _ = panel.Section("shadercache")
            .Bar("use-skipped", HudSampler.Share(() => ShaderUseCache.Skips, () => ShaderUseCache.Uploads),
                sampler.PerFrame(() => ShaderUseCache.Skips))
            .Value("use-calls", sampler.PerFrame(() => ShaderUseCache.Calls))
            .Value("use-uploads", sampler.PerFrame(() => ShaderUseCache.Uploads))
            .Section("frustumsweep")
            .Warn("debug-timings", () => HudBuild.DebugBuild)
            .Value("cull-time", sampler.PerFrame(() => FrustumSweep.Ticks, FrameClock.TickMs), "ms")
            .Bar("cull-skipped", HudSampler.Ratio(() => FrustumSweep.Skipped, () => FrustumSweep.Tested, 100),
                sampler.PerFrame(() => FrustumSweep.Skipped))
            .Value("cull-tested", sampler.PerFrame(() => FrustumSweep.Tested))
            .Value("cull-visible", sampler.PerFrame(() => FrustumSweep.Visible))
            .Value("cull-rebuilt", sampler.PerFrame(() => FrustumSweep.Rebuilds))
            .Value("cull-diffed", sampler.PerFrame(() => FrustumSweep.Diffed))
            .Value("cull-rebuilt-max", () => FrustumSweep.MaxRebuilds)
            .Value("cull-sorted", sampler.PerFrame(() => FrustumSweep.Settles))
            .Value("cull-staged", sampler.PerFrame(() => FrustumSweep.StagedCalls))
            .Peaks(FrustumSweep.ResetPeaks)
            .Section("animculling")
            .Value("anim-idle", sampler.PerFrame(() => IdleAnimators.Skipped))
            .Bar("anim-culled", HudSampler.Share(() => AnimatableCulling.Culled, () => AnimatableCulling.Drawn),
                sampler.PerFrame(() => AnimatableCulling.Culled))
            .Value("anim-drawn", sampler.PerFrame(() => AnimatableCulling.Drawn))
            .Bar("pot-culled", HudSampler.Share(() => PotCulling.Culled, () => PotCulling.Drawn),
                sampler.PerFrame(() => PotCulling.Culled))
            .Bar("hearth-culled", HudSampler.Share(() => HearthCulling.Culled, () => HearthCulling.Drawn),
                sampler.PerFrame(() => HearthCulling.Culled))
            .Bar("help-skipped", HudSampler.Share(() => HelpCompose.Skipped, () => HelpCompose.Composed),
                sampler.PerFrame(() => HelpCompose.Skipped))
            .Section("indirectdraw")
            .Warn("draw-unsupported", () => IndirectDraw.Detected && !IndirectDraw.Supported)
            .Warn("draw-unmapped", () => IndirectDraw.Supported && !IndirectDraw.Persistent)
            .Value("draw-calls-indirect", sampler.PerFrame(() => IndirectDraw.Draws))
            .Value("draw-ranges", sampler.PerFrame(() => IndirectDraw.Ranges));
        UploadCounters(panel);
    }

    private void UploadCounters(HudPanel panel) =>
        _ = panel.Section("meshpool")
            .Value("pool-trimmed", () => PoolTrim.Trimmed)
            .Value("pool-trimmed-mb", () => PoolTrim.TrimmedBytes >> 20, "MB")
            .Value("pool-drained", () => PoolTrim.Drained)
            .Section("meshrecycle")
            .Value("recycle-saved", sampler.PerFrame(() => MeshRecycle.Saved, 4 * Mebibyte), "MB")
            .Value("recycle-clones", sampler.PerFrame(() => MeshRecycle.Clones))
            .Value("recycle-reused", sampler.PerFrame(() => MeshRecycle.Reused))
            .Section("chunkbudget")
            .Line(UploadCapText)
            .Bar("upload-capped", sampler.PerFrame(() => ChunkBudget.CapHits, 100))
            .Bar("upload-over", sampler.PerFrame(() => ChunkBudget.Over, 100))
            .Value("upload-over-ms", HudSampler.Ratio(() => ChunkBudget.OverTicks * FrameClock.TickMs, () => ChunkBudget.Over),
                "ms")
            .Bar("upload-priority-cut", sampler.PerFrame(() => ChunkBudget.PriorityCuts, 100))
            .Value("upload-births", sampler.PerSecond(() => VulkanRenderer.Born))
            .Value("upload-birth-ms", HudSampler.Ratio(() => VulkanRenderer.BirthTicks * FrameClock.TickMs,
                () => VulkanRenderer.Born), "ms")
            .Section("chunkload")
            .Warn("chunkload-unpatched",
                () => ChunkLoadBudget.Millis != ChunkLoadBudget.Engine && !ChunkLoadBudget.Rewritten)
            .Bar("chunkload-stops", sampler.PerFrame(() => ChunkLoadBudget.Stops, 100))
            .Value("chunkload-held", HudSampler.Ratio(() => ChunkLoadBudget.Held, () => ChunkLoadBudget.Stops));

    private void WorldCounters(HudPanel panel)
    {
        Func<double> passes = () => TessAccounting.Totals().Passes, passMs = () => TessAccounting.Totals().Ms;
        var busy = sampler.PerSecond(passMs, 0.1); // percent of one thread
        _ = panel.Section("chunklookup")
            .Bar("chunk-hitrate", HudSampler.Share(() => ChunkLookup.Hits, () => ChunkLookup.Misses))
            .Value("chunk-hits", sampler.PerFrame(() => ChunkLookup.Hits))
            .Section("tessaccounting")
            .Warn("tess-unpatched", () => !TessAccounting.Installed)
            .Warn("tess-partial", () => TessAccounting.Installed && !OccludedChunks.Installed)
            .Warn("tess-occluded-other", () => OccludedChunks.StoodDown)
            .Warn("tess-workers-unpatched",
                () => WorkerPool.Wanted > 0 && (!TessWorkers.Installed || TessSafety.Broken || TessWorkers.Broken))
            .Warn("tess-workers-failed", () => TessWorkers.Failed || WorkerPool.BackgroundOff)
            .Value("tess-rate", sampler.PerSecond(passes))
            .Value("tess-near", () => TessSchedule.NearWaiting)
            .Value("tess-mean", HudSampler.Ratio(passMs, passes), "ms")
            .Bar("tess-busy", () => busy() / (1 + WorkerPool.Running));
        _ = panel
            .Bar("tess-edge", HudSampler.Ratio(() => TessAccounting.Totals().Edge, passes, 100),
                sampler.PerSecond(() => TessAccounting.Totals().Edge))
            .Bar("tess-zero", HudSampler.Ratio(() => TessAccounting.ZeroMs, passMs, 100),
                sampler.PerSecond(() => TessAccounting.ZeroPasses))
            .Value("tess-priority",
                sampler.PerSecond(() =>
                    TessAccounting.Count(TessBucket.PriorityFull) + TessAccounting.Count(TessBucket.PriorityEdge)))
            .Value("tess-skipped", sampler.PerSecond(() => TessAccounting.Count(TessBucket.Skipped)))
            .Value("tess-requeued", sampler.PerSecond(() => TessAccounting.Count(TessBucket.Requeued)))
            .Value("tess-occluded", sampler.PerSecond(() => OccludedChunks.Hits))
            .Value("deepculling-chunks", () => DeepCulling.CulledChunks)
            .Section("farfoliage")
            .Warn("farfoliage-unpatched", () => FarFoliage.Pixels > 0 && !FarFoliage.Installed)
            .Value("farfoliage-from", () => FarFoliage.Threshold, "m")
            .Value("farfoliage-chunks", () => FarFoliage.FarChunks)
            .Value("farfoliage-thinned", sampler.PerSecond(() => FarFoliage.Thinned))
            .Value("farfoliage-dropped", sampler.PerSecond(() => FarFoliage.Dropped))
            .Value("farfoliage-marked", sampler.PerSecond(() => FarFoliage.Marked))
            .Section("farlod")
            .Value("farlod-from", () => FarLod.Threshold, "m")
            .Value("farlod-chunks", () => FarLod.Installed ? FarLod.FarChunks : double.NaN)
            .Value("farlod-farthest", () => FarLod.Installed ? FarLod.FarthestChunks : double.NaN);
        LightCounters(panel);
        EntityCounters(panel);
        GarbageCounters(panel);
    }

    private void LightCounters(HudPanel panel) =>
        _ = panel.Section("owntess")
            .Warn("owntess-unpatched", () => !OwnTessellation.Installed)
            .Warn("owntess-other", () => OwnTessellation.StoodDown || OwnTessellation.JsonStoodDown)
            .Bar("owntess-engine", HudSampler.Share(() => OwnTessellation.EngineBlocks, () => OwnTessellation.Blocks),
                sampler.PerFrame(() => OwnTessellation.EngineBlocks))
            .Value("owntess-blocks", sampler.PerFrame(() => OwnTessellation.Blocks))
            .Value("owntess-json", sampler.PerFrame(() => OwnTessellation.JsonBlocks))
            .Section("facelight")
            .Warn("facelight-unpatched", () => !FaceLight.Installed)
            .Warn("facelight-other", () => FaceLight.StoodDown)
            .Bar("facelight-engine", HudSampler.Share(() => FaceLight.EngineFaces, () => FaceLight.FastFaces),
                sampler.PerFrame(() => FaceLight.EngineFaces))
            .Value("facelight-faces", sampler.PerFrame(() => FaceLight.FastFaces))
            .Value("facelight-blocks", sampler.PerFrame(() => FaceLight.FusedBlocks))
            .Section("particlelight")
            .Warn("particlelight-unpatched", () => !ParticleLight.Installed)
            .Bar("particlelight-busy", HudSampler.Ratio(() => ParticleLight.Busy, () => ParticleLight.Reads, 100),
                sampler.PerFrame(() => ParticleLight.Busy))
            .Value("particlelight-reads", sampler.PerFrame(() => ParticleLight.Reads))
            .Value("particlelight-packed", sampler.PerFrame(() => ParticleLight.Packed));

    private void EntityCounters(HudPanel panel) =>
        _ = panel.Section("animframes")
            .Warn("anim-fast-blocked", () => AnimationFrames.Blocked)
            .Bar("anim-hitrate", HudSampler.Share(() => AnimationFrames.Hits, () => AnimationFrames.Misses))
            .Value("anim-compile-worst", () => AnimationFrames.WorstMs, "ms")
            .Line(() => AnimationFrames.WorstCode, sub: true)
            .Value("anim-hits", sampler.PerSecond(() => AnimationFrames.Hits), "/s")
            .Value("anim-misses", sampler.PerSecond(() => AnimationFrames.Misses), "/s")
            .Peaks(AnimationFrames.ResetPeaks)
            .Section("entitytess")
            .Warn("shapememo-blocked", () => ShapeInitMemo.Blocked)
            .Value("shapememo-skipped", sampler.PerSecond(() => ShapeInitMemo.Skipped), "/s")
            .Section("entitytimes")
            .Warn("entitytimes-unpatched", () => !EntityTimes.Installed)
            .Rows(EntityTimes.Parts, part => EntityTime(panel, part))
            .Peaks(EntityTimes.ResetPeaks);

    private void EntityTime(HudPanel panel, int part)
    {
        string[] keys = ["entitytimes-tick", "entitytimes-animation", "entitytimes-prepare"];
        _ = panel.Value(keys[part], sampler.PerFrame(() => EntityTimes.Ticks(part), FrameClock.TickMs), "ms")
            .Line(() => EntityTimes.SlowestCode(part).Length == 0
                ? ""
                : KometText.Translate("hud-entitytess-slowest", EntityTimes.SlowestCode(part),
                    KometText.Format(EntityTimes.SlowestMs(part), "F1")), sub: true);
    }

    private void GarbageCounters(HudPanel panel)
    {
        _ = panel.Section("garbage")
            .Warn("garbage-unpatched", () =>
                !(DecompressScratch.Rewritten && LightScratch.Rewritten &&
                  (ColumnNoiseScratch.Rewritten || !KometMod.LocalServer) && // world generation runs on the server
                  CloudTileScratch.Rewritten && PartitionReuse.Rewritten && CookingMatch.Rewritten))
            .Value("garbage-decompress", sampler.PerFrame(() => DecompressScratch.Saved, Mebibyte), "MB")
            .Value("garbage-noise", sampler.PerFrame(() => ColumnNoiseScratch.Saved, Mebibyte), "MB")
            .Value("garbage-light", sampler.PerFrame(() => LightScratch.Avoided))
            .Value("garbage-partitions", sampler.PerFrame(() => PartitionReuse.Reused))
            .Value("garbage-bus-swept", sampler.PerFrame(() => EventBusSweep.Swept))
            .Value("garbage-bus-listeners", () => EventBusSweep.Listeners);
    }

    private HudPanel Panel()
    {
        var panel = new HudPanel();
        _panels.Add(panel);
        return panel;
    }
}
