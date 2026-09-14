using System.Text;
using Komet.Features;
using Komet.Runtime.Diagnostics;

namespace Komet.Runtime.UI.Overlay;

internal sealed partial class HudOverlay : IRenderer
{
    private readonly ICoreClientAPI _capi;
    private readonly HudSettings _settings;
    private readonly HudSettingsDialog _dialog;
    private readonly HudVerifyDialog _verify;
    private readonly FrameStats _frames = new(HudSettings.HistoryFrames);
    private readonly GpuStats _gpu = new();
    private readonly RenderPassStats _passes = new();
    private readonly ResourceStats _resources = new();
    private readonly ModTimings _timings = new();
    private readonly ModStats _mods = new();
    private readonly List<HudPanel> _panels = [];
    private const int MaxColumns = 4;
    private readonly int _columns;
    private int _interval;
    private float _sinceInterval, _benchLeft;
    private bool _profilerOwned;
    private int _dragging = -1, _lastClick = -1;   // panel indices
    private long _lastClickTime, _saveCallback;
    private double _grabX, _grabY;
    private float _scrollRemainder;
    private HudCanvas? _grid;
    private Task? _sampling;
    private UpdateCheck? _update;
    private GuiDialogConfirm? _ask;
    private (string Version, bool Preview, string Commit, string SourcePath) _build;
    private bool _disposed;

    public HudOverlay(ICoreClientAPI capi)
    {
        _capi = capi;
        _settings = HudSettings.Load(capi);
        BuildPanels();
        foreach (var panel in _panels.Bounded(PanelCount)) _columns = Math.Max(_columns, panel.Column + 1);
        _dialog = new HudSettingsDialog(capi, _settings, () => Report(mean: false, "hud-dumped"), StartBench, OpenVerify);
        _verify = new HudVerifyDialog(capi, _settings, () => _update, RunUpdateCheck);
        if (!Assert(_panels.Count > 0) || !Index(_columns - 1, MaxColumns)) return;
        _settings.RegisterHotkeys(capi);
        _settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        capi.Event.MouseDown += OnMouseDown;
        capi.Event.MouseMove += OnMouseMove;
        capi.Event.MouseUp += OnMouseUp;
        capi.Event.MouseWheelMove += OnMouseWheel;
        capi.Event.LevelFinalize += AskForUpdateCheck;
    }

    // A click on "check" or "check again" is consent for that one check, whatever the update-notice setting says.
    private void RunUpdateCheck()
    {
        if (!Assert(_build.Version.Length > 0) || !Assert(!_disposed)) return;
        if (_update is null) _update = new UpdateCheck(_capi.Logger, _build.Version, _build.Preview, _build.Commit, _build.SourcePath);
        else _update.Start();
    }

    private void OpenVerify()
    {
        if (!Assert(!_disposed) || !Assert(_panels.Count > 0)) return;
        if (_update is null) RunUpdateCheck();
        if (!_verify.IsOpened() && !_verify.TryOpen()) _capi.Logger.Warning("Komet: checksum window could not be opened");
    }

    // Once, after the world is up: the check calls api.github.com, so nobody's client talks to GitHub without a yes.
    private void AskForUpdateCheck()
    {
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        if (_settings.UpdateAsked || !Assert(_build.Version.Length > 0)) return;
        _ask = new GuiDialogConfirm(_capi, HudSettings.Translate("update-ask"), yes => { _settings.UpdateAsked = true; _settings.UpdateCheck = yes; });
        if (!_ask.TryOpen()) _capi.Logger.Warning("Komet: update opt-in dialog could not be opened, update notices stay off");
    }

    private void OnSettingsChanged()
    {
        if (!Assert(_settings.Interval > 0)) return;
        ShaderUseCache.Stats = _settings is { Visible: true, ShowMods: true };
        if (_settings.UpdateCheck && _update is null) RunUpdateCheck();
        (_sinceInterval, _interval) = ((float)_settings.Interval, 0);
        UpdateProfiler();
        _capi.Event.UnregisterCallback(_saveCallback);   // coalesces slider drags and hotkey bursts into one write
        _saveCallback = _capi.Event.RegisterCallback(_ => _settings.Save(_capi), 500);
    }

    // Only disable what we enabled. Begin() right away, otherwise End() of the running frame crashes without a root entry.
    private void UpdateProfiler()
    {
        ModProfiler.Enabled = _benchLeft > 0 || _settings is { Visible: true, ShowModTimes: true };
        var profiler = _capi.World.FrameProfiler;
        if (!NotNull(profiler)) return;
        var wanted = _benchLeft > 0 || _settings is { Visible: true, ShowPasses: true };
        if (wanted && !profiler.Enabled)
        {
            _profilerOwned = profiler.Enabled = true;
            profiler.Begin();
        }
        else if (!wanted && _profilerOwned) _profilerOwned = profiler.Enabled = false;
    }

    private void Report(bool mean, string message)
    {
        if (!Assert(message.Length > 0) || !Assert(_panels.Count > 0)) return;
        var text = new StringBuilder();
        foreach (var panel in _panels.Bounded(PanelCount)) panel.AppendText(text.Length == 0 ? text : text.Append("\n\n"), mean);
        var report = text.ToString();
        if (!Assert(report.Length > 0)) return;
        _capi.Input.ClipboardText = report;
        _capi.Logger.Notification("{0}", report);
        _capi.ShowChatMessage(HudSettings.Translate(message));
    }

    private void StartBench(float seconds)
    {
        if (!Assert(seconds is > 0 and <= 3600) || !Assert(_benchLeft <= 0)) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.ResetBench();
        _benchLeft = seconds;
        UpdateProfiler();
        _capi.ShowChatMessage(HudSettings.Translate("hud-bench-start", seconds));
    }

    private void Bench(float elapsed)
    {
        if (!Assert(elapsed > 0) || !Assert(_benchLeft > 0)) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Accumulate();
        _benchLeft -= elapsed;
        if (_benchLeft > 0) return;
        Report(mean: true, "hud-bench-done");
        UpdateProfiler();
    }

    // Only while the cursor is free (chat, inventory or menu open)
    private void OnMouseDown(MouseEvent e)
    {
        if (!_settings.Visible || _capi.Input.MouseGrabbed || e.Button != EnumMouseButton.Left || _dialog.Contains(e.X, e.Y) || _verify.Contains(e.X, e.Y)) return;
        if (!Assert(_dragging < 0) || !Assert(_grid is null)) { _dragging = -1; return; }   // a mouse up went missing
        var index = _panels.FindIndex(p => p.Visible && p.Contains(e.X, e.Y));
        if (index < 0) return;
        var panel = _panels[index];
        var now = Environment.TickCount64;
        if (panel.Log is { } log && index == _lastClick && now - _lastClickTime < HudSettings.DoubleClickMs)   // double click grows / shrinks a log panel
        {
            log.Expanded = !log.Expanded;
            Refresh(panel, 0);
            (_lastClick, e.Handled) = (-1, true);
            return;
        }
        (_dragging, _lastClick, _lastClickTime) = (index, index, now);
        (_grabX, _grabY) = (e.X - panel.X, e.Y - panel.Y);
        _grid = HudCanvas.Grid(_capi, scaled(HudSettings.SnapGrid));
        e.Handled = true;
    }

    private void OnMouseMove(MouseEvent e)
    {
        if (_dragging < 0) return;
        if (!Index(_dragging, _panels.Count)) { _dragging = -1; return; }
        var panel = _panels[_dragging];
        panel.Pin(Snap(e.X - _grabX, panel.Width, horizontal: true), Snap(e.Y - _grabY, panel.Height, horizontal: false));
        e.Handled = true;
    }

    // Grid first; edges of other panels (with gap) and the screen margin win within SnapDistance
    private double Snap(double pos, double size, bool horizontal)
    {
        double step = scaled(HudSettings.SnapGrid), gap = scaled(HudSettings.PanelGap), margin = scaled(HudSettings.ScreenMargin);
        if (!Finite(pos) || !Assert(size > 0) || !Assert(step > 0)) return pos;
        double best = Math.Round(pos / step) * step, bestDistance = scaled(HudSettings.SnapDistance);
        double screen = horizontal ? _capi.Render.FrameWidth : _capi.Render.FrameHeight;
        Consider(margin, screen - 2 * margin);
        for (var i = 0; i < Math.Min(_panels.Count, PanelCount); i++)
        {
            var panel = _panels[i];
            if (i != _dragging && panel.Visible) Consider(horizontal ? panel.X : panel.Y, horizontal ? panel.Width : panel.Height);
        }
        return best;

        void Consider(double start, double length)
        {
            if (!Finite(start) || !Assert(length >= 0)) return;
            foreach (var candidate in (ReadOnlySpan<double>)[start + length + gap, start - size - gap, start, start + length - size])
            {
                var distance = Math.Abs(candidate - pos);
                if (distance < bestDistance) (best, bestDistance) = (candidate, distance);
            }
        }
    }

    private void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (!_settings.Visible || _capi.Input.MouseGrabbed) return;
        var hit = _panels.Find(p => p is { Visible: true, Log: not null } && p.Contains(_capi.Input.MouseX, _capi.Input.MouseY));
        if (hit == null || !NotNull(hit.Log)) return;
        _scrollRemainder += e.deltaPrecise * HudSettings.LogScrollLines;
        if (!Assert(Math.Abs(_scrollRemainder) < 1000)) { _scrollRemainder = 0; return; }
        var lines = (int)_scrollRemainder;
        _scrollRemainder -= lines;
        if (lines != 0)
        {
            hit.Log.Scroll(lines);
            Refresh(hit, 0);
        }
        e.SetHandled();
    }

    private void OnMouseUp(MouseEvent e)
    {
        if (_dragging < 0) return;
        _dragging = -1;
        _settings.NotifyChanged();
        _grid?.Dispose();
        _grid = null;
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (!_settings.Visible && _benchLeft <= 0) return;
        if (!Assert(stage is EnumRenderStage.Before or EnumRenderStage.Ortho or EnumRenderStage.Done)) return;   // registered for exactly these
        if (stage == EnumRenderStage.Before) _gpu.Begin();
        else if (stage == EnumRenderStage.Done) _gpu.End();
        else Render(deltaTime);
    }

    private void Render(float deltaTime)
    {
        if (!Assert(deltaTime is >= 0 and < 10)) return;   // NaN fails too
        var profiler = _capi.World.FrameProfiler;
        _frames.Record(deltaTime);
        if (profiler.Enabled) _passes.AddFrame(profiler.PrevRootEntry);
        _sinceInterval += deltaTime;
        if (_sinceInterval >= _settings.Interval) EndInterval();
        if (_settings.Visible) DrawPanels();
        profiler.Mark("komet-hud");
    }

    private void EndInterval()
    {
        if (!Assert(_sinceInterval > 0) || !Assert(_frames.Frames > 0)) { _sinceInterval = 0; return; }
        _frames.SampleWindow();
        _passes.UpdateOrder();
        if (ModProfiler.Enabled) _timings.Update(_frames.Frames, _sinceInterval);
        _gpu.SampleVram();
        if (_interval % HudSettings.SlowEvery == 0 && _sampling?.IsCompleted != false)
            _sampling = Task.Run(() =>
            {
                _resources.Sample();
                if (!_settings.Visible) return;
                foreach (var panel in _panels.Bounded(PanelCount))
                    if (panel is { Visible: true, Log: { } log }) log.Sample();
            });
        if (_settings is { Visible: true, ShowMods: true } && _interval % HudSettings.ModsEvery == 0) _mods.Sample(_capi, "komet");
        if (_settings.Visible) foreach (var panel in _panels.Bounded(PanelCount)) Refresh(panel, _interval);
        if (_benchLeft > 0) Bench(_sinceInterval);
        _frames.Reset();
        _passes.Reset();
        ShaderUseCache.ResetStats();
        (_sinceInterval, _interval) = (0, _interval + 1);
    }

    // Interval 0 redraws right away, not at the next tick of the panel's own cadence
    private void Refresh(HudPanel panel, int interval)
    {
        if (!Assert(interval >= 0) || !Index(panel.Column, _columns)) return;
        panel.Refresh(interval, _settings.Detail, panel.Pinned == null ? ColumnWidth(panel.Column, natural: true) : panel.Width);
    }

    private double ColumnWidth(int column, bool natural)
    {
        if (!Index(column, _columns)) return 0;
        double width = 0;
        foreach (var panel in _panels.Bounded(PanelCount))
            if (panel.Column == column && panel is { Visible: true, Pinned: null })
                width = Math.Max(width, natural ? panel.NaturalWidth : panel.Width);
        return width;
    }

    private void DrawPanels()
    {
        if (!Assert(_columns > 0) || !Assert(_capi.Render.FrameWidth > 0) || !Assert(_capi.Render.FrameHeight > 0)) return;
        _grid?.Draw(0, 0);
        var right = _settings.Corner is HudCorner.TopRight or HudCorner.BottomRight;
        var bottom = _settings.Corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        double gap = scaled(HudSettings.PanelGap), margin = scaled(HudSettings.ScreenMargin), x = margin;
        foreach (var panel in _panels.Bounded(PanelCount))
            if (panel is { Visible: true, Pinned: var (px, py) }) panel.Draw(px, py);

        for (var column = 0; column < Math.Min(_columns, MaxColumns); column++)
        {
            var y = margin;
            foreach (var panel in _panels.Bounded(PanelCount))
            {
                if (panel.Column != column || panel.Pinned != null || !panel.Visible) continue;
                panel.Draw(right ? _capi.Render.FrameWidth - x - panel.Width : x, bottom ? _capi.Render.FrameHeight - y - panel.Height : y);
                y += panel.Height + gap;
            }
            x += ColumnWidth(column, natural: false) + gap;
        }
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
        _update?.Dispose();
        _ask?.Dispose();
        _settings.Save(_capi);
        _dialog.Dispose();
        _verify.Dispose();
        _grid?.Dispose();
        _gpu.Dispose();
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Dispose();
        if (_profilerOwned) _capi.World.FrameProfiler.Enabled = false;
        ModProfiler.Enabled = false;
    }

    public double RenderOrder => 0.9;
    public int RenderRange => int.MaxValue;
}
