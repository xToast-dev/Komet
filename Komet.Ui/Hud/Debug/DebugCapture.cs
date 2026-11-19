using System.Globalization;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Hud;

// The capture and protocol service. .komet debug: every collector on, each frame recorded, ending in DebugProtocol
// (clipboard, Logs/komet-debug, the Protocol tab). Modes: timed (10 s), "now" (3 s), "spike [ms]" (armed until a frame
// exceeds ms, then 2 s), "stop"; "en"/"de" language.
internal sealed partial class DebugCapture : IDisposable
{
    private const float DefaultSeconds = 10, NowSeconds = 3, SpikeWindow = 20, PostRoll = 2, JoinSeconds = 60;
    private const double DefaultSpikeMs = 50;
    internal const int MaxSeconds = 600; // the longest recording; the protocol tab's last duration chip
    private readonly HudBuild _build;
    private readonly ICoreClientAPI _capi;
    private readonly Dictionary<string, (string, double)[]> _debugModTop = [];
    private readonly Dictionary<string, (double Sum, int Intervals)> _debugMods = [];
    private readonly Action _opened; // the window on the protocol tab
    private readonly HudSampler _sampler;
    private readonly HudSettings _settings;

    private DebugFrames? _debug;
    private float _debugLeft, _debugTotal;
    private long _debugLogFrom;
    private string _debugMode = "", _debugTrigger = "";
    private double _debugSpikeMs;
    private DateTimeOffset _debugStarted;
    private Task? _debugWriting;
    private bool _disposed;
    private RuntimeEvents? _events; // allocation samples and lock waits, on while a capture runs
    private bool _joinPending = true; // the world's first frame not yet seen: DebugOnJoin starts a recording there
    private System.Func<string, DebugReport>? _rebuild; // the last protocol again in another language

    public DebugCapture(ICoreClientAPI capi, HudSettings settings, HudSampler sampler, HudBuild build, Action opened)
    {
        (_capi, _settings, _sampler, _build, _opened) = (capi, settings, sampler, build, opened);
        sampler.Recorded = Frame;
        sampler.Interval += Interval;
    }

    // The last protocol, its text (file and clipboard format) and its compare with the one before
    internal DebugReport LastReport { get; private set; } = DebugReport.Empty;
    internal string LastText { get; private set; } = "";
    internal DebugCompare LastCompare { get; private set; } = DebugCompare.None;

    internal (bool Running, bool Armed, double Elapsed, double Total, long Frames, int Spikes) Progress =>
        _debug is { } debug
            ? (true, _debugMode == "spike" && _debugTrigger.Length == 0, debug.Now, _debugTotal, debug.Count,
                _sampler.Spikes.Count)
            : (_debugWriting is { IsCompleted: false }, false, 0, 0, 0, 0);

    // Frames are being recorded (not yet writing)
    internal bool Recording => _debug is not null;
    private bool Running => _debug is not null || _debugWriting is { IsCompleted: false };
    private double SpikeThresholdMs => _settings.ToastMs > 0 ? _settings.ToastMs : DefaultSpikeMs;

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _sampler.Interval -= Interval;
        _sampler.Recorded = null;
        _ = _events?.Stop();
        (_debug, _events, DebugTimings.Capturing, _sampler.Capturing) = (null, null, false, false);
    }

    public void Register(ICoreClientAPI capi)
    {
        if (!NotNull(capi.ChatCommands) || !NotNull(capi.ChatCommands.Parsers)) return;

        _ = capi.ChatCommands.GetOrCreate("komet").BeginSubCommand("debug")
            .WithDescription(KometText.Translate("cmd-debug"))
            .WithArgs(capi.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(args => DebugCommand(args[0] as string ?? "")).EndSubCommand();
    }

    // Each frame's first stage: the world's first frame, with DebugOnJoin on, records its first minute (joins compare
    // with joins)
    public void BeforeFrame()
    {
        if (!_joinPending) return;

        _joinPending = false;
        if (!_settings.DebugOnJoin || Running || _sampler.Benching) return;

        Capture("record", JoinSeconds);
        _debugTrigger = KometText.Translate("hud-dbg-join");
        _capi.ShowChatMessage(KometText.Translate("cmd-debug-join-started", DebugText.Num(JoinSeconds, "F0")));
    }

    private TextCommandResult DebugCommand(string line)
    {
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!Assert(words.Length < 64)) return TextCommandResult.Error("");

        if (words is ["join"])
        {
            _settings.DebugOnJoin = !_settings.DebugOnJoin;
            return TextCommandResult.Success(
                KometText.Translate(_settings.DebugOnJoin ? "cmd-debug-join-on" : "cmd-debug-join-off"));
        }

        var (mode, seconds, spikeMs, language) = ("record", DefaultSeconds, double.NaN, "");
        foreach (var word in words.Bounded(64))
        {
            var number = double.TryParse(word, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1e6;
            if (word is "en" or "de")
            {
                language = word;
            }
            else if (word is "now" or "stop" or "spike")
            {
                mode = word;
            }
            else if (number && mode == "spike")
            {
                spikeMs = value;
            }
            else if (number)
            {
                seconds = (float)value;
            }
            else
            {
                return TextCommandResult.Error(KometText.Translate("cmd-debug-usage"));
            }
        }

        if (language.Length > 0)
        {
            _settings.DebugLanguage = language; // only from a line that parsed
        }

        if (mode == "stop") return StopDebug();

        if (TryCapture(mode, seconds, spikeMs) is { } refused)
        {
            return TextCommandResult.Error(KometText.Translate(refused));
        }

        return TextCommandResult.Success(mode == "spike"
            ? KometText.Translate("cmd-debug-armed", DebugText.Num(_debugSpikeMs, "F0"))
            : KometText.Translate("cmd-debug-started", DebugText.Num(_debugLeft, "F0")));
    }

    private void StartDebug(string mode, float seconds, double spikeMs)
    {
        _sampler.ResetMeasures();
        _debugMods.Clear();
        _debugModTop.Clear();
        _ = _events?.Stop();
        _debug = new DebugFrames();
        _events = RuntimeEvents.Start(); // right after the frames: one origin for frame and event times
        (_debugMode, _debugSpikeMs, _debugTrigger, _debugStarted) = (mode, spikeMs, "", DateTimeOffset.Now);
        _debugLeft = mode == "spike" ? float.PositiveInfinity : seconds;
        _debugTotal = mode == "spike" ? 0 : seconds;
        _debugLogFrom = DebugFiles.LogLength();
        _ = DebugTimings.TakeTimings(); // what an earlier capture left behind
        DebugTimings.Capturing = _sampler.Capturing = true;
        _sampler.Refresh();
        _sampler.Quiet(); // the first switch-on of the collectors patches the engine
    }

    private TextCommandResult StopDebug()
    {
        if (_debug is null) return TextCommandResult.Error(KometText.Translate("cmd-debug-idle"));

        if (_debugTrigger.Length == 0 && _debugMode == "spike") _debugTrigger = KometText.Translate("hud-dbg-stopped");

        FinishDebug();
        return TextCommandResult.Success(KometText.Translate("cmd-debug-stopped"));
    }

    private void Frame(in FrameRecord frame, bool steady)
    {
        if (_debug is not { } debug) return;

        var record = debug.Add(frame, _sampler.Passes.FrameTop, steady);
        if (_debugMode != "spike" || _debugTrigger.Length > 0 || !steady || record.DtMs < _debugSpikeMs) return;

        _debugTrigger = KometText.Translate("hud-dbg-triggered", DebugText.Num(record.DtMs, "F1"),
            DebugText.Num(record.At, "F1"));
        _debugLeft = PostRoll;
    }

    private void Interval(float elapsed)
    {
        if (_debug is null || !Assert(elapsed > 0)) return;

        _sampler.Accumulate();
        var timings = _sampler.Timings;
        for (var i = 0; i < ModTimes.MaxMods; i++)
        {
            if (timings.ModName(i) is { Length: > 0 } mod && timings.ModMs(i) is var ms && double.IsFinite(ms))
            {
                var (sum, intervals) = _debugMods.GetValueOrDefault(mod);
                _debugMods[mod] = (sum + ms, intervals + 1);
                _debugModTop[mod] = ModTop(i, _debugModTop.GetValueOrDefault(mod));
            }
        }

        _debugLeft -= elapsed;
        if (_debugLeft <= 0) FinishDebug();
    }

    // The place's named details; the previous array again when the count holds (no LINQ chain per mod and interval)
    private (string, double)[] ModTop(int place, (string, double)[]? previous)
    {
        var timings = _sampler.Timings;
        var count = 0;
        for (var r = 0; r < ModTimes.DetailCount; r++)
        {
            if (timings.DetailName(place, r).Length > 0) count++;
        }

        var top = previous is { } same && same.Length == count ? same : new (string, double)[count];
        var n = 0;
        for (var r = 0; r < ModTimes.DetailCount; r++)
        {
            if (timings.DetailName(place, r) is { Length: > 0 } name && Index(n, top.Length))
            {
                top[n] = (name, timings.DetailMs(place, r));
                n++;
            }
        }

        return top;
    }

    private void FinishDebug()
    {
        if (_debug is not { } debug) return;

        _sampler.Spikes.Rank();
        var input = Snapshot(debug);
        input.Events = _events?.Stop() ?? RuntimeEvents.Snapshot.Empty;
        (_debug, _events, DebugTimings.Capturing, _sampler.Capturing) = (null, null, false, false);
        _sampler.Refresh();
        var (capi, from) = (_capi, _debugLogFrom);
        _debugWriting = Task.Run(() =>
        {
            var (report, folder, compare, rebuild) = DebugFiles.Write(capi, input, from);
            var text = report?.Text() ?? ""; // built here, off the main thread
            capi.Event.EnqueueMainThreadTask(() => Delivered(report, text, folder, (compare, rebuild)), "komet-debug");
        });
    }

    // The last protocol again in another language, onto the clipboard
    internal void Relabel(string code)
    {
        if (_rebuild is not { } rebuild || !Assert(code is "en" or "de")) return;

        LastReport = rebuild(code);
        LastText = LastReport.Text();
        _capi.Input.ClipboardText = LastText;
    }

    private void Delivered(DebugReport? report, string text, string folder,
        (DebugCompare Compare, System.Func<string, DebugReport>? Rebuild) next)
    {
        if (_disposed) return;

        if (report is null || text.Length == 0)
        {
            _capi.ShowChatMessage(KometText.Translate("cmd-debug-failed", folder));
            return;
        }

        _capi.Input.ClipboardText = text;
        _capi.ShowChatMessage(KometText.Translate("cmd-debug-done", folder));
        (LastReport, LastText, LastCompare, _rebuild) = (report, text, next.Compare, next.Rebuild);
        _opened();
    }

    // seconds 0: until stopped; spikeMs NaN: the overlay's threshold
    internal void Capture(string mode, float seconds, double spikeMs = double.NaN)
    {
        if (TryCapture(mode, seconds, spikeMs) is "cmd-debug-bench")
        {
            _capi.ShowChatMessage(KometText.Translate("cmd-debug-bench"));
        }
    }

    // The lang key of why no capture started, or null when one did
    private string? TryCapture(string mode, float seconds, double spikeMs)
    {
        if (!Assert(mode is "now" or "record" or "spike")) return "cmd-debug-usage";

        if (Running) return "cmd-debug-running";

        if (_sampler.Benching) return "cmd-debug-bench";

        var time = seconds <= 0 ? MaxSeconds : Math.Clamp(seconds, 1, MaxSeconds);
        if (mode == "now") time = NowSeconds;

        StartDebug(mode, time, double.IsFinite(spikeMs) && spikeMs > 0 ? spikeMs : SpikeThresholdMs);
        return null;
    }

    internal void EndCapture()
    {
        if (_debug is null) return;

        _ = StopDebug();
    }
}
