using System.Globalization;
using System.Runtime;
using System.Text;
using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using Komet.Shapes;
using Komet.Tessellation;
using Komet.World;
using Vintagestory.Client;

namespace Komet.Hud;

// The report's panels (HudPanel), built once: the rows the bench averages and the protocol's HUD section quotes
internal sealed partial class HudOverlay
{
    private const int PanelCount = 9;
    private const string Gen0Size = "DOTNET_GCgen0size";
    private const double Mebibyte = 1.0 / 1024 / 1024;

    // Vulkan's opaque terrain pipeline statistics, in its query order; NaN while Vulkan does not draw it
    private const int Vertices = 0, Fragments = 1, Primitives = 2, Rasterized = 3;

    private static readonly Func<string> VulkanOff = KometText.Once("hud-vulkan-off");

    // The GC reads its config at startup: Komet reports effective values, mode and budget on separate lines (column
    // width).
    private static (string Mode, string Budget) GcConfig(System.Func<string, object[], string> t)
    {
        var config = GC.GetConfigurationVariables();
        var mode = GCSettings.IsServerGC
            ? t("hud-gc-server", [ConfigValue(config, "HeapCount"),
                ConfigValue(config, "GCDynamicAdaptationMode")])
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
        value is IConvertible number ? number.ToInt64(CultureInfo.InvariantCulture) : -1;

    // Mark names without engine namespaces, so one long type name cannot widen a column; pseudo causes in words
    private static string Describe(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Assert(name.Length < 1024)) return "";
        if (name[0] == '~') return KometText.Cached("hud-cause-", name);
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return name;
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && dash < dot
            ? string.Concat(name.AsSpan(0, dash + 1), name.AsSpan(dot + 1))
            : name[(dot + 1)..];
    }

    private string SpikeCause(int place) => Index(place, SpikeLedger.Shown) &&
        _spikes.Ranked(place) is { Name.Length: > 0 } cause
            ? KometText.Translate("hud-spike-cause", Describe(cause.Name), cause.Count, KometText.Format(cause.MaxMs, "F1"))
            : "";

    private void AppendSpikes(StringBuilder text)
    {
        if (_spikes.Stored == 0) return;
        _ = text.Append("\n\n[").Append(KometText.Translate("hud-spikes-recent")).Append(']');
        var marks = new StringBuilder();
        for (var age = 0; age < Math.Min(_spikes.Stored, SpikeLedger.Recent); age++)
        {
            var spike = _spikes.Latest(age);
            _ = marks.Clear();
            for (var rank = 0; rank < SpikeLedger.TopMarks; rank++)
            {
                if (_spikes.LatestMark(age, rank) is not { Name: { } mark } entry) continue;
                _ = marks.Append(marks.Length > 0 ? ", " : "").Append(Describe(mark)).Append(' ')
                    .Append(Millis(entry.Ms)).Append(" ms");
                if (entry.Calls > 1) _ = marks.Append(" ×").Append(entry.Calls);
            }

            _ = text.Append('\n').Append(KometText.Translate("hud-spike-line", KometText.Format(spike.AtSeconds, "F1"),
                Millis(spike.DtMs), Describe(spike.Cause), Millis(spike.GcMs), Millis(spike.OutsideMs),
                Millis(spike.JitMs), Millis(spike.RunQueueMs), marks.ToString()));
        }
    }

    // NaN: not measured; infinite: broken clock
    private static string Millis(double ms)
    {
        if (double.IsNaN(ms)) return "–";
        return Finite(ms) && Assert(ms >= 0) ? KometText.Format(ms, "F1") : "";
    }

    // An unplaced cap would leave the engine's budget unchanged silently
    private static string UploadCapText()
    {
        var millis = ChunkBudget.CapMillis;
        if (!Assert(millis is >= ChunkBudget.Uncapped and <= ChunkBudget.MaxCapMillis)) return "";
        if (millis == ChunkBudget.Uncapped) return KometText.Translate("hud-uploadcap-off");
        return KometText.Translate(ChunkBudget.Capped ? "hud-uploadcap-active" : "hud-uploadcap-unpatched", millis);
    }

    private void BuildPanels()
    {
        if (!Assert(_panels.Count == 0) || !NotNull(_capi.ModLoader)) return;
        FramesPanel();
        GraphPanel();
        SystemPanel();
        WorldCounters(Panel());
        PassesPanel();
        RenderCostPanel();
        OcclusionPanel();
        RenderCounters(Panel());
        ModTimesPanel();
        _ = Assert(_panels.Count == PanelCount);
    }

    private void FramesPanel()
    {
        _ = Panel()
            .Title("title", () => EditionBadge().Text)
            .Line(() => _update is { } check ? KometText.Translate(check.Report.Notice().Key, check.Report.Detail) : "", sub: true)
            .Value("fps", () => _frames.Fps)
            .Value("low1", () => _frames.Low1Fps, final: true)
            .Value("low01", () => _frames.Low01Fps, final: true)
            .Value("frametime-avg", () => _frames.AverageMs, "ms")
            .Value("frametime-worst", () => _frames.WorstMs, "ms", final: true)
            .Value("frametime-worst-gc", () => _frames.WorstGcMs, "ms", true, final: true)
            .Value("gpu", () => RenderCost.FrameGpuMs(_gpu.Ms), "ms")
            .Value("draw-calls", () => _frames.DrawCallsPerFrame);
    }

    private void GraphPanel()
    {
        var graph = Panel();
        _ = graph.Section("spikes", SpikeLedger.MinMs, SpikeLedger.MeanFactor)
            .Value("spikes-count", () => _spikes.Count)
            .Value("spikes-threshold", () => _spikes.ThresholdMs, "ms", true)
            .Rows(SpikeLedger.Shown,
                i => graph.Line(() => SpikeCause(i), () => _spikes.Ranked(i).AverageMs, "ms", sub: true));
    }

    private void SystemPanel()
    {
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
            .Value("entities-loaded", () => _capi.World.LoadedEntities.Count)
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
            .Value("gc-idle", PerSecond(() => GcIdle.Moved), "/s", sub: true)
            .Value("gc2", () => _resources.Gen2PerSec, "/s")
            .Value("climate-cache", () => ClimateCache.Capacity, sub: true)
            .Line(() => _gc.Mode, sub: true)
            .Line(() => KometText.Translate("hud-gc-latency", _gc.Budget, GCSettings.LatencyMode), sub: true);
    }

    private void PassesPanel()
    {
        var passes = Panel().Section("passes");
        _ = passes.Rows(RenderPassStats.Count, i => passes
                .Line(() => KometText.Cached("hud-pass-", _passes.Key(i)), () => _passes.AverageMs(i), "ms",
                    () => _passes.Percent(i))
                .Rows(RenderPassStats.DetailCount,
                    j => passes.Line(() => Describe(_passes.DetailName(i, j)), () => _passes.DetailMs(i, j), "ms",
                        sub: true)))
            .Section("total")
            .Value("profiler-marks", PerFrame(() => _passes.MarkCalls))
            .Value("frame", () => _passes.AverageTotalMs, "ms")
            .Section("worstframe")
            .Value("worstframe-total", () => _passes.WorstFrameMs, "ms")
            .Rows(RenderPassStats.DetailCount,
                i => passes.Line(() => Describe(_passes.WorstMarkName(i)), () => _passes.WorstMarkMs(i), "ms",
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
            .Warn("debug-timings", () => DebugBuild)
            .Value("rc-cpu", PerFrame(() => RenderCost.TotalTicks, FrameClock.TickMs), "ms")
            .Rows(RenderCost.Groups, i => cost.Value("rc-" + RenderCost.GroupKey(i),
                PerFrame(() => RenderCost.GroupTicks(i), FrameClock.TickMs), "ms", true))
            .Value("rc-submit", PerFrame(() => RenderCost.SubmitTicks, FrameClock.TickMs), "ms")
            .Value("rc-draws", PerFrame(() => RenderCost.Draws))
            .Value("rc-ranges", PerFrame(() => RenderCost.Ranges))
            .Section("rc-gpu-section")
            .Value("rc-gpu", () => RenderCost.GpuMs, "ms")
            .Rows(RenderCost.Groups, i => cost.Value("rc-" + RenderCost.GroupKey(i), () => RenderCost.GroupGpuMs(i),
                "ms", true))
            .Value("gpu", () => RenderCost.FrameGpuMs(_gpu.Ms), "ms")
            .Section("vulkan")
            .Line(() => Komet.Vulkan.VulkanCore.Enabled ? Komet.Vulkan.VulkanCore.Status : VulkanOff(), sub: true)
            .Line(() => Komet.Vulkan.VulkanRenderer.Only ? Komet.Vulkan.VulkanRenderer.Status : "", sub: true);
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
                static () => TerrainStatistic(Fragments) / Math.Max(1, Komet.Vulkan.VulkanRenderer.TerrainPixels), "x");
    }

    private static double TerrainStatistic(int at)
    {
        return Komet.Vulkan.VulkanRenderer.TerrainStatistic(at);
    }

    private void RenderCounters(HudPanel panel)
    {
        _ = panel.Section("shadercache")
            .Bar("use-skipped", Share(() => ShaderUseCache.Skips, () => ShaderUseCache.Uploads),
                PerFrame(() => ShaderUseCache.Skips))
            .Value("use-calls", PerFrame(() => ShaderUseCache.Calls))
            .Value("use-uploads", PerFrame(() => ShaderUseCache.Uploads))
            .Section("frustumsweep")
            .Warn("debug-timings", () => DebugBuild)
            .Value("cull-time", PerFrame(() => FrustumSweep.Ticks, FrameClock.TickMs), "ms")
            .Bar("cull-skipped", Ratio(() => FrustumSweep.Skipped, () => FrustumSweep.Tested, 100),
                PerFrame(() => FrustumSweep.Skipped))
            .Value("cull-tested", PerFrame(() => FrustumSweep.Tested))
            .Value("cull-visible", PerFrame(() => FrustumSweep.Visible))
            .Value("cull-rebuilt", PerFrame(() => FrustumSweep.Rebuilds))
            .Value("cull-diffed", PerFrame(() => FrustumSweep.Diffed))
            .Value("cull-rebuilt-max", () => FrustumSweep.MaxRebuilds)
            .Value("cull-sorted", PerFrame(() => FrustumSweep.Settles))
            .Value("cull-staged", PerFrame(() => FrustumSweep.StagedCalls))
            .Peaks(FrustumSweep.ResetPeaks)
            .Section("animculling")
            .Value("anim-idle", PerFrame(() => IdleAnimators.Skipped))
            .Bar("anim-culled", Share(() => AnimatableCulling.Culled, () => AnimatableCulling.Drawn),
                PerFrame(() => AnimatableCulling.Culled))
            .Value("anim-drawn", PerFrame(() => AnimatableCulling.Drawn))
            .Bar("pot-culled", Share(() => PotCulling.Culled, () => PotCulling.Drawn), PerFrame(() => PotCulling.Culled))
            .Bar("hearth-culled", Share(() => HearthCulling.Culled, () => HearthCulling.Drawn), PerFrame(() => HearthCulling.Culled))
            .Bar("help-skipped", Share(() => HelpCompose.Skipped, () => HelpCompose.Composed), PerFrame(() => HelpCompose.Skipped))
            .Section("indirectdraw")
            .Warn("draw-unsupported", () => IndirectDraw.Detected && !IndirectDraw.Supported)
            .Warn("draw-unmapped", () => IndirectDraw.Supported && !IndirectDraw.Persistent)
            .Value("draw-calls-indirect", PerFrame(() => IndirectDraw.Draws))
            .Value("draw-ranges", PerFrame(() => IndirectDraw.Ranges));
        UploadCounters(panel);
    }

    private void UploadCounters(HudPanel panel)
    {
        _ = panel.Section("meshpool")
            .Value("pool-trimmed", () => PoolTrim.Trimmed)
            .Value("pool-trimmed-mb", () => PoolTrim.TrimmedBytes >> 20, "MB")
            .Value("pool-drained", () => PoolTrim.Drained)
            .Section("meshrecycle")
            .Value("recycle-saved", PerFrame(() => MeshRecycle.Saved, 4 * Mebibyte), "MB")
            .Value("recycle-clones", PerFrame(() => MeshRecycle.Clones))
            .Value("recycle-reused", PerFrame(() => MeshRecycle.Reused))
            .Section("chunkbudget")
            .Line(UploadCapText)
            .Bar("upload-capped", PerFrame(() => ChunkBudget.CapHits, 100))
            .Bar("upload-over", PerFrame(() => ChunkBudget.Over, 100))
            .Value("upload-over-ms", Ratio(() => ChunkBudget.OverTicks * FrameClock.TickMs, () => ChunkBudget.Over), "ms")
            .Bar("upload-priority-cut", PerFrame(() => ChunkBudget.PriorityCuts, 100))
            .Value("upload-births", PerSecond(() => Komet.Vulkan.VulkanRenderer.Born))
            .Value("upload-birth-ms", Ratio(() => Komet.Vulkan.VulkanRenderer.BirthTicks * FrameClock.TickMs,
                () => Komet.Vulkan.VulkanRenderer.Born), "ms")
            .Section("chunkload")
            .Warn("chunkload-unpatched",
                () => ChunkLoadBudget.Millis != ChunkLoadBudget.Engine && !ChunkLoadBudget.Rewritten)
            .Bar("chunkload-stops", PerFrame(() => ChunkLoadBudget.Stops, 100))
            .Value("chunkload-held", Ratio(() => ChunkLoadBudget.Held, () => ChunkLoadBudget.Stops));
    }

    private void WorldCounters(HudPanel panel)
    {
        Func<double> passes = () => TessAccounting.Totals().Passes, passMs = () => TessAccounting.Totals().Ms;
        var busy = PerSecond(passMs, 0.1); // percent of one thread
        _ = panel.Section("chunklookup")
            .Bar("chunk-hitrate", Share(() => ChunkLookup.Hits, () => ChunkLookup.Misses))
            .Value("chunk-hits", PerFrame(() => ChunkLookup.Hits))
            .Section("tessaccounting")
            .Warn("tess-unpatched", () => !TessAccounting.Installed)
            .Warn("tess-partial", () => TessAccounting.Installed && !OccludedChunks.Installed)
            .Warn("tess-occluded-other", () => OccludedChunks.StoodDown)
            .Warn("tess-workers-unpatched",
                () => WorkerPool.Wanted > 0 && (!TessWorkers.Installed || TessSafety.Broken || TessWorkers.Broken))
            .Warn("tess-workers-failed", () => TessWorkers.Failed || WorkerPool.BackgroundOff)
            .Value("tess-rate", PerSecond(passes))
            .Value("tess-near", () => TessSchedule.NearWaiting)
            .Value("tess-mean", Ratio(passMs, passes), "ms")
            .Bar("tess-busy", () => busy() / (1 + WorkerPool.Running));
        _ = panel
            .Bar("tess-edge", Ratio(() => TessAccounting.Totals().Edge, passes, 100),
                PerSecond(() => TessAccounting.Totals().Edge))
            .Bar("tess-zero", Ratio(() => TessAccounting.ZeroMs, passMs, 100),
                PerSecond(() => TessAccounting.ZeroPasses))
            .Value("tess-priority",
                PerSecond(() =>
                    TessAccounting.Count(TessBucket.PriorityFull) + TessAccounting.Count(TessBucket.PriorityEdge)))
            .Value("tess-skipped", PerSecond(() => TessAccounting.Count(TessBucket.Skipped)))
            .Value("tess-requeued", PerSecond(() => TessAccounting.Count(TessBucket.Requeued)))
            .Value("tess-occluded", PerSecond(() => OccludedChunks.Hits))
            .Value("deepculling-chunks", () => DeepCulling.CulledChunks)
            .Section("farfoliage")
            .Warn("farfoliage-unpatched", () => FarFoliage.Pixels > 0 && !FarFoliage.Installed)
            .Value("farfoliage-from", () => FarFoliage.Threshold, "m")
            .Value("farfoliage-chunks", () => FarFoliage.FarChunks)
            .Value("farfoliage-thinned", PerSecond(() => FarFoliage.Thinned))
            .Value("farfoliage-dropped", PerSecond(() => FarFoliage.Dropped))
            .Value("farfoliage-marked", PerSecond(() => FarFoliage.Marked))
            .Section("farlod")
            .Value("farlod-from", () => FarLod.Threshold, "m")
            .Value("farlod-chunks", () => FarLod.Installed ? FarLod.FarChunks : double.NaN)
            .Value("farlod-farthest", () => FarLod.Installed ? FarLod.FarthestChunks : double.NaN);
        LightCounters(panel);
        EntityCounters(panel);
        GarbageCounters(panel);
    }

    private void LightCounters(HudPanel panel)
    {
        _ = panel.Section("owntess")
            .Warn("owntess-unpatched", () => !OwnTessellation.Installed)
            .Warn("owntess-other", () => OwnTessellation.StoodDown || OwnTessellation.JsonStoodDown)
            .Bar("owntess-engine", Share(() => OwnTessellation.EngineBlocks, () => OwnTessellation.Blocks),
                PerFrame(() => OwnTessellation.EngineBlocks))
            .Value("owntess-blocks", PerFrame(() => OwnTessellation.Blocks))
            .Value("owntess-json", PerFrame(() => OwnTessellation.JsonBlocks))
            .Section("facelight")
            .Warn("facelight-unpatched", () => !FaceLight.Installed)
            .Warn("facelight-other", () => FaceLight.StoodDown)
            .Bar("facelight-engine", Share(() => FaceLight.EngineFaces, () => FaceLight.FastFaces),
                PerFrame(() => FaceLight.EngineFaces))
            .Value("facelight-faces", PerFrame(() => FaceLight.FastFaces))
            .Value("facelight-blocks", PerFrame(() => FaceLight.FusedBlocks))
            .Section("particlelight")
            .Warn("particlelight-unpatched", () => !ParticleLight.Installed)
            .Bar("particlelight-busy", Ratio(() => ParticleLight.Busy, () => ParticleLight.Reads, 100),
                PerFrame(() => ParticleLight.Busy))
            .Value("particlelight-reads", PerFrame(() => ParticleLight.Reads))
            .Value("particlelight-packed", PerFrame(() => ParticleLight.Packed));
    }

    private void EntityCounters(HudPanel panel)
    {
        _ = panel.Section("animframes")
            .Warn("anim-fast-blocked", () => AnimationFrames.Blocked)
            .Bar("anim-hitrate", Share(() => AnimationFrames.Hits, () => AnimationFrames.Misses))
            .Value("anim-compile-worst", () => AnimationFrames.WorstMs, "ms")
            .Line(() => AnimationFrames.WorstCode, sub: true)
            .Value("anim-hits", PerSecond(() => AnimationFrames.Hits), "/s")
            .Value("anim-misses", PerSecond(() => AnimationFrames.Misses), "/s")
            .Peaks(AnimationFrames.ResetPeaks)
            .Section("entitytess")
            .Warn("shapememo-blocked", () => ShapeInitMemo.Blocked)
            .Value("shapememo-skipped", PerSecond(() => ShapeInitMemo.Skipped), "/s")
            .Section("entitytimes")
            .Warn("entitytimes-unpatched", () => !EntityTimes.Installed)
            .Rows(EntityTimes.Parts, part => EntityTime(panel, part))
            .Peaks(EntityTimes.ResetPeaks);
    }

    private void EntityTime(HudPanel panel, int part)
    {
        string[] keys = ["entitytimes-tick", "entitytimes-animation", "entitytimes-prepare"];
        _ = panel.Value(keys[part], PerFrame(() => EntityTimes.Ticks(part), FrameClock.TickMs), "ms")
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
            .Value("garbage-decompress", PerFrame(() => DecompressScratch.Saved, Mebibyte), "MB")
            .Value("garbage-noise", PerFrame(() => ColumnNoiseScratch.Saved, Mebibyte), "MB")
            .Value("garbage-light", PerFrame(() => LightScratch.Avoided))
            .Value("garbage-partitions", PerFrame(() => PartitionReuse.Reused))
            .Value("garbage-bus-swept", PerFrame(() => EventBusSweep.Swept))
            .Value("garbage-bus-listeners", () => EventBusSweep.Listeners);
    }

    private HudPanel Panel()
    {
        var panel = new HudPanel();
        _panels.Add(panel);
        return panel;
    }
}
