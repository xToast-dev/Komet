using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Ui.Hud;

namespace Komet.Hud;

// The HUD's root and renderer: settings, the sampling hub, the capture service, the overlay and the dockable window
// with its tabs, wired together; per frame it feeds the sampler and draws the overlay, toast and shader card.
internal sealed partial class HudOverlay : IRenderer
{
    // The bench's laps ("hudWindow": "cycle"): the window's views by their old fixed index (8 = protocol); a lap past
    // them opens the protocol
    private static readonly string[] Laps =
        ["overview", "frames", "system", "render", "threads", "log", "settings", "version", "protocol", "inspector"];

    private readonly HudBuild _build;
    private readonly ICoreClientAPI _capi;
    private readonly DebugCapture _capture;
    private readonly HudUiFonts _fonts = new();
    private readonly OverlayCard _overlay;
    private readonly HudSampler _sampler;
    private readonly HudSettings _settings;
    private readonly ShaderCard _shaders;
    private readonly SnapGrid _snap;
    private readonly HudWindow _window;
    private HudAsk? _ask;
    private bool _disposed;
    private long _saveCallback;

    public HudOverlay(ICoreClientAPI capi)
    {
        _capi = capi;
        _settings = HudSettings.Load(capi);
        _build = new HudBuild(capi);
        _fonts.Update(_settings.FontScale);
        _sampler = new HudSampler(capi, _settings, () => Showing);
        _sampler.Panels = new HudPanels(capi, _sampler, _build).Build();
        _snap = new SnapGrid(capi, _sampler.Quiet);
        _capture = new DebugCapture(capi, _settings, _sampler, _build, () => Show("protocol"));
        _overlay = new OverlayCard(capi, _settings, _fonts, _sampler, _build, _capture, _snap);
        _shaders = new ShaderCard(capi, _settings, _fonts);
        _window = new HudWindow(capi, _settings, _fonts, _snap, Views(), () => _build.Badges(() => Select("version")))
        {
            OpenDebug = () => Show("protocol")
        };
        Options = new OptionsScreen(capi, _settings,
            new KometActions(Dump, _sampler.StartBench, OpenVerify, OpenWindow, () => Show("protocol")));
        (Options.VersionState, Options.OpenVersion) = (_build.VersionState, () => Select("version"));
        Options.Tools = DevTools;
        if (!NotNull(capi.Event)) return;

        Options.OnOpened += _sampler.Quiet;
        Options.OnClosed += _sampler.Quiet;
        Options.Composing += _sampler.Quiet;
        _settings.RegisterHotkeys(capi, () => _window.Toggle(), ToggleDeveloper);
        _window.Changed += WindowChanged;
        _window.Composing += _sampler.Skip;
        _sampler.Interval += Sampled;
        ShowForBench = ShowLap;
        CaptureForBench = seconds => _capture.Capture("record", seconds);
        OptionsForBench = (open, page) => _ = open ? Options.Open(page) : Options.TryClose();
        DeveloperForBench = () => _ = Options.Developer || Options.OpenDeveloper();
        RegisterCommands(capi);
        _capture.Register(capi);
        _overlay.Mouse(capi.Event, true);
        _settings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        capi.Event.LevelFinalize += AskForUpdateCheck;
        capi.Event.LevelFinalize += LevelReady;
        HudWarmup.Warm(capi.Logger, _fonts);
    }

    internal OptionsScreen Options { get; }

    internal static Action<int>? ShowForBench { get; private set; }

    // Bench-started capture ("gui": "debug"): its end runs under the freeze watchdog
    internal static Action<float>? CaptureForBench { get; private set; }

    internal static Action<bool, string?>? OptionsForBench { get; private set; }

    // The SDK's measurement (KOMET_BENCH_STUDIO): the developer screen opened for a whole run
    internal static Action? DeveloperForBench { get; private set; }

    private bool Showing => _settings.Visible || _settings.ShowDetails || _window.IsOpened();

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.Before) _capture.BeforeFrame();

        if (!Showing && !_sampler.Forced)
        {
            // the shader card shows with the HUD hidden too; a query begun while it showed still ends
            if (stage == EnumRenderStage.Ortho)
            {
                _shaders.Draw();
            }
            else if (stage == EnumRenderStage.Done)
            {
                _sampler.Gpu.End();
            }

            return;
        }

        if (stage == EnumRenderStage.Before)
        {
            _sampler.Gpu.Begin();
        }
        else if (stage == EnumRenderStage.Done)
        {
            _sampler.Gpu.End();
            RenderCost.EndFrame();
        }
        else
        {
            Render(deltaTime);
        }
    }

    // Registered for three stages: may be called three times
    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        var events = _capi.Event; // null when the constructor stood down early (headless, tests)
        Features.Cleanup("Hud", "detach save callback", () => events?.UnregisterCallback(_saveCallback));
        _window.Changed -= WindowChanged;
        _sampler.Interval -= Sampled;
        if (_capi.Event is { } mouse) _overlay.Mouse(mouse, false);
        _settings.Changed -= OnSettingsChanged; // a late change would switch the collectors on again
        (ShowForBench, CaptureForBench, OptionsForBench, DeveloperForBench) = (null, null, null, null);
        Features.Cleanup("Hud", "finish capture", _capture.Dispose);
        if (events is not null)
        {
            events.LevelFinalize -= AskForUpdateCheck;
            events.LevelFinalize -= LevelReady;
        }

        Options.OnOpened -= _sampler.Quiet;
        Options.OnClosed -= _sampler.Quiet;
        Options.Composing -= _sampler.Quiet;
        Features.Cleanup("Hud", "save settings", () => _settings.Save(_capi));
        Features.Cleanup("Hud", "release resources", () => Owned.DropAll(
        [
            _window, _overlay, _shaders, _ask, Options, _devTools is [IDisposable tool, ..] ? tool : null, _snap,
            _sampler, _build
        ]));
    }

    double IRenderer.RenderOrder => 0.9;
    int IRenderer.RenderRange => int.MaxValue;

    private IHudTab View(string id)
    {
        var view = _window.Find(id);
        _ = Assert(view is not null);
        return view ?? _window.Current;
    }

    // The window on that view; the options screen closes so the window is not hidden behind it
    private void Show(string id)
    {
        if (_disposed) return;

        _ = !Options.IsOpened() || Options.TryClose();
        if (!_window.Shows(View(id))) _window.Toggle(View(id));
    }

    private void Select(string id) => _window.Select(View(id));

    private void ShowLap(int lap)
    {
        if (!Assert(lap >= 0) || _disposed) return;

        if (lap < Laps.Length)
        {
            _window.Toggle(View(Laps[lap]));
        }
        else
        {
            Show("protocol");
        }
    }

    private HudViews Views()
    {
        IHudTab[] bar =
        [
            new OverviewTab(_capi, _settings, _sampler, _build, _capture, Show), new FramesTab(_capi, _sampler),
            new SystemTab(_capi, _sampler), new RenderTab(_sampler), new ThreadsTab(_settings, _sampler),
            new LogTab(_sampler.Log), new ProtocolTab(_capi, _settings, _capture), new InspectorTab(_capi)
        ];
        return new HudViews(bar, new SettingsPage(_capi, _settings, () => Options.Open(KometPages.Hud)),
            new VersionPage(_build));
    }

    // From the options screen, which closes so the window is not hidden behind it
    private void OpenWindow()
    {
        _ = !Options.IsOpened() || Options.TryClose();
        _window.Toggle(_window.Current);
    }

    private void OpenVerify()
    {
        if (_build.Update is null) _build.RunUpdateCheck();

        Show("version");
    }

    // Once the world is up: api.github.com only after a yes.
    private void AskForUpdateCheck()
    {
        _capi.Event.LevelFinalize -= AskForUpdateCheck;
        if (_settings.UpdateAsked || !Assert(_build.Version.Length > 0)) return;

        _sampler.Quiet();
        _ask = new HudAsk(_capi, _settings, _fonts, _snap,
            (KometText.Translate("update-ask-title"), KometText.Translate("update-ask")),
            yes => (_settings.UpdateAsked, _settings.UpdateCheck) = (true, yes));
        _ask.OnClosed += _sampler.Quiet;
        if (!_ask.TryOpen())
        {
            _capi.Logger.Warning("Komet: update opt-in dialog could not be opened, update notices stay off");
        }
    }

    private void Dump() => _capture.Capture("now", 0);

    private void OnSettingsChanged()
    {
        _sampler.SettingsChanged();
        _window.Invalidate(true);
        _overlay.Invalidate();
        if (_settings.UpdateCheck && _build.Update is null) _build.RunUpdateCheck();

        _capi.Event.UnregisterCallback(_saveCallback); // coalesces slider drags and hotkey bursts into one write
        // the options screen pauses singleplayer; without the flag the engine refuses (developer mode crashes)
        _saveCallback = _capi.Event.RegisterCallback(_ => _settings.Save(_capi), 500, true);
    }

    // The window opened, closed or switched views: the shown view's collectors run, it hears that it shows
    private void WindowChanged()
    {
        if (_disposed) return;

        _sampler.Quiet();
        var open = _window.IsOpened();
        (_sampler.Needs, _sampler.WindowOpen) = (open ? _window.Current.Needs : HudNeeds.None, open);
        _sampler.Refresh();
        if (open) _window.Current.Shown();
    }

    // New numbers: the window and the overlay compose again
    private void Sampled(float elapsed)
    {
        _window.Invalidate();
        _overlay.Invalidate();
    }

    private void LevelReady()
    {
        _capi.Event.LevelFinalize -= LevelReady;
        ModTimes.Ready(); // every mod has started and patched
    }

    private void Render(float deltaTime)
    {
        if (!Assert(deltaTime is >= 0 and < 10))
        {
            return; // NaN fails too
        }

        var profiler = _capi.World.FrameProfiler;
        // Without this mark the Ortho renderers before the HUD were booked to the HUD
        profiler.Mark("ortho-before-komet-hud");
        _sampler.Frame(deltaTime, profiler);
        profiler.Mark("komet-hud-interval");
        _overlay.Toast();
        profiler.Mark("komet-hud-panel");
        _overlay.Draw(_settings.Visible, _settings.ShowDetails || _window.IsOpened());

        _shaders.Draw(); // the HUD shown or not
        profiler.Mark("komet-hud");
    }

    private void RegisterCommands(ICoreClientAPI capi)
    {
        if (!NotNull(capi.ChatCommands)) return;

        _ = capi.ChatCommands.GetOrCreate("komet").BeginSubCommand("workbench")
            .WithDescription(KometText.Translate("hotkey-hud-workbench"))
            .HandleWith(_ => OpenDeveloper()).EndSubCommand();
        _ = capi.ChatCommands.GetOrCreate("komet").BeginSubCommand("dev")
            .WithDescription(KometText.Translate("cmd-dev"))
            .HandleWith(_ => OpenDeveloper()).EndSubCommand();
    }

    private TextCommandResult OpenDeveloper() =>
        Options.OpenDeveloper()
            ? TextCommandResult.Success()
            : TextCommandResult.Error(KometText.Translate("cmd-hud-open-failed"));
}
