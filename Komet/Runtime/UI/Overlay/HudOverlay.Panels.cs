using System.Reflection;
using Komet.Features;
using Komet.Runtime.Diagnostics;
using Vintagestory.Client;

namespace Komet.Runtime.UI.Overlay;

internal sealed partial class HudOverlay
{
    private const int PanelCount = 8;

    private void BuildPanels()
    {
        if (!Assert(_panels.Count == 0) || !NotNull(_capi.ModLoader)) return;
        var mod = _capi.ModLoader.GetMod("komet");
        var version = NotNull(mod) ? mod.Info.Version : "?";
        var debug = typeof(HudOverlay).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Debug";
        var edition = debug ? (HudSettings.Translate("hud-edition-dev"), HudCanvas.Accent) : (HudSettings.Translate("hud-edition-release"), new Rgba(0.20, 0.60, 0.30, 1));

        _ = Panel(column: 0)
            .Title("title", edition, (HudSettings.Translate("hud-build", version), HudCanvas.Neutral))
            .Value("fps",             () => _frames.Fps)
            .Value("low1",            () => _frames.Low1Fps)
            .Value("low01",           () => _frames.Low01Fps)
            .Value("frametime-avg",   () => _frames.AverageMs, "ms")
            .Value("frametime-worst", () => _frames.WorstMs, "ms")
            .Value("gpu",             () => _gpu.Ms, "ms")
            .Value("draw-calls",      () => _frames.DrawCallsPerFrame);

        _ = Panel(column: 0, enabled: () => _settings.ShowGraph)
            .Section("graph", HudCanvas.GraphFrames)
            .Graph(_frames);

        _ = Panel(column: 0, refreshEvery: HudSettings.SlowEvery, enabled: () => _settings.ShowSystem)
            .Section("world")
            .Value("tris-rendered",     () => _frames.RenderedTriangles)
            .Value("tris-allocated",    () => _frames.AvailableTriangles)
            .Value("chunks-loaded",     () => RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded)
            .Value("chunks-tesselate",  () => RuntimeStats.chunksAwaitingTesselation)
            .Value("chunks-upload",     () => RuntimeStats.chunksAwaitingPooling)
            .Value("entities-rendered", () => RuntimeStats.renderedEntities)
            .Value("entities-loaded",   () => _capi.World.LoadedEntities.Count)
            .Section("cpu")
            .Bar("cpu-process", () => _resources.CpuPercent)
            .Bar("cpu-system",  () => _resources.SystemCpuPercent)
            .Section("memory")
            .Bar("ram-used",         () => _resources.PercentOfRam(_resources.UsedRamMb), () => _resources.UsedRamMb, "MB")
            .Value("ram-available",  () => _resources.AvailableRamMb, "MB")
            .Value("ram-total",      () => _resources.TotalRamMb, "MB")
            .Bar("working-set",      () => _resources.PercentOfRam(_resources.WorkingSetMb), () => _resources.WorkingSetMb, "MB")
            .Value("heap-used",      () => _resources.ManagedUsedMb, "MB", sub: true)
            .Value("heap-committed", () => _resources.ManagedCommittedMb, "MB", sub: true)
            .Value("native",         () => _resources.NativeMb, "MB", sub: true)
            .Bar("vram",             () => 100 * _gpu.VramUsedMb / _gpu.VramTotalMb, () => _gpu.VramUsedMb, "MB")
            .Value("vram-total",     () => _gpu.VramTotalMb, "MB")
            .Value("gc0", () => _resources.Gen0PerSec, "/s")
            .Value("gc2", () => _resources.Gen2PerSec, "/s");

        var passes = Panel(column: 1, enabled: () => _settings.ShowPasses).Section("passes");
        _ = passes.Rows(RenderPassStats.Count, i => passes
            .Line(() => HudSettings.Translate("hud-pass-" + _passes.Key(i)), () => _passes.AverageMs(i), "ms", percent: () => _passes.Percent(i), marker: () => _passes.WorstPercent(i))
            .Rows(RenderPassStats.DetailCount, j => passes.Line(() => _passes.DetailName(i, j), () => _passes.DetailMs(i, j), "ms", sub: true, detail: true)));
        _ = passes.Section("total").Value("frame", () => _passes.AverageTotalMs, "ms");

        var times = Panel(column: 2, enabled: () => _settings.ShowModTimes).Section("modtimes");
        _ = times.Rows(ModTimings.MaxMods, i => times
            .Line(() => _timings.ModName(i), () => _timings.ModMs(i), "ms", percent: () => 100 * _timings.ModMs(i) / _frames.AverageMs)
            .Rows(ModTimings.DetailCount, j => times.Line(() => _timings.DetailName(i, j), () => _timings.DetailMs(i, j), "ms", sub: true, detail: true)));

        LogPanel("log", "client-main.log", () => _settings.ShowLog);
        LogPanel("debuglog", "client-debug.log", () => _settings.ShowDebugLog);

        var mods = Panel(column: 1, refreshEvery: HudSettings.SlowEvery, enabled: () => _settings.ShowMods);
        _ = mods.Section("mods")
            .Value("mods-loaded", () => _mods.Mods)
            .Rows(ModStats.MaxMods, i => mods.Line(() => _mods.ModNames[i] ?? "", sub: true))
            .Section("harmony")
            .Value("harmony-methods", () => _mods.PatchedMethods)
            .Value("harmony-owners", () => _mods.Owners)
            .Rows(ModStats.MaxOwners, i => mods.Line(() => _mods.OwnerList[i].Name ?? "", () => _mods.OwnerList[i].Methods, sub: true))
            .Section("conflicts")
            .Value("conflicts-count", () => _mods.Conflicts)
            .Rows(ModStats.MaxConflicts, i => mods.Line(() => _mods.ConflictNames[i] ?? "", sub: true))
            .Section("shadercache")
            .Value("use-calls",   () => (double)ShaderUseCache.Calls / Math.Max(1, _frames.Frames))
            .Value("use-uploads", () => (double)ShaderUseCache.Uploads / Math.Max(1, _frames.Frames))
            .Bar("use-skipped",   () => 100.0 * ShaderUseCache.Skips / Math.Max(1, ShaderUseCache.Skips + ShaderUseCache.Uploads),
                                  () => (double)ShaderUseCache.Skips / Math.Max(1, _frames.Frames));
        _ = Assert(_panels.Count == PanelCount);
    }

    private void LogPanel(string key, string fileName, Func<bool> enabled)
    {
        if (!Assert(key.Length > 0) || !Assert(fileName.EndsWith(".log", StringComparison.Ordinal))) return;
        var log = new LogStats(fileName);
        var panel = Panel(column: 2, refreshEvery: HudSettings.SlowEvery, enabled: enabled, log: log)
            .Section(key)
            .Line(() => log.Offset == 0 ? "" : HudSettings.Translate("hud-log-scrolled", log.Offset));
        _ = panel.Rows(LogStats.MaxRows, i => panel.Line(() => log.Line(i), sub: true, color: () => log.Level(i) switch
        {
            LogLevel.Warning => HudCanvas.Warning,
            LogLevel.Error => HudCanvas.Error,
            LogLevel.Debug => HudCanvas.Dim,
            _ => null,
        }));
    }

    private HudPanel Panel(int column, int refreshEvery = 1, Func<bool>? enabled = null, LogStats? log = null)
    {
        var panel = new HudPanel(_capi, _settings, _panels.Count, Index(column, 3) ? column : 0, Assert(refreshEvery > 0) ? refreshEvery : 1, enabled, log);
        _ = Assert(_panels.Count < PanelCount);
        _panels.Add(panel);   // the list owns and disposes every panel
        return panel;
    }
}
