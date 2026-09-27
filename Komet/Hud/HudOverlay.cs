using System.Text;
using Vintagestory.Client;

namespace Komet.Hud;

internal sealed partial class HudOverlay : IRenderer
{
    private const int Columns = PanelLayout.Columns, WarmupMs = 5000, SettleMs = 30000;

    // Frames not booked as steady after the HUD appears and around a Komet dialog: first-call JIT, the first Cairo pass of every panel
    // and a dialog's composition cost tens of ms and owned the 0.1 % low for ~20 s. Long enough for a round of panels at low fps.
    private const int QuietMs = 1000;
    private readonly PanelBox[] _boxes = new PanelBox[PanelCount];
    private readonly (string Version, bool Preview, string Commit, string SourcePath) _build;
    private readonly ICoreClientAPI _capi;
    private readonly HudFonts _fonts = new();
    private readonly FrameStats _frames = new();
    private readonly (string Mode, string Budget) _gc = GcConfig();
    private readonly GpuStats _gpu = new();
    private readonly PanelLayout _layout = new(PanelCount);
    private readonly ModStats _mods = new();
    private readonly List<HudPanel> _panels = [];
    private readonly RenderPassStats _passes = new();
    private readonly PanelQueue _queue = new(PanelCount);
    private readonly ResourceStats _resources = new();
    private readonly HudSettings _settings;
    private readonly SnapGrid _snap;
    private readonly SpikeLedger _spikes = new();
    private readonly ModTimes _timings = new();
    private readonly HudVerifyDialog _verify;
    private GuiDialogConfirm? _ask;
    private long _consumed; // FrameClock records taken
    private bool _disposed, _modsShown, _wasVisible, _profilerOwned, _resumed, _settled;
    private int _dragging = -1, _lastClick = -1; // panel indices
    private double _elapsedTotal;
    private long _frameTotal; // frames and seconds since the HUD began, what the rows divide counter growth by
    private string? _frozen; // what the panels showed when the settings window opened
    private double _grabX, _grabY;
    private int _interval;
    private bool _intervalFrame; // the previous frame ran EndInterval
    private long _lastClickTime, _saveCallback;
    private bool _levelReady; // LevelFinalize has run: every mod has started and patched
    private long _quietUntil;
    private Task? _sampling;
    private float _scrollRemainder;
    private float _sinceInterval, _benchLeft;
    private UpdateCheck? _update;

    // The options window, which GraphicsMenu opens in place of the game's graphics tab
    internal OptionsScreen Options { get; }

    public HudOverlay(ICoreClientAPI capi)
    {
        _capi = capi;
        _settings = HudSettings.Load(capi);
        _build = ReadBuild(capi);
        _fonts.Update(_settings.FontScale);
        _snap = new SnapGrid(capi, Quiet);
        BuildPanels();
        Options = new OptionsScreen(capi, _settings, Dump, StartBench, OpenVerify);
        _verify = new HudVerifyDialog(capi, _settings, _fonts, _snap, () => _update, RunUpdateCheck);
        if (!Assert(_panels.Count == PanelCount) || !NotNull(capi.Event)) return;
        Options.OnOpened += SettingsToggled;
        Options.OnClosed += SettingsToggled;
        _verify.OnOpened += Quiet;
        _verify.OnClosed += Quiet;
        Options.Composing += Quiet;
        _verify.Composing += Quiet;
        _settings.RegisterHotkeys(capi);
        _settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        capi.Event.MouseDown += OnMouseDown;
        capi.Event.MouseMove += OnMouseMove;
        capi.Event.MouseUp += OnMouseUp;
        capi.Event.MouseWheelMove += OnMouseWheel;
        capi.Event.LevelFinalize += AskForUpdateCheck;
        capi.Event.LevelFinalize += LevelReady;
        Warm(capi.Logger, _fonts);
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!_settings.Visible && _benchLeft <= 0) return;
        // registered for exactly these
        if (!Assert(stage is EnumRenderStage.Before or EnumRenderStage.Ortho or EnumRenderStage.Done)) return;
        if (stage == EnumRenderStage.Before) _gpu.Begin();
        else if (stage == EnumRenderStage.Done) _gpu.End();
        else Render(deltaTime);
    }

    // Registered for three stages, so the game may call this three times before the mod system does
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _capi.Event.MouseDown -= OnMouseDown;
        _capi.Event.MouseMove -= OnMouseMove;
        _capi.Event.MouseUp -= OnMouseUp;
        _capi.Event.MouseWheelMove -= OnMouseWheel;
        _capi.Event.UnregisterCallback(_saveCallback);
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        _capi.Event.LevelFinalize -= LevelReady;
        Options.OnOpened -= SettingsToggled;
        Options.OnClosed -= SettingsToggled;
        _verify.OnOpened -= Quiet;
        _verify.OnClosed -= Quiet;
        Options.Composing -= Quiet;
        _verify.Composing -= Quiet;
        _update?.Dispose();
        _ask?.Dispose();
        _settings.Save(_capi);
        Options.Dispose();
        _verify.Dispose();
        _snap.Dispose();
        _gpu.Dispose();
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Dispose();
        if (_profilerOwned) _capi.World.FrameProfiler.Enabled = false;
        ModTimes.Enabled = FrameClock.Stats = Counting.Hud = false;
    }

    double IRenderer.RenderOrder => 0.9;
    int IRenderer.RenderRange => int.MaxValue;

    // A click on "check" or "check again" is consent for that one check, whatever the update-notice setting says.
    private void RunUpdateCheck()
    {
        if (!Assert(_build.Version.Length > 0) || !Assert(!_disposed)) return;
        if (_update is null)
            _update = new UpdateCheck(_capi.Logger, _build.Version, _build.Preview, _build.Commit, _build.SourcePath);
        else _update.Start();
    }

    private void OpenVerify()
    {
        if (!Assert(!_disposed) || !Assert(_panels.Count > 0)) return;
        if (_update is null) RunUpdateCheck();
        if (!_verify.IsOpened() && !_verify.TryOpen())
            _capi.Logger.Warning("Komet: checksum window could not be opened");
    }

    // Once, after the world is up: the check calls api.github.com, so nobody's client talks to GitHub without a yes.
    private void AskForUpdateCheck()
    {
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        if (_settings.UpdateAsked || !Assert(_build.Version.Length > 0)) return;
        Quiet(); // GuiDialogConfirm composes itself in its constructor
        _ask = new GuiDialogConfirm(_capi, HudText.Translate("update-ask"),
            yes => (_settings.UpdateAsked, _settings.UpdateCheck) = (true, yes));
        _ask.OnClosed += Quiet;
        if (!_ask.TryOpen())
            _capi.Logger.Warning("Komet: update opt-in dialog could not be opened, update notices stay off");
    }

    private void Quiet()
    {
        _quietUntil = Math.Max(_quietUntil, Environment.TickCount64 + QuietMs);
        _ = Assert(_quietUntil > 0);
    }

    // The dump button in this window reports what the HUD showed before it opened: opening it, and dumping, are not what was measured.
    // GuiDialog raises OnOpened after it counts as open and OnClosed after it no longer does. Null while the HUD is hidden or has
    // drawn no panel yet.
    private void SettingsToggled()
    {
        if (!Assert(!_disposed)) return; // Dispose() unsubscribes before it disposes the dialog
        Quiet();
        _frozen = Options.IsOpened() && _settings.Visible && Report(false) is { Length: > 0 } shown ? shown : null;
        _ = Assert(_frozen is null || Options.IsOpened());
    }

    // What the panels showed: every row as its panel measured it at the end of its last interval. Read live instead, a window opened by
    // '.komet' (a key event, before the frame renders) on the frame after an interval's end saw a window of 0 frames, about 1 open in
    // 12 at 50 fps: FPS 0, every pass 0 ms, mod shares at Infinity %. means: the bench's means per row, the final rows as they stand.
    // Then the ledger's latest frames: kept across intervals, and the opening frame is quiet.
    private string Report(bool means)
    {
        if (!Assert(_panels.Count == PanelCount)) return "";
        var text = new StringBuilder();
        foreach (var panel in _panels.Bounded(PanelCount)) panel.AppendShown(text, means);
        if (text.Length > 0) AppendSpikes(text);
        return text.ToString();
    }

    private void OnSettingsChanged()
    {
        if (!Assert(_settings.Interval > 0)) return;
        if (_settings.Visible && !_wasVisible) Quiet();
        _wasVisible = _settings.Visible;
        if (_settings is not { Visible: true, ShowMods: true }) _modsShown = false; // the next showing walks again

        if (_settings.UpdateCheck && _update is null) RunUpdateCheck();
        (_sinceInterval, _interval) = ((float)_settings.Interval, 0);
        UpdateProfiler();
        _capi.Event.UnregisterCallback(_saveCallback); // coalesces slider drags and hotkey bursts into one write
        _saveCallback = _capi.Event.RegisterCallback(_ => _settings.Save(_capi), 500);
    }

    // What the shown panels or the bench read is measured. The profiler: only disable what we enabled, Begin() right away, otherwise
    // End() of the running frame crashes without a root entry.
    private void UpdateProfiler()
    {
        var measuring = _benchLeft > 0 || _settings.Visible;
        var (patched, epoch) = (ModTimes.Patched, Counting.Epoch);
        Counting.Hud = _benchLeft > 0 || _settings is { Visible: true, ShowCounters: true };
        // counting starts: the first refresh covers the span since, not a NaN across the pause
        if (Counting.Epoch != epoch)
            foreach (var panel in _panels.Bounded(PanelCount)) panel.Prime();
        ModTimes.Enabled = _benchLeft > 0 || _settings is { Visible: true, ShowModTimes: true };
        // the first switch-on patches the engine on this frame: 3-29 ms of Harmony in tests
        if (ModTimes.Patched != patched) Quiet();
        FrameClock.Stats = measuring;
        var profiler = _capi.World.FrameProfiler;
        if (!NotNull(profiler) || !Finite(_benchLeft)) return;
        var wanted = _benchLeft > 0 || _settings is { Visible: true, ShowPasses: true };
        if (!wanted && _profilerOwned) _profilerOwned = profiler.Enabled = false;
        if (!wanted || profiler.Enabled) return;
        _profilerOwned = profiler.Enabled = true;
        profiler.Begin();
    }

    // What the panels show, frozen when this window opened, or now when the HUD was hidden then; hidden, nothing is measured to quote
    private void Dump()
    {
        var shown = _frozen ?? (_settings.Visible ? Report(false) : "");
        if (shown.Length > 0) Copy(shown, "hud-dumped");
        else _capi.ShowChatMessage(HudText.Translate("hud-dump-hidden"));
    }

    private void Copy(string report, string message)
    {
        if (!Assert(message.Length > 0) || !Assert(report.Length > 0)) return;
        _capi.Input.ClipboardText = report;
        _capi.Logger.Notification("{0}", report);
        _capi.ShowChatMessage(HudText.Translate(message));
    }

    private void StartBench(float seconds)
    {
        if (!Assert(seconds is > 0 and <= 3600) || !Assert(_panels.Count > 0)) return;
        var running = _benchLeft > 0; // not restarted, only its time left is told
        _capi.ShowChatMessage(HudText.Translate("hud-bench-start", running ? MathF.Ceiling(_benchLeft) : seconds));
        if (running) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetBench();
        _frames.ResetHistory();
        _passes.ResetWorst();
        _spikes.Reset();
        _benchLeft = seconds;
        UpdateProfiler();
    }

    private void Bench(float elapsed)
    {
        if (!Assert(elapsed > 0) || !Assert(_benchLeft > 0)) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Accumulate();
        _benchLeft -= elapsed;
        if (_benchLeft > 0) return;
        _spikes.Rank(); // the report reads the ledger's rows, which otherwise wait for the next interval
        Copy(Report(true), "hud-bench-done");
        UpdateProfiler();
    }

    // Only while the cursor is free (chat, inventory or menu open)
    private void OnMouseDown(MouseEvent e)
    {
        if (!_settings.Visible || _capi.Input.MouseGrabbed || e.Button != EnumMouseButton.Left ||
            Options.Contains(e.X, e.Y) || _verify.Contains(e.X, e.Y)) return;
        // its mouse up went elsewhere: GuiManager and the event API stop at the first handler
        if (_dragging >= 0) EndDrag();
        var index = _layout.Topmost(e.X, e.Y);
        if (index < 0 || !Index(index, _panels.Count)) return;
        var (panel, now) = (_panels[index], Environment.TickCount64);
        e.Handled = true;
        // double click grows / shrinks a log panel
        if (panel.Log is { } log && index == _lastClick && now - _lastClickTime < HudSettings.DoubleClickMs)
        {
            log.Expanded = !log.Expanded;
            Refresh(panel);
            _lastClick = -1;
            return;
        }

        (_dragging, _lastClick, _lastClickTime) = (index, index, now);
        var at = _layout.Drawn(index);
        (_grabX, _grabY) = (e.X - at.X, e.Y - at.Y);
    }

    private void OnMouseMove(MouseEvent e)
    {
        if (_dragging < 0) return;
        if (!Index(_dragging, _panels.Count))
        {
            _dragging = -1;
            return;
        }

        var (panel, frame) = (_panels[_dragging], Frame());
        double step = scaled(HudSettings.SnapStep), distance = scaled(HudSettings.SnapDistance);
        panel.Pin(_layout.Snap(e.X - _grabX, panel.Width, true, frame, step, distance),
            _layout.Snap(e.Y - _grabY, panel.Height, false, frame, step, distance));
        e.Handled = true;
    }

    private void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!_settings.Visible || _capi.Input.MouseGrabbed) return;
        var index = _layout.Topmost(_capi.Input.MouseX, _capi.Input.MouseY);
        if (index < 0 || !Index(index, _panels.Count) || _panels[index] is not { Log: { } log } hit) return;
        _scrollRemainder += e.deltaPrecise * HudSettings.LogScrollLines;
        if (!Assert(Math.Abs(_scrollRemainder) < 1000))
        {
            _scrollRemainder = 0;
            return;
        }

        var lines = (int)_scrollRemainder;
        _scrollRemainder -= lines;
        if (lines != 0)
        {
            log.Scroll(lines);
            Refresh(hit);
        }

        e.SetHandled();
    }

    // The up that ends a drag is ours: our mouse down swallowed the matching press, so nothing else is waiting for it
    private void OnMouseUp(MouseEvent e)
    {
        if (_dragging < 0) return;
        EndDrag();
        e.Handled = true;
    }

    // The pin moved: saved, and the settings window's corner row shows "custom"
    private void EndDrag()
    {
        _ = Assert(_dragging >= 0);
        _dragging = -1;
        _settings.NotifyChanged();
    }

    private void Render(float deltaTime)
    {
        if (!Assert(deltaTime is >= 0 and < 10)) return; // NaN fails too
        var profiler = _capi.World.FrameProfiler;
        var steady = Steady();
        // otherwise the clock's first frame, or its patch is missing: the engine's dt alone
        if (FrameClock.Completed != _consumed) Record(FrameClock.Last, profiler, steady);
        else _frames.Record(deltaTime, float.NaN, steady);

        (_sinceInterval, _frameTotal, _elapsedTotal) =
            (_sinceInterval + deltaTime, _frameTotal + 1, _elapsedTotal + deltaTime);
        var interval = _sinceInterval >= _settings.Interval;
        if (interval) EndInterval();
        if (_settings.Visible && (!interval || _intervalFrame)) DrawNext(); // not on top of the interval's own work
        _intervalFrame = interval;
        if (_settings.Visible) DrawPanels();
        profiler.Mark("komet-hud");
    }

    // The frame that ended last, measured by FrameClock: its dt with its own GC pause, its own profile, and the time between frames
    private void Record(in FrameRecord frame, FrameProfilerUtil profiler, bool steady)
    {
        var consumed = _consumed;
        _consumed = FrameClock.Completed; // taken even when broken, or the HUD would stop at it
        if (!Assert(frame.Index >= consumed) || !Assert(frame.DtMs >= 0) || !Finite(frame.GcMs)) return;
        _frames.Record((float)(frame.DtMs / 1000), (float)frame.GcMs, steady);
        if (profiler.Enabled && frame.Root != null) _passes.AddFrame(frame, steady);
        else _passes.AddFrame(null, steady); // clears the previous frame's marks
        _ = _spikes.Add(frame, _passes.FrameTop, steady, _capi.InWorldEllapsedMilliseconds / 1000.0);
    }

    // The in-world clock stands still while the world loads and while the game is paused, the frame that resumes it carries the whole gap,
    // and the burst of chunks the world starts with is a load cost rather than a frame time
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

        // Komet's own warm-up; the first mod walk runs on a pool thread but competes for the cores
        if (Environment.TickCount64 < _quietUntil || _mods is { Walking: true, Walked: false }) return false;
        if (!_resumed) return true;
        _resumed = false;
        return false;
    }

    private void EndInterval()
    {
        if (!Assert(_sinceInterval > 0) || !Assert(_frames.Frames > 0))
        {
            _sinceInterval = 0;
            return;
        }

        _frames.SampleWindow();
        _passes.UpdateOrder();
        _spikes.Rank();
        if (ModTimes.Enabled) _timings.Update(_frames.Frames, _sinceInterval);
        _gpu.Sample();
        RequestMods();
        if (_interval % HudSettings.SlowEvery == 0)
        {
            _gpu.SampleVram(_settings.ShowSystem || _benchLeft > 0);
            if (_sampling?.Exception?.GetBaseException() is { } failed)
                _capi.Logger.Warning("Komet HUD: sampling failed ({0})", failed.Message);
            if (_sampling?.IsCompleted != false) _sampling = Task.Run(SampleSlow);
        }

        if (_settings.Visible) RefreshPanels();
        if (_benchLeft > 0) Bench(_sinceInterval);
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetPeaks();
        _frames.Reset();
        _passes.Reset();
        (_sinceInterval, _interval) = (0, _interval + 1);
    }

    // Once per showing of the mods panel, and only after LevelFinalize: before it neither Komet nor the mods loaded after it have patched
    private void RequestMods()
    {
        if (!_levelReady || _modsShown || _settings is not { Visible: true, ShowMods: true } ||
            !NotNull(_capi.ModLoader)) return;
        _modsShown = true; // Harmony's registry rarely changes after load
        _mods.Request(_capi, KometModSystem.ModId);
    }

    private void LevelReady()
    {
        _capi.Event.LevelFinalize -= LevelReady;
        _ = Assert(!_levelReady); // once per world, like the event
        _levelReady = true;
        ModTimes.Ready();
    }

    // /proc and the log files, off the main thread
    private void SampleSlow()
    {
        if (!Assert(!_disposed) || !Assert(_panels.Count == PanelCount)) return;
        _resources.Sample();
        if (!_settings.Visible) return;
        foreach (var panel in _panels.Bounded(PanelCount))
            if (panel is { Visible: true, Log: { } log })
                log.Sample();
    }

    // Every due panel is measured before any is drawn, so all of them see the column's final width; a panel that its column has
    // outgrown is redrawn even when not due, and one never drawn is due at once. The drawing itself is queued, one panel per frame.
    // Pinned panels keep their own width.
    private void RefreshPanels()
    {
        var interval = _interval;
        if (!Assert(interval >= 0) || !Assert(_panels.Count == PanelCount)) return;
        _fonts.Update(_settings.FontScale);
        Span<bool> due = stackalloc bool[PanelCount];
        for (var i = 0; i < Math.Min(_panels.Count, PanelCount); i++)
            due[i] = _panels[i].Measure(interval, _settings.Detail, _panels[i] is { Visible: true, Ready: false });
        for (var i = 0; i < Math.Min(_panels.Count, PanelCount); i++)
        {
            var panel = _panels[i];
            if (due[i] || (panel.Visible && panel.Width < Width(panel))) _queue.Add(i);
        }
    }

    // At most one Cairo pass and texture upload per frame
    private void DrawNext()
    {
        var next = _queue.Next();
        if (next < 0 || !Index(next, _panels.Count)) return;
        var panel = _panels[next];
        if (!panel.Visible) return;
        panel.Render(Width(panel));
        _ = Assert(panel.Ready); // measured at the interval, so Render() has something to draw
    }

    // The column's natural width, so the panels of a column line up; a pinned panel keeps its own
    private double Width(HudPanel panel)
    {
        if (!NotNull(panel) || !Index(panel.Column, Columns)) return 0;
        if (panel.Pinned != null) return panel.Width;
        double width = 0;
        foreach (var other in _panels.Bounded(PanelCount))
            if (other.Column == panel.Column && other is { Visible: true, Pinned: null })
                width = Math.Max(width, other.NaturalWidth);
        return width;
    }

    // One panel right away (log scroll, double click), not at the next tick of its own cadence
    private void Refresh(HudPanel panel)
    {
        if (!Index(panel.Column, Columns) || !Assert(panel.Visible) ||
            !panel.Measure(_interval, _settings.Detail, true)) return;
        _queue.Remove(_panels.IndexOf(panel));
        panel.Render(Width(panel));
    }

    // Laid out when something moved, then drawn layer by layer; a panel that was never drawn has no texture yet and takes no room
    private void DrawPanels()
    {
        var frame = Frame();
        if (!Assert(_panels.Count == PanelCount) || !Assert(frame.Width > 0) || !Assert(frame.Height > 0)) return;
        if (_dragging >= 0) _snap.Draw();
        for (var i = 0; i < PanelCount; i++)
            _boxes[i] = new PanelBox(_panels[i] is { Visible: true, Ready: true }, _panels[i].Column, _panels[i].Width,
                _panels[i].Height, _panels[i].Pinned);
        _ = _layout.Update(_boxes, frame);
        for (var layer = 0; layer < PanelLayout.Layers; layer++)
            for (var i = 0; i < PanelCount; i++)
                if (_layout.Layer(i) == layer && _layout.Drawn(i) is var at)
                    _panels[i].Draw(at.X, at.Y);
    }

    // The slack is the padding: an overlap within it hides no text
    private LayoutFrame Frame() => new(_capi.Render.FrameWidth, _capi.Render.FrameHeight, _settings.Corner,
        scaled(HudSettings.PanelGap), scaled(HudSettings.ScreenMargin), scaled(HudPanel.Padding), _dragging);
}
