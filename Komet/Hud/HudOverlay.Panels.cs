using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime;
using System.Text;
using Vintagestory.Client;

namespace Komet.Hud;

internal sealed partial class HudOverlay
{
    private const int PanelCount = 10;
    private const string Gen0Size = "DOTNET_GCgen0size";
    private const double Mebibyte = 1.0 / 1024 / 1024;

    // Assertions do not inline without the optimizer, so every Komet timing below is inflated several times over
    private static readonly bool DebugBuild =
        typeof(HudOverlay).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Debug";

    private static readonly Func<string> ServerLocal = HudText.Once("hud-server-local"),
        ServerRemote = HudText.Once("hud-server-remote");

    private static string Metadata(Assembly assembly, string key)
    {
        return !NotNull(assembly) || !Assert(key.Length > 0)
            ? ""
            : assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Take(64).FirstOrDefault(a => a.Key == key)
                ?.Value ?? "";
    }

    // "" and no colour while notices are off or nothing has been checked
    private string UpdateText()
    {
        return _settings.UpdateCheck && _update is { } check
            ? HudText.Translate(check.Report.Notice().Key, check.Report.Detail)
            : "";
    }

    private Rgba? UpdateColor()
    {
        return _settings.UpdateCheck && _update is { } check ? check.Report.Notice().Color : null;
    }

    // Version, channel and commit as CI stamped them, and the zip the mod was loaded from: what the title badges and the update check need
    private static (string Version, bool Preview, string Commit, string SourcePath) ReadBuild(ICoreClientAPI capi)
    {
        var mod = capi.ModLoader.GetMod(KometModSystem.ModId);
        var assembly = typeof(HudOverlay).Assembly;
        if (!NotNull(mod) || !Assert(mod.Info.Version.Length > 0)) return ("", false, "", "");
        return (mod.Info.Version, Metadata(assembly, "Channel") == "preview", Metadata(assembly, "Commit"),
            mod.SourcePath ?? "");
    }

    // Edition (dev build, CI preview or release) and build (version, plus commit and time when CI stamped them)
    private (string Text, Rgba Color)[] TitleBadges()
    {
        var assembly = typeof(HudOverlay).Assembly;
        var built = HudText.LocalTime(Metadata(assembly, "Built"));
        var (version, preview, commit, _) = _build;
        // CI stamps channel and commit together
        if (!Assert(version.Length > 0) || !Assert(!preview || commit.Length > 0)) return [];
        var edition = (DebugBuild, preview) switch
        {
            (true, _) => (HudText.Translate("hud-edition-dev"), HudCanvas.Accent),
            (_, true) => (HudText.Translate("hud-edition-preview"), new Rgba(0.80, 0.50, 0.15, 1)),
            _ => (HudText.Translate("hud-edition-release"), new Rgba(0.20, 0.60, 0.30, 1))
        };
        var build = (commit.Length > 0, built.Length > 0) switch
        {
            (true, true) => HudText.Translate("hud-build-stamped", version, commit, built),
            (true, false) => HudText.Translate("hud-build-commit", version, commit),
            _ => HudText.Translate("hud-build", version)
        };
        return [edition, (build, HudCanvas.Neutral)];
    }

    // The collector reads its configuration at startup, before any mod system runs: Komet reports the effective values it finds. The
    // mode on one line and the budget on the next, one line of both widened the system panel's whole column.
    private static (string Mode, string Budget) GcConfig()
    {
        var config = GC.GetConfigurationVariables();
        var mode = GCSettings.IsServerGC
            ? HudText.Translate("hud-gc-server", ConfigValue(config, "HeapCount"),
                ConfigValue(config, "GCDynamicAdaptationMode"))
            : HudText.Translate("hud-gc-workstation");
        var concurrent = HudText.Translate(config.TryGetValue("ConcurrentGC", out var bgc) && bgc is true
            ? "hud-gc-concurrent"
            : "hud-gc-blocking");
        var budget = ConfigValue(config, "GCGen0MaxBudget");
        var size = Environment.GetEnvironmentVariable(Gen0Size) ?? "";
        if (!Assert(size.Length < 64) || !Assert(config.Count > 0)) size = "";
        return (mode + ", " + concurrent,
            (budget > 0 ? HudText.Format(budget * Mebibyte, "N0") + " MB" : "?") +
            (size.Length > 0 ? $", {Gen0Size}={size}" : ""));
    }

    // -1 for a key this runtime does not report
    private static long ConfigValue(IReadOnlyDictionary<string, object> config, string key)
    {
        if (!NotNull(config) || !Assert(key.Length > 0)) return -1;
        return config.TryGetValue(key, out var value) && value is IConvertible number
            ? number.ToInt64(CultureInfo.InvariantCulture)
            : -1;
    }

    // The latency mode is the one setting a program may change while it runs
    private string GcText()
    {
        return Assert(_gc.Mode.Length > 0) && Assert(Enum.IsDefined(GCSettings.LatencyMode))
            ? HudText.Translate("hud-gc-latency", _gc.Budget, GCSettings.LatencyMode)
            : "";
    }

    // Mark names without the namespaces the engine puts into them (initbebehavior-Vintagestory.GameContent.BEBehaviorFruitingBush
    // reads as initbebehavior-BEBehaviorFruitingBush), so one long type name cannot widen a whole column; the ledger's pseudo causes
    // in words
    private static string Describe(string? name)
    {
        if (string.IsNullOrEmpty(name) || !Assert(name.Length < 1024)) return "";
        if (name[0] == '~' && Assert(name.Length > 1)) return HudText.Cached("hud-cause-", name);
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return name;
        var dash = name.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 && dash < dot
            ? string.Concat(name.AsSpan(0, dash + 1), name.AsSpan(dot + 1))
            : name[(dot + 1)..];
    }

    private string SpikeCause(int place)
    {
        if (!Index(place, SpikeLedger.Shown) || _spikes.Ranked(place) is not { Name.Length: > 0 } cause) return "";
        return Assert(cause.Count > 0)
            ? HudText.Translate("hud-spike-cause", Describe(cause.Name), cause.Count, HudText.Format(cause.MaxMs, "F1"))
            : "";
    }

    // The latest spike frames whole, newest first; the panel rows only name their causes
    private void AppendSpikes(StringBuilder text)
    {
        if (_spikes.Stored == 0 || !Assert(_spikes.Stored <= SpikeLedger.Recent)) return;
        _ = text.Append("\n\n[").Append(HudText.Translate("hud-spikes-recent")).Append(']');
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
                // one mark summed over its calls, e.g. nine entities
                if (entry.Calls > 1) _ = marks.Append(" ×").Append(entry.Calls);
            }

            _ = text.Append('\n').Append(HudText.Translate("hud-spike-line", HudText.Format(spike.AtSeconds, "F1"),
                Millis(spike.DtMs), Describe(spike.Cause), Millis(spike.GcMs), Millis(spike.OutsideMs),
                Millis(spike.JitMs), Millis(spike.RunQueueMs), marks.ToString()));
        }
    }

    // NaN is a column that was not measured (no profile, no schedstat); infinite would be a broken clock
    private static string Millis(double ms)
    {
        if (double.IsNaN(ms)) return "–";
        return Finite(ms) && Assert(ms >= 0) ? HudText.Format(ms, "F1") : "";
    }

    // One walk per game start, not per interval: running while the world loads, then done, or cancelled by leaving the world or the switch
    private static string PreJitText()
    {
        var key = PreJit.State switch
        {
            PreJitState.Running => "hud-prejit-running",
            PreJitState.Done => "hud-prejit-done",
            PreJitState.Cancelled => "hud-prejit-cancelled",
            _ => "hud-prejit-idle"
        };
        return Assert(PreJit.Prepared >= 0) && Finite(PreJit.JitMs) ? HudText.Translate(key) : "";
    }

    // scale × the growth of part per growth of whole, both since this row's previous read: per frame, per second, per pass, a share
    private static Func<double> Ratio(Func<double> part, Func<double> whole, double scale = 1)
    {
        Growth grown = new(part), of = new(whole);
        return Assert(Finite(scale)) && NotNull(whole)
            ? () => scale * grown.Next() / of.Next()
            : static () => double.NaN;
    }

    private Func<double> PerFrame(Func<double> total, double scale = 1)
    {
        return Assert(scale > 0) && NotNull(total) ? Ratio(total, () => _frameTotal, scale) : static () => double.NaN;
    }

    private Func<double> PerSecond(Func<double> total, double scale = 1)
    {
        return Assert(scale > 0) && NotNull(total) ? Ratio(total, () => _elapsedTotal, scale) : static () => double.NaN;
    }

    // A cap the transpiler could not place would leave the engine's budget alone without a word
    private static string UploadCapText()
    {
        var millis = ChunkBudget.CapMillis;
        if (!Assert(millis is >= ChunkBudget.Uncapped and <= ChunkBudget.MaxCapMillis)) return "";
        if (millis == ChunkBudget.Uncapped) return HudText.Translate("hud-uploadcap-off");
        return HudText.Translate(ChunkBudget.Capped ? "hud-uploadcap-active" : "hud-uploadcap-unpatched", millis);
    }

    // The index of a panel is the key of its pinned position: the order is persisted, new panels go last
    private void BuildPanels()
    {
        if (!Assert(_panels.Count == 0) || !NotNull(_capi.ModLoader)) return;
        FramesPanel();
        GraphPanel();
        SystemPanel();
        PassesPanel();
        ModTimesPanel();
        LogPanel("log", "client-main.log", () => _settings.ShowLog);
        LogPanel("debuglog", "client-debug.log", () => _settings.ShowDebugLog);
        ModsPanel();
        RenderCounters(Panel(2, HudSettings.SlowEvery, () => _settings.ShowCounters));
        WorldCounters(Panel(2, HudSettings.SlowEvery, () => _settings.ShowCounters));
        _ = Assert(_panels.Count == PanelCount);
    }

    private void FramesPanel()
    {
        if (!Assert(_panels.Count == 0)) return;
        _ = Panel(0)
            .Title("title", TitleBadges())
            .Line(UpdateText, sub: true, color: UpdateColor)
            .Value("fps", () => _frames.Fps)
            .Value("low1", () => _frames.Low1Fps, final: true)
            .Value("low01", () => _frames.Low01Fps, final: true)
            .Value("frametime-avg", () => _frames.AverageMs, "ms")
            .Value("frametime-worst", () => _frames.WorstMs, "ms", final: true)
            .Value("frametime-worst-gc", () => _frames.WorstGcMs, "ms", true, final: true)
            .Value("gpu", () => _gpu.Ms, "ms")
            .Value("draw-calls", () => _frames.DrawCallsPerFrame);
    }

    // The spikes sit under the graph they explain
    private void GraphPanel()
    {
        if (!Assert(_panels.Count == 1)) return;
        var graph = Panel(0, enabled: () => _settings.ShowGraph);
        _ = graph.Section("graph", HudCanvas.GraphFrames)
            .Graph(_frames)
            .Section("spikes", SpikeLedger.MinMs, SpikeLedger.MeanFactor)
            .Value("spikes-count", () => _spikes.Count)
            .Value("spikes-threshold", () => _spikes.ThresholdMs, "ms", true)
            .Rows(SpikeLedger.Shown,
                i => graph.Line(() => SpikeCause(i), () => _spikes.Ranked(i).AverageMs, "ms", sub: true));
    }

    private void SystemPanel()
    {
        if (!Assert(_panels.Count == 2)) return;
        _ = Panel(0, HudSettings.SlowEvery, () => _settings.ShowSystem)
            .Section("world")
            .Value("tris-rendered", () => _frames.RenderedTriangles)
            .Value("tris-allocated", () => _frames.AvailableTriangles, detail: true)
            .Value("chunks-loaded", () => RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded)
            .Value("chunks-tesselate", () => RuntimeStats.chunksAwaitingTesselation)
            .Value("chunks-upload", () => RuntimeStats.chunksAwaitingPooling)
            .Value("entities-rendered", () => RuntimeStats.renderedEntities, detail: true)
            .Value("entities-loaded", () => _capi.World.LoadedEntities.Count)
            .Section("cpu")
            .Bar("cpu-process", () => _resources.CpuPercent)
            .Bar("cpu-system", () => _resources.SystemCpuPercent)
            .Section("memory")
            .Bar("ram-used", () => _resources.PercentOfRam(_resources.UsedRamMb), () => _resources.UsedRamMb, "MB")
            .Value("ram-available", () => _resources.AvailableRamMb, "MB")
            .Value("ram-total", () => _resources.TotalRamMb, "MB", detail: true)
            .Bar("working-set", () => _resources.PercentOfRam(_resources.WorkingSetMb), () => _resources.WorkingSetMb,
                "MB")
            .Value("heap-used", () => _resources.ManagedUsedMb, "MB", true, true)
            .Value("heap-committed", () => _resources.ManagedCommittedMb, "MB", true, true)
            .Value("native", () => _resources.NativeMb, "MB", true, true)
            .Bar("vram", () => 100 * _gpu.VramUsedMb / _gpu.VramTotalMb, () => _gpu.VramUsedMb, "MB")
            .Value("vram-total", () => _gpu.VramTotalMb, "MB", detail: true)
            .Value("vram-evictions", () => _gpu.EvictionsPerSec, "/s", true, true)
            .Bar("gc-pause", () => _resources.GcPausePercent)
            .Value("gc-alloc", () => _resources.AllocatedMbPerSec, "MB")
            .Value("gc0", () => _resources.Gen0PerSec, "/s")
            .Value("gc2", () => _resources.Gen2PerSec, "/s")
            .Value("climate-cache", () => ClimateCache.Capacity, sub: true, detail: true)
            .Line(() => _gc.Mode, sub: true, detail: true)
            .Line(GcText, sub: true, detail: true);
    }

    private void PassesPanel()
    {
        if (!Assert(_panels.Count == 3)) return;
        var passes = Panel(1, enabled: () => _settings.ShowPasses).Section("passes");
        _ = passes.Rows(RenderPassStats.Count, i => passes
                .Line(() => HudText.Cached("hud-pass-", _passes.Key(i)), () => _passes.AverageMs(i), "ms",
                    () => _passes.Percent(i), () => _passes.WorstPercent(i))
                .Rows(RenderPassStats.DetailCount,
                    j => passes.Line(() => Describe(_passes.DetailName(i, j)), () => _passes.DetailMs(i, j), "ms",
                        sub: true, detail: true)))
            .Section("total")
            .Value("frame", () => _passes.AverageTotalMs, "ms")
            .Section("worstframe")
            .Value("worstframe-total", () => _passes.WorstFrameMs, "ms")
            .Rows(RenderPassStats.DetailCount,
                i => passes.Line(() => Describe(_passes.WorstMarkName(i)), () => _passes.WorstMarkMs(i), "ms",
                    sub: true));
    }

    private void ModTimesPanel()
    {
        if (!Assert(_panels.Count == 4)) return;
        var times = Panel(2, enabled: () => _settings.ShowModTimes).Section("modtimes");
        _ = times.Rows(ModTimes.MaxMods, i => times
            .Line(() => _timings.ModName(i), () => _timings.ModMs(i), "ms",
                () => 100 * _timings.ModMs(i) / _frames.AverageMs)
            .Rows(ModTimes.DetailCount,
                j => times.Line(() => _timings.DetailName(i, j), () => _timings.DetailMs(i, j), "ms", sub: true,
                    detail: true)));
    }

    private void LogPanel(string key, string fileName, Func<bool> enabled)
    {
        if (!Assert(key.Length > 0) || !Assert(fileName.EndsWith(".log", StringComparison.Ordinal))) return;
        var log = new LogStats(fileName);
        var panel = Panel(2, HudSettings.SlowEvery, enabled, log)
            .Section(key)
            .Line(() => log.Offset == 0 ? "" : HudText.Translate("hud-log-scrolled", log.Offset));
        _ = panel.Rows(LogStats.MaxRows, i => panel.Line(() => log.Row(i).Text, sub: true, color: () =>
            log.Row(i).Level switch
            {
                LogLevel.Warning => HudCanvas.Warning,
                LogLevel.Error => HudCanvas.Error,
                LogLevel.Debug => HudCanvas.Dim,
                _ => null
            }));
    }

    private void ModsPanel()
    {
        if (!Assert(_panels.Count == 7)) return;
        var mods = Panel(1, HudSettings.SlowEvery, () => _settings.ShowMods);
        _ = mods.Section("mods")
            .Value("mods-loaded", () => _mods.Snapshot.Mods)
            .Rows(ModStats.MaxMods, i => mods.Line(() => _mods.Snapshot.ModNames[i] ?? "", sub: true))
            .Section("harmony")
            .Line(() => KometModSystem.LocalServer ? ServerLocal() : ServerRemote())
            .Value("harmony-methods", () => _mods.Snapshot.PatchedMethods)
            .Value("harmony-owners", () => _mods.Snapshot.Owners)
            .Rows(ModStats.MaxOwners,
                i => mods.Line(() => _mods.Snapshot.OwnerList[i].Name ?? "", () => _mods.Snapshot.OwnerList[i].Methods,
                    sub: true))
            .Section("conflicts")
            .Value("conflicts-count", () => _mods.Snapshot.Conflicts)
            .Rows(ModStats.MaxConflicts, i => mods.Line(() => _mods.Snapshot.ConflictNames[i] ?? "", sub: true))
            .Section("features")
            .Value("features-off", () => Features.NotActive)
            .Rows(Features.MaxShown, i => mods.Line(() => Features.Shown(i), sub: true, color: () =>
                Features.ShownState(i) is FeatureState.HeldOff or FeatureState.StoodDown ? HudCanvas.Warning : null));
    }

    // Komet's feature counters on the GPU side. Each section shows its headline row; the rest wait for the detail lines.
    private void RenderCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount - 1)) return;
        _ = panel.Section("shadercache")
            .Bar("use-skipped",
                Ratio(() => ShaderUseCache.Skips, () => ShaderUseCache.Skips + ShaderUseCache.Uploads, 100),
                PerFrame(() => ShaderUseCache.Skips), good: true)
            .Value("use-calls", PerFrame(() => ShaderUseCache.Calls), detail: true)
            .Value("use-uploads", PerFrame(() => ShaderUseCache.Uploads), detail: true)
            .Section("frustumsweep")
            .Warn("debug-timings", () => DebugBuild)
            .Value("cull-time", PerFrame(() => FrustumSweep.Ticks, FrameClock.TickMs), "ms")
            .Bar("cull-skipped", Ratio(() => FrustumSweep.Skipped, () => FrustumSweep.Tested, 100),
                PerFrame(() => FrustumSweep.Skipped), good: true)
            .Value("cull-tested", PerFrame(() => FrustumSweep.Tested), detail: true)
            .Value("cull-visible", PerFrame(() => FrustumSweep.Visible), detail: true)
            .Value("cull-rebuilt", PerFrame(() => FrustumSweep.Rebuilds), detail: true)
            .Value("cull-diffed", PerFrame(() => FrustumSweep.Diffed), detail: true)
            .Value("cull-rebuilt-max", () => FrustumSweep.MaxRebuilds, detail: true)
            .Value("cull-sorted", PerFrame(() => FrustumSweep.Settles), detail: true)
            .Peaks(FrustumSweep.ResetPeaks)
            .Section("indirectdraw")
            .Warn("draw-unsupported", () => IndirectDraw.Detected && !IndirectDraw.Supported)
            .Warn("draw-unmapped", () => IndirectDraw.Supported && !IndirectDraw.Persistent)
            .Value("draw-calls-indirect", PerFrame(() => IndirectDraw.Draws), detail: true)
            .Value("draw-ranges", PerFrame(() => IndirectDraw.Ranges), detail: true);
        UploadCounters(panel);
    }

    private void UploadCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount - 1)) return;
        _ = panel.Section("meshpool")
            .Value("pool-time", PerFrame(() => MeshPool.Ticks, FrameClock.TickMs), "ms")
            .Value("pool-models", PerFrame(() => MeshPool.Models), detail: true)
            .Value("pool-vertices", PerFrame(() => MeshPool.Vertices), detail: true)
            .Value("pool-skipped", PerFrame(() => MeshPool.Skipped), detail: true)
            .Value("frag-removed", PerFrame(() => MeshPool.Removed), detail: true)
            .Bar("frag-squeeze-skipped", Ratio(() => MeshPool.Skips, () => MeshPool.Squeezes, 100),
                PerFrame(() => MeshPool.Skips), good: true, detail: true)
            .Value("frag-recounts", PerFrame(() => MeshPool.Recounts), detail: true)
            .Section("meshrecycle")
            .Value("recycle-saved", PerFrame(() => MeshRecycle.Saved, 4 * Mebibyte), "MB")
            .Value("recycle-clones", PerFrame(() => MeshRecycle.Clones), detail: true)
            .Value("recycle-reused", PerFrame(() => MeshRecycle.Reused), detail: true)
            .Section("chunkbudget")
            .Line(UploadCapText,
                color: () =>
                    ChunkBudget.Capped || ChunkBudget.CapMillis == ChunkBudget.Uncapped ? null : HudCanvas.Warning)
            .Bar("upload-capped", PerFrame(() => ChunkBudget.CapHits, 100));
    }

    // Chunks from arrival to mesh, entities and animation, garbage avoided and the start-up compile
    private void WorldCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount)) return;
        Func<double> passes = () => TessAccounting.Totals().Passes, passMs = () => TessAccounting.Totals().Ms;
        var busy = PerSecond(passMs, 0.1); // percent of one thread
        _ = panel.Section("chunklookup")
            .Bar("chunk-hitrate", Ratio(() => ChunkLookup.Hits, () => ChunkLookup.Hits + ChunkLookup.Misses, 100),
                good: true)
            .Value("chunk-hits", PerFrame(() => ChunkLookup.Hits), detail: true)
            .Section("tessaccounting")
            .Warn("tess-unpatched", () => !TessAccounting.Installed)
            .Warn("tess-partial", () => TessAccounting.Installed && !OccludedChunks.Installed)
            .Warn("tess-occluded-other", () => OccludedChunks.StoodDown)
            .Warn("tess-workers-unpatched",
                () => WorkerPool.Wanted > 0 && (!TessWorkers.Installed || TessSafety.Broken))
            .Warn("tess-workers-failed", () => TessWorkers.Failed || WorkerPool.BackgroundOff)
            .Value("tess-rate", PerSecond(passes))
            .Value("tess-near", () => TessSchedule.NearWaiting)
            .Value("tess-mean", Ratio(passMs, passes), "ms")
            .Bar("tess-busy", () => busy() / (1 + WorkerPool.Running));
        PoolCounters(panel);
        _ = panel
            .Bar("tess-edge", Ratio(() => TessAccounting.Totals().Edge, passes, 100),
                PerSecond(() => TessAccounting.Totals().Edge), detail: true)
            .Bar("tess-zero", Ratio(() => TessAccounting.ZeroMs, passMs, 100),
                PerSecond(() => TessAccounting.ZeroPasses), detail: true)
            .Value("tess-priority",
                PerSecond(() =>
                    TessAccounting.Count(TessBucket.PriorityFull) + TessAccounting.Count(TessBucket.PriorityEdge)),
                detail: true)
            .Value("tess-skipped", PerSecond(() => TessAccounting.Count(TessBucket.Skipped)), detail: true)
            .Value("tess-requeued", PerSecond(() => TessAccounting.Count(TessBucket.Requeued)), detail: true)
            .Value("tess-occluded", PerSecond(() => OccludedChunks.Hits), detail: true)
            .Section("extendedrows")
            .Warn("extendedrows-unpatched", () => !ExtendedRows.Rewritten)
            .Warn("extendedrows-blocked", () => ExtendedRows.Rewritten && ExtendedRows.Blocked)
            .Value("extendedrows-fallbacks", PerFrame(() => ExtendedRows.Fallbacks))
            .Value("extendedrows-rows", PerFrame(() => ExtendedRows.Decoded), detail: true)
            .Value("extendedrows-cells", PerFrame(() => ExtendedRows.CellsDecoded), detail: true);
        LightCounters(panel);
        EntityCounters(panel);
        GarbageCounters(panel);
    }

    // Komet's worker threads: how their time splits between frame jobs (culling), tessellation and waiting, each a share of all of them
    private void PoolCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount)) return;
        Func<double> frame = PerSecond(() => WorkerPool.FrameTicks * 1000.0 / Stopwatch.Frequency, 0.1),
            tess = PerSecond(() => WorkerPool.BackgroundTicks * 1000.0 / Stopwatch.Frequency, 0.1);

        static double Share(double percent)
        {
            return double.IsFinite(percent) && WorkerPool.Running > 0 ? percent / WorkerPool.Running : double.NaN;
        }

        _ = panel.Section("pool")
            .Warn("pool-frame-off", () => WorkerPool.FrameOff)
            .Value("pool-threads", () => WorkerPool.Running)
            .Line(() => HudText.Translate(TessWorkers.Boosted ? "hud-pool-tess-boost" : "hud-pool-tess-limit",
                WorkerPool.BackgroundLimit, WorkerPool.InBackground), sub: true)
            .Bar("pool-frame", () => Share(frame()))
            .Bar("pool-tess", () => Share(tess()))
            .Bar("pool-idle", () => Share(100 * WorkerPool.Running - frame() - tess()), detail: true);
    }

    private void LightCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount)) return;
        _ = panel.Section("visiblefaces")
            .Warn("visiblefaces-unpatched", () => !VisibleFaces.Installed)
            .Warn("visiblefaces-other", () => VisibleFaces.StoodDown)
            .Bar("visiblefaces-engine",
                Ratio(() => VisibleFaces.Fallbacks, () => VisibleFaces.Chunks + VisibleFaces.Fallbacks, 100),
                PerFrame(() => VisibleFaces.Fallbacks))
            .Value("visiblefaces-chunks", PerFrame(() => VisibleFaces.Chunks), detail: true)
            .Value("visiblefaces-ported",
                Ratio(() => VisibleFaces.PortedCells, () => VisibleFaces.FastCells + VisibleFaces.PortedCells, 100),
                "%", detail: true)
            .Section("facelight")
            .Warn("facelight-unpatched", () => !FaceLight.Installed)
            .Warn("facelight-other", () => FaceLight.StoodDown)
            .Bar("facelight-engine",
                Ratio(() => FaceLight.EngineFaces, () => FaceLight.FastFaces + FaceLight.EngineFaces, 100),
                PerFrame(() => FaceLight.EngineFaces))
            .Value("facelight-faces", PerFrame(() => FaceLight.FastFaces), detail: true)
            .Value("facelight-blocks", PerFrame(() => FaceLight.FusedBlocks), detail: true)
            .Section("particlelight")
            .Warn("particlelight-unpatched", () => !ParticleLight.Installed)
            .Bar("particlelight-busy", Ratio(() => ParticleLight.Busy, () => ParticleLight.Reads, 100),
                PerFrame(() => ParticleLight.Busy))
            .Value("particlelight-reads", PerFrame(() => ParticleLight.Reads), detail: true)
            .Value("particlelight-packed", PerFrame(() => ParticleLight.Packed), detail: true);
    }

    private void EntityCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount)) return;
        _ = panel.Section("animframes")
            .Warn("anim-fast-blocked", () => AnimationFrames.Blocked)
            .Bar("anim-hitrate",
                Ratio(() => AnimationFrames.Hits, () => AnimationFrames.Hits + AnimationFrames.Misses, 100), good: true)
            .Value("anim-compile-worst", () => AnimationFrames.WorstMs, "ms")
            .Line(() => AnimationFrames.WorstCode, sub: true)
            .Value("anim-hits", PerSecond(() => AnimationFrames.Hits), "/s", detail: true)
            .Value("anim-misses", PerSecond(() => AnimationFrames.Misses), "/s", detail: true)
            .Value("anim-init-skipped", PerSecond(() => InitOnce.Skipped), "/s", detail: true)
            .Peaks(AnimationFrames.ResetPeaks)
            .Section("entitytess")
            .Line(() => EntityTessBudget.Substituted || EntityTessBudget.Millis == EntityTessBudget.Engine
                    ? ""
                    : HudText.Translate("hud-entitytess-unpatched", EntityTessBudget.Millis), sub: true,
                color: () => HudCanvas.Warning)
            .Warn("shapememo-blocked", () => ShapeInitMemo.Blocked)
            .Value("entitytess-time", PerFrame(() => EntityTessBudget.Ticks, FrameClock.TickMs), "ms")
            .Value("entitytess-worst", () => EntityTessBudget.WorstFrameMs, "ms")
            .Line(() => EntityTessBudget.SlowestCode.Length == 0
                    ? ""
                    : HudText.Translate("hud-entitytess-slowest", EntityTessBudget.SlowestCode,
                        HudText.Format(EntityTessBudget.SlowestMs, "F1")),
                sub: true)
            .Value("entitytess-count", PerSecond(() => EntityTessBudget.Tesselations), "/s", detail: true)
            .Value("entitytess-deferred", PerSecond(() => EntityTessBudget.Deferred), "/s", detail: true)
            .Value("entitytess-waited", () => EntityTessBudget.MostWaited, detail: true)
            .Value("shapememo-skipped", PerSecond(() => ShapeInitMemo.Skipped), "/s", detail: true)
            .Peaks(EntityTessBudget.ResetPeaks);
    }

    private void GarbageCounters(HudPanel panel)
    {
        if (!Assert(_panels.Count == PanelCount)) return;
        _ = panel.Section("garbage")
            .Warn("garbage-unpatched", () =>
                !(DecompressScratch.Rewritten && LightScratch.Rewritten && ColumnNoiseScratch.Rewritten &&
                  CloudTileScratch.Rewritten))
            .Value("garbage-decompress", PerFrame(() => DecompressScratch.Saved, Mebibyte), "MB")
            .Value("garbage-noise", PerFrame(() => ColumnNoiseScratch.Saved, Mebibyte), "MB")
            .Value("garbage-light", PerFrame(() => LightScratch.Avoided), detail: true)
            .Section("prejit")
            .Line(PreJitText, sub: true, color: () => PreJit.State == PreJitState.Cancelled ? HudCanvas.Warning : null)
            .Value("prejit-methods", () => PreJit.Prepared)
            .Value("prejit-failed", () => PreJit.Failed, detail: true)
            .Value("prejit-jit", () => PreJit.JitMs, "ms", detail: true)
            .Value("prejit-wall", () => PreJit.WallMs, "ms", detail: true);
    }

    private HudPanel Panel(int column, int refreshEvery = 1, Func<bool>? enabled = null, LogStats? log = null)
    {
        var every = Assert(refreshEvery > 0) ? refreshEvery : 1;
        // one panel of a cadence per interval, not all of them at once
        var phase = _panels.Count(p => p.RefreshEvery == every);
        var panel = new HudPanel(_capi, _settings, _fonts, _panels.Count, Index(column, Columns) ? column : 0, every,
            phase, enabled, log);
        _ = Assert(_panels.Count < PanelCount);
        _panels.Add(panel); // the list owns and disposes every panel
        return panel;
    }
}
