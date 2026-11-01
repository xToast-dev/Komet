using Cairo;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using System.Runtime.CompilerServices;
using System.Text;
using Vintagestory.Client;

namespace Komet.Hud;

internal sealed partial class HudOverlay : IRenderer
{
    private const int WarmupMs = 5000, SettleMs = 30000;

    // Not steady after the HUD appears or around a dialog: JIT, first Cairo passes and composition owned the 0.1 % low
    // ~20 s.
    private const int QuietMs = 1000;

    private readonly (string Version, bool Preview, string Commit, string SourcePath) _build;
    private readonly ICoreClientAPI _capi;
    private readonly FrameStats _frames = new();
    private readonly (string Mode, string Budget) _gc = GcConfig(KometText.Translate);
    private readonly GpuStats _gpu = new();
    private readonly List<HudPanel> _panels = [];
    private readonly RenderPassStats _passes = new();
    private readonly ResourceStats _resources = new();
    private readonly HudSettings _settings;
    private readonly SnapGrid _snap;
    private readonly SpikeLedger _spikes = new();
    private readonly ModTimes _timings = new();
    private readonly HudWindow _window;
    private readonly DebugWindow _debugWindow;
    private HudAsk? _ask;
    private long _consumed; // FrameClock records taken
    private bool _disposed, _wasVisible, _profilerOwned, _resumed, _settled;
    private bool _endNow; // a settings change wants fresh numbers at the next frame instead of an interval later
    private double _lastInterval;
    private (bool Showing, bool Threads) _primed;
    private double _elapsedTotal;
    private long _frameTotal; // frames and seconds since the HUD began, what the rows divide counter growth by
    private int _interval;
    private long _saveCallback;
    private long _quietUntil;
    private int _skip;
    private Task? _sampling;
    private Action? _sample;
    private float _sinceInterval, _benchLeft;
    private UpdateCheck? _update;

    internal OptionsScreen Options { get; }

    internal static Action<int>? ShowForBench { get; private set; }

    // Bench-started capture ("gui": "debug"): its end runs under the freeze watchdog
    internal static Action<float>? CaptureForBench { get; private set; }

    internal static Action<bool, string?>? OptionsForBench { get; private set; }

    public HudOverlay(ICoreClientAPI capi)
    {
        _capi = capi;
        _settings = HudSettings.Load(capi);
        _build = ReadBuild(capi);
        _uiFonts.Update(_settings.FontScale);
        _snap = new SnapGrid(capi, Quiet);
        BuildPanels();
        _window = new HudWindow(capi, _settings, _uiFonts, _snap, View, Badges);
        _debugWindow = new DebugWindow(capi, _settings, _uiFonts, this, _snap);
        _window.OpenDebug = () => _debugWindow.Toggle(true);
        Options = new OptionsScreen(capi, _settings,
            new KometActions(Dump, StartBench, OpenVerify, OpenWindow, () => _debugWindow.Toggle(true)));
        (Options.VersionState, Options.OpenVersion) = (VersionState, () => _window.Select(HudWindow.Version));
        if (!NotNull(capi.Event)) return;
        Options.OnOpened += Quiet;
        Options.OnClosed += Quiet;
        Options.Composing += Quiet;
        _settings.RegisterHotkeys(capi, () => _window.Toggle(), () => _debugWindow.Toggle());
        _window.Changed += WindowChanged;
        ShowForBench = ShowTab;
        CaptureForBench = seconds => Capture("record", seconds);
        OptionsForBench = (open, page) => _ = open ? Options.Open(page) : Options.TryClose();
        _window.Composing += Skip;
        _debugWindow.Composing += Skip;
        RegisterDebug(capi);
        _settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        capi.Event.LevelFinalize += AskForUpdateCheck;
        capi.Event.LevelFinalize += LevelReady;
        Warm(capi.Logger, _uiFonts);
    }

    private void ShowTab(int lap)
    {
        if (!Assert(lap >= 0) || _disposed) return;
        if (lap < HudWindow.Tabs.Length) _window.Toggle(lap);
        else
        {
            _ = !_window.IsOpened() || _window.TryClose();
            _debugWindow.Toggle(true);
        }
    }

    private bool Showing => _settings.Visible || _window.IsOpened();

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (_joinPending && stage == EnumRenderStage.Before) RecordJoin();
        if (!Showing && !Forced)
        {
            // the shader card shows with the HUD hidden too; a query begun while it showed still ends
            if (stage == EnumRenderStage.Ortho) DrawShaders();
            else if (stage == EnumRenderStage.Done) _gpu.End();
            return;
        }

        if (stage == EnumRenderStage.Before) _gpu.Begin();
        else if (stage == EnumRenderStage.Done)
        {
            _gpu.End();
            RenderCost.EndFrame();
        }
        else Render(deltaTime);
    }

    // Registered for three stages: may be called three times
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var events = _capi.Event; // null when the constructor stood down early (headless, tests)
        events?.UnregisterCallback(_saveCallback);
        _window.Changed -= WindowChanged;
        _settings.Changed -= OnSettingsChanged; // a late change would switch the collectors on again
        (ShowForBench, CaptureForBench, OptionsForBench) = (null, null, null);
        _ = _events?.Stop(); // a capture the world's end cut short
        _window.Dispose();
        _overlayCanvas?.Dispose();
        _toastCanvas?.Dispose();
        _shadersCanvas?.Dispose();
        _debugWindow.Dispose();
        if (events is not null)
        {
            events.LevelFinalize -= AskForUpdateCheck;
            events.LevelFinalize -= LevelReady;
        }

        Options.OnOpened -= Quiet;
        Options.OnClosed -= Quiet;
        Options.Composing -= Quiet;
        _update?.Dispose();
        _ask?.Dispose();
        _settings.Save(_capi);
        Options.Dispose();
        _snap.Dispose();
        _gpu.Dispose();
        if (_profilerOwned) _capi.World.FrameProfiler.Enabled = false;
        (_debug, KometDebug.Capturing) = (null, false);
        ModTimes.Enabled = FrameClock.Stats = Counting.Hud = WorkerPool.Timed = false;
    }

    double IRenderer.RenderOrder => 0.9;
    int IRenderer.RenderRange => int.MaxValue;

    // A click on "check" is consent for that one check.
    private void RunUpdateCheck()
    {
        if (!Assert(_build.Version.Length > 0)) return;
        if (_update is null)
            _update = new UpdateCheck(_capi.Logger, _build.Version, _build.Preview, _build.Commit, _build.SourcePath);
        else _update.Start();
    }

    // From the options screen, which closes so the window is not hidden behind it
    private void OpenWindow()
    {
        _ = !Options.IsOpened() || Options.TryClose();
        _window.Toggle(_window.Tab);
    }

    private void OpenVerify()
    {
        if (_update is null) RunUpdateCheck();
        _ = !Options.IsOpened() || Options.TryClose();
        _window.Toggle(HudWindow.Version);
    }

    // Once the world is up: api.github.com only after a yes.
    private void AskForUpdateCheck()
    {
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        if (_settings.UpdateAsked || !Assert(_build.Version.Length > 0)) return;
        Quiet();
        _ask = new HudAsk(_capi, _settings, _uiFonts, _snap, (KometText.Translate("update-ask-title"), KometText.Translate("update-ask")),
            yes => (_settings.UpdateAsked, _settings.UpdateCheck) = (true, yes));
        _ask.OnClosed += Quiet;
        if (!_ask.TryOpen())
            _capi.Logger.Warning("Komet: update opt-in dialog could not be opened, update notices stay off");
    }

    private void Skip()
    {
        if (_disposed) return;
        _skip = 2;
    }

    private void Quiet()
    {
        _quietUntil = Math.Max(_quietUntil, Environment.TickCount64 + QuietMs);
    }

    private string Means()
    {
        var text = new StringBuilder();
        foreach (var panel in _panels.Bounded(PanelCount)) panel.AppendMeans(text);
        if (text.Length > 0) AppendSpikes(text);
        return text.ToString();
    }

    private void OnSettingsChanged()
    {
        var shown = _settings.Visible && !_wasVisible;
        if (shown) Quiet();
        if (shown || !Near(_settings.Interval, _lastInterval)) (_endNow, _interval) = (true, 0);
        (_wasVisible, _lastInterval) = (_settings.Visible, _settings.Interval);
        _window.Invalidate(true);
        _overlayDirty = true;

        if (_settings.UpdateCheck && _update is null) RunUpdateCheck();
        UpdateProfiler();
        _capi.Event.UnregisterCallback(_saveCallback); // coalesces slider drags and hotkey bursts into one write
        // the options screen pauses singleplayer; without the flag the engine refuses (developer mode crashes)
        _saveCallback = _capi.Event.RegisterCallback(_ => _settings.Save(_capi), 500, permittedWhilePaused: true);
    }

    // Only disable what Komet enabled and Begin() at once, or End() of the running frame crashes without a root entry.
    private void UpdateProfiler()
    {
        var measuring = Forced || Showing;
        var (patched, costPatched, epoch) = (ModTimes.Patched, RenderCost.Patched, Counting.Epoch);
        var threads = _window.Shows(HudWindow.System) || _window.Shows(HudWindow.Threads);
        Counting.Hud = Forced;
        if (Counting.Epoch != epoch)
            foreach (var panel in _panels.Bounded(PanelCount)) panel.Prime();
        ModTimes.Enabled = measuring;
        // opt-in even for the HUD's bench: its probes would skew a bench of the rest
        RenderCost.Enabled = _debug is not null || threads;
        WorkerPool.Timed = threads; // the worker meters, which only these tabs show
        PrimeRates(threads);
        // first switch-on patches the engine this frame: 3-29 ms of Harmony in tests
        if (ModTimes.Patched != patched || RenderCost.Patched != costPatched) Quiet();
        FrameClock.Stats = measuring;
        var profiler = _capi.World.FrameProfiler;
        if (!NotNull(profiler) || !Finite(_benchLeft)) return;
        if (!measuring && _profilerOwned) _profilerOwned = profiler.Enabled = false;
        if (!measuring || profiler.Enabled) return;
        _profilerOwned = profiler.Enabled = true;
        profiler.Begin();
    }

    // Rates read again after a pause would span the whole pause: a fresh read restarts them from now
    private void PrimeRates(bool threads)
    {
        var primed = (Showing, threads);
        if ((primed.Showing && !_primed.Showing) || (threads && !_primed.Threads)) Threads(new HudLive());
        _primed = primed;
    }

    private void WindowChanged()
    {
        if (_disposed) return;
        Quiet();
        UpdateProfiler();
        if (_window.Shows(HudWindow.Overview)) RequestAudit();
        if (_window.Shows(HudWindow.Log)) _ = Task.Run(_log.Sample);
    }

    private void Dump()
    {
        Capture("now", 0);
    }

    private void Copy(string report, string message)
    {
        if (!Assert(report.Length > 0)) return;
        _capi.Input.ClipboardText = report;
        _capi.Logger.Notification("{0}", report);
        _capi.ShowChatMessage(KometText.Translate(message));
    }

    private void StartBench(float seconds)
    {
        if (!Assert(seconds is > 0 and <= 3600)) return;
        if (_debug is not null)
        {
            _capi.ShowChatMessage(KometText.Translate("cmd-debug-running"));
            return;
        }

        var running = _benchLeft > 0; // not restarted, only its time left is told
        _capi.ShowChatMessage(KometText.Translate("hud-bench-start", running ? MathF.Ceiling(_benchLeft) : seconds));
        if (running) return;
        ResetMeasures();
        _benchLeft = seconds;
        UpdateProfiler();
    }

    // A bench or capture measures from its own start: worst frames, spikes and the panels' means begin anew
    private void ResetMeasures()
    {
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetBench();
        _frames.ResetHistory();
        _passes.ResetWorst();
        _spikes.Reset();
    }

    private void Bench(float elapsed)
    {
        if (!Assert(elapsed > 0) || !Assert(_benchLeft > 0)) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Accumulate();
        _benchLeft -= elapsed;
        if (_benchLeft > 0) return;
        _spikes.Rank(); // the report reads the ledger's rows, which otherwise wait for the next interval
        Copy(Means(), "hud-bench-done");
        UpdateProfiler();
    }

    private void Render(float deltaTime)
    {
        if (!Assert(deltaTime is >= 0 and < 10)) return; // NaN fails too
        var profiler = _capi.World.FrameProfiler;
        // Without this mark the Ortho renderers before the HUD were booked to the HUD
        profiler.Mark("ortho-before-komet-hud");
        var steady = Steady();
        if (FrameClock.Completed != _consumed) Record(FrameClock.Last, profiler, steady);
        else _frames.Record(deltaTime, float.NaN, steady);

        (_sinceInterval, _frameTotal, _elapsedTotal) =
            (_sinceInterval + deltaTime, _frameTotal + 1, _elapsedTotal + deltaTime);
        if (_sinceInterval >= _settings.Interval || (_endNow && _frames.Frames > 0)) EndInterval();
        profiler.Mark("komet-hud-interval");
        Toast();
        profiler.Mark("komet-hud-panel");
        if (_settings.Visible) DrawOverlay();
        DrawShaders(); // the HUD shown or not
        profiler.Mark("komet-hud");
    }

    private void Record(in FrameRecord frame, FrameProfilerUtil profiler, bool steady)
    {
        var consumed = _consumed;
        _consumed = FrameClock.Completed; // taken even when broken, or the HUD would stop at it
        if (!Assert(frame.Index >= consumed) || !Assert(frame.DtMs >= 0) || !Finite(frame.GcMs)) return;
        _frames.Record((float)(frame.DtMs / 1000), (float)frame.GcMs, steady);
        if (profiler.Enabled && frame.Root != null) _passes.AddFrame(frame, steady);
        else _passes.AddFrame(null, steady); // clears the previous frame's marks
        _ = _spikes.Add(frame, _passes.FrameTop, steady, _capi.InWorldEllapsedMilliseconds / 1000.0);
        if (double.IsFinite(frame.RunQueueMs)) (_runQueueSum, _runQueues) = (_runQueueSum + frame.RunQueueMs, _runQueues + 1);
        if (_debug is { } debug) DebugFrame(debug, frame, steady);
    }

    // The world clock stops while loading or paused, the resuming frame carries the gap; the initial chunk burst is
    // load cost
    private bool Steady()
    {
        var inWorld = _capi.InWorldEllapsedMilliseconds;
        _settled |= inWorld > SettleMs ||
                    (RuntimeStats.chunksAwaitingTesselation == 0 && RuntimeStats.chunksAwaitingPooling == 0);
        if (!Assert(inWorld >= 0) || !_settled || _capi.IsGamePaused || inWorld < WarmupMs)
        {
            _resumed = true;
            return false;
        }

        if (Environment.TickCount64 < _quietUntil) return false;
        if (_skip > 0)
        {
            _skip--;
            return false;
        }

        if (!_resumed) return true;
        _resumed = false;
        return false;
    }

    private void EndInterval()
    {
        if (!Assert(_sinceInterval > 0) || !Assert(_frames.Frames > 0))
        {
            (_sinceInterval, _endNow) = (0, false);
            return;
        }

        _frames.SampleWindow();
        _passes.UpdateOrder();
        _spikes.Rank();
        if (ModTimes.Enabled) _timings.Update(_frames.Frames, _sinceInterval);
        _gpu.Sample();
        RenderCost.Sample();
        if (Forced || _window.Shows(HudWindow.Render)) OcclusionCulling.Sample(); // bench and protocol rows read it too
        _capi.World.FrameProfiler.Mark("komet-hud-readback"); // the GPU's numbers, each read a wait for Mesa's glthread
        if (_interval % HudSettings.SlowEvery == 0)
        {
            _gpu.SampleVram(_window.IsOpened() || Forced);
            if (_sampling?.Exception?.GetBaseException() is { } failed)
                _capi.Logger.Warning("Komet HUD: sampling failed ({0})", failed.Message);
            if (_sampling?.IsCompleted != false) _sampling = Task.Run(_sample ??= SampleSlow);
        }

        if (Showing) TakeLive();
        _window.Invalidate();
        _overlayDirty = true;
        if (_benchLeft > 0) Bench(_sinceInterval);
        if (_debug is not null) DebugInterval(_sinceInterval);
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetPeaks();
        _frames.Reset();
        _passes.Reset();
        (_sinceInterval, _interval, _endNow) = (0, _interval + 1, false);
        (_runQueueSum, _runQueues) = (0, 0);
    }

    private void LevelReady()
    {
        _capi.Event.LevelFinalize -= LevelReady;
        ModTimes.Ready(); // every mod has started and patched
    }

    private void SampleSlow()
    {
        if (_disposed) return;
        _resources.Sample();
        if (_window.Shows(HudWindow.Log)) _log.Sample();
    }
}

// Snapshot taken each interval before collectors reset: views draw from it anytime, never a half interval.
internal sealed class HudLive
{
    public const int MaxPasses = 9, MaxThreads = 8;
    public double Fps = double.NaN, Low1 = double.NaN, Low01 = double.NaN, AvgMs = double.NaN,
        P99 = double.NaN, GpuMs = double.NaN, ModsMs, RunQueueMs = double.NaN, CulledPercent = double.NaN;

    public double Under8, Under12, Under25, Over25; // shares of the frame history, 0..100
    public int DrawCalls, Triangles;
    public double TotalMs;
    public readonly (string Key, double Ms)[] Passes = new (string, double)[MaxPasses];
    public int PassCount;
    public readonly (string Name, double Busy, double MsPerFrame)[] Threads = new (string, double, double)[MaxThreads];
    public readonly double[] KindBusy = [.. Enumerable.Repeat(double.NaN, RenderCost.Kinds)]; // by RenderCost kind, NaN unseen
    public int ThreadCount;
    public (double Draws, double Left, double Segments, long MemoryMb, string[] Missing) Vulkan = (0, 0, 0, 0, []);
    public double WorkerFrame = double.NaN, WorkerTess = double.NaN; // percent of one thread, per worker
    public double Locks = double.NaN;
}

internal sealed partial class HudOverlay
{
    private readonly HudLive _live = new();
    private readonly float[] _sortedHistory = new float[HudCanvas.GraphFrames * 4];
    private double _runQueueSum; // the interval's frames with a run-queue time
    private int _runQueues;
    private System.Func<double>? _locks, _workerFrame, _workerTess;
    private readonly System.Func<double>?[] _threadBusy = new System.Func<double>?[RenderCost.Kinds], _threadMs = new System.Func<double>?[RenderCost.Kinds];

    private void TakeLive()
    {
        var l = _live;
        (l.Fps, l.Low1, l.Low01, l.AvgMs) = (_frames.Fps, _frames.Low1Fps, _frames.Low01Fps, _frames.AverageMs);
        (l.GpuMs, l.DrawCalls, l.Triangles) = (RenderCost.FrameGpuMs(_gpu.Ms), _frames.DrawCallsPerFrame, _frames.RenderedTriangles);
        (l.ModsMs, l.TotalMs, l.RunQueueMs) = (_timings.TotalExcept("game"), _passes.AverageTotalMs,
            _runQueues > 0 ? _runQueueSum / _runQueues : double.NaN);
        l.CulledPercent = OcclusionCulling.Enabled ? OcclusionCulling.CulledPercent : double.NaN;
        History(l);
        l.PassCount = 0;
        for (var i = 0; i < Math.Min(RenderPassStats.Count, HudLive.MaxPasses); i++)
            if (_passes.Key(i) is { Length: > 0 } key && _passes.AverageMs(i) > 0.005)
                l.Passes[l.PassCount++] = (key, _passes.AverageMs(i));
        Threads(l);
        l.Vulkan = Komet.Vulkan.VulkanRenderer.Snapshot();
    }

    // scale × growth of part per growth of whole since this row's last read
    private static Func<double> Ratio(Func<double> part, Func<double> whole, double scale = 1)
    {
        Growth grown = new(part), of = new(whole);
        return () => scale * grown.Next() / of.Next();
    }

    private static Func<double> Share(Func<double> part, Func<double> rest) =>
        Ratio(part, () => part() + rest(), 100);

    private Func<double> PerFrame(Func<double> total, double scale = 1) =>
        Ratio(total, () => _frameTotal, scale);

    private Func<double> PerSecond(Func<double> total, double scale = 1) =>
        Ratio(total, () => _elapsedTotal, scale);

    private void History(HudLive l)
    {
        var n = Math.Min(_frames.Recorded, Math.Min(_frames.HistoryLength, _sortedHistory.Length));
        if (n == 0)
        {
            (l.P99, l.Under8, l.Under12, l.Under25, l.Over25) = (double.NaN, 0, 0, 0, 0);
            return;
        }

        var first = _frames.HistoryLength - n;
        for (var i = 0; i < Math.Min(n, HudCanvas.GraphFrames * 4); i++) _sortedHistory[i] = _frames.HistoryMs(first + i);
        var sorted = _sortedHistory.AsSpan(0, n);
        sorted.Sort();
        l.P99 = FrameStats.Percentile(sorted, 990);
        int a = 0, b = 0, c = 0;
        foreach (var ms in sorted.Bounded(_sortedHistory.Length))
            if (ms < 8) a++;
            else if (ms < 12) b++;
            else if (ms < 25) c++;
        (l.Under8, l.Under12, l.Under25, l.Over25) = (100.0 * a / n, 100.0 * b / n, 100.0 * c / n, 100.0 * (n - a - b - c) / n);
    }

    private void Threads(HudLive l)
    {
        l.ThreadCount = 0;
        for (var k = 0; k < RenderCost.Kinds; k++)
        {
            // the closure built only on first use: a captured loop local allocated one per kind each interval
            _threadBusy[k] ??= PerSecond(KindNs(k), 1e-7);
            _threadMs[k] ??= PerFrame(KindNs(k), 1e-6);
            var (busy, ms) = (_threadBusy[k]!(), _threadMs[k]!());
            l.KindBusy[k] = RenderCost.Seen(k) ? busy : double.NaN;
            if (RenderCost.Seen(k) && l.ThreadCount < HudLive.MaxThreads)
                l.Threads[l.ThreadCount++] = (KometText.Cached("hud-rc-", RenderCost.KindKey(k)), busy, ms);
        }

        _workerFrame ??= PerSecond(static () => WorkerPool.FrameTicks * FrameClock.TickMs, 0.1);
        _workerTess ??= PerSecond(static () => WorkerPool.BackgroundTicks * FrameClock.TickMs, 0.1);
        _locks ??= PerSecond(() => Monitor.LockContentionCount);
        var running = Math.Max(1, WorkerPool.Running);
        (l.WorkerFrame, l.WorkerTess, l.Locks) = (_workerFrame() / running, _workerTess() / running, _locks());
    }

    private static Func<double> KindNs(int kind) => () => RenderCost.ThreadNs(kind);
}

// Waits made visible or shorter: the HUD's code and fonts warmed on a pool thread at start, and a progress card while
// Vulkan builds shaders and pipelines, HUD or not, so waiting frames are no mystery
internal sealed partial class HudOverlay
{
    private const int MaxPrepared = 1024, MaxNested = 64, MaxWarmed = 32;
    private const double ShadersWidth = 300, ShadersFadeMs = 600, ShadersLinger = 1500;

    private const string Glyphs =
        "0123456789 abcdefghijklmnopqrstuvwxyzäöüß ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÜ .,:;%()[]/+-–×·Ø~";

    private static readonly Type[] Warmed =
    [
        typeof(HudOverlay), typeof(HudUi), typeof(HudUiFonts), typeof(HudCanvas), typeof(HudMotion), typeof(HudLive),
        typeof(FrameStats), typeof(RenderPassStats), typeof(SpikeLedger), typeof(GpuStats), typeof(ModTimes),
        typeof(HudSettings), typeof(FrameClock), typeof(OptionsScreen), typeof(HudPanel), typeof(HudLine),
        typeof(KometText), typeof(HudWindow), typeof(DebugWindow), typeof(Growth), typeof(Features)
    ];

    private HudCanvas? _shadersCanvas;
    private (long Done, long Started) _shadersShown = (-1, -1);
    private long _shadersSince = -1, _shadersIdleAt, _shadersBase, _shadersComposed;

    private static void Warm(ILogger logger, HudUiFonts fonts)
    {
        if (!NotNull(logger)) return;
        _ = Task.Run(() => Prepare(fonts)).ContinueWith(failed => logger.Warning("Komet HUD: warm-up failed ({0})",
                failed.Exception?.GetBaseException().Message), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // First-call JIT and font loading cost the frame after F7 60-80 ms; warmed, the cold first frame drops ~46 -> ~12
    // ms.
    private static void Prepare(HudUiFonts fonts)
    {
        WarmFonts(fonts);
        var prepared = 0;
        foreach (var type in Warmed.Bounded(MaxWarmed))
        {
            prepared += PrepareType(type);
            foreach (var nested in AccessTools.InnerTypes(type).Bounded(MaxNested)) prepared += PrepareType(nested);
        }
    }

    // cairo's font caches are process-wide and locked: a private surface on a pool thread loads the faces. SetupContext
    // (writes options) stays on the main thread.
    private static void WarmFonts(HudUiFonts fonts)
    {
        using var surface = new ImageSurface(Format.Argb32, 64, 64);
        using var context = new Context(surface);
        using var options = new FontOptions();
        options.Antialias = Antialias.Subpixel; // what SetupContext sets, part of the scaled font's cache key
        context.FontOptions = options;
        foreach (var font in (ReadOnlySpan<CairoFont>)[fonts.Small, fonts.Body, fonts.Strong, fonts.Stat, fonts.Big, fonts.Huge, fonts.Mono])
        {
            context.SelectFontFace(font.Fontname, font.Slant, font.FontWeight);
            context.SetFontSize(scaled(font.UnscaledFontsize));
            _ = context.TextExtents(Glyphs);
            context.MoveTo(0, 32);
            context.ShowText(Glyphs);
        }
    }

    private static int PrepareType(Type type)
    {
        if (type.ContainsGenericParameters) return 0;
        var prepared = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(type).Bounded(MaxPrepared))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepared++;
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                                          or MissingMemberException)
            {
                // the first call compiles it as before
            }
        }

        return prepared;
    }

    private void DrawShaders()
    {
        var (pending, started) = (Komet.Vulkan.Builds.Pending, Komet.Vulkan.Builds.Started);
        if (!Assert(pending >= 0) || !Assert(started >= 0)) return;
        var now = Environment.TickCount64;
        if (pending > 0 && _shadersSince < 0) (_shadersSince, _shadersBase) = (now, started - pending); // a new burst counts from 0
        if (_shadersSince < 0) return;
        if (pending > 0) _shadersIdleAt = now;
        var idle = now - _shadersIdleAt;
        if (pending == 0 && idle > ShadersLinger + ShadersFadeMs)
        {
            _shadersSince = -1;
            return;
        }

        var shown = (started - pending - _shadersBase, started - _shadersBase);
        // at most ten compositions a second (each paints and uploads on the main thread); the last always
        if (_shadersCanvas is null || (shown != _shadersShown && (now - _shadersComposed >= 100 || shown.Item1 == shown.Item2)))
        {
            _shadersComposed = now;
            ComposeShaders(shown.Item1, shown.Item2);
        }
        if (_shadersCanvas is not { Ready: true } card) return;
        var alpha = Math.Clamp(Math.Min((now - _shadersSince) / 200.0, 1 - (idle - ShadersLinger) / ShadersFadeMs), 0, 1);
        card.Fade(Math.Round((_capi.Render.FrameWidth - card.Width) / 2), scaled(12), 60, alpha, 1);
    }

    private void ComposeShaders(long done, long started)
    {
        if (!Assert(done >= 0) || !Assert(started >= done)) return;
        _shadersShown = (done, started);
        _uiFonts.Update(_settings.FontScale);
        _shadersCanvas ??= new HudCanvas(_capi);
        var (f, px) = (_uiFonts, scaled(_uiFonts.Scale));
        var (w, h) = (ShadersWidth * px, 14 * px + f.BodyRow + 6 * px);
        if (!_shadersCanvas.Blank(w, h)) return;
        HudUi.Ground(_shadersCanvas, w, h, HudUi.Back with { A = 0.92 }, 6 * px);
        var text = done >= started ? T("hud-shaders-done", started) : T("hud-shaders", done, started);
        _shadersCanvas.Text(12 * px, 5 * px, f.BodyRow, f.Body, text);
        var (barX, barY, barW) = (12 * px, h - 9 * px, w - 24 * px);
        _shadersCanvas.Fill(barX, barY, barW, 4 * px, Rgba.White(0.12), 2 * px);
        _shadersCanvas.Fill(barX, barY, Math.Max(4 * px, barW * done / Math.Max(1, started)), 4 * px, HudUi.Accent, 2 * px);
        _shadersCanvas.End();
    }
}
