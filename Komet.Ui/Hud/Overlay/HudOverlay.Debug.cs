using Komet.Api;
using Komet.Diagnostics;
using Komet.Core;

namespace Komet.Hud;

// /komet debug: every collector on, each frame recorded, ending in DebugProtocol (clipboard, Logs/komet-debug). Modes:
// timed (10 s), "now" (3 s), "spike [ms]" (armed until a frame exceeds ms, then 2 s), "stop"; "en"/"de" language.
internal sealed partial class HudOverlay
{
    private const float DefaultSeconds = 10, NowSeconds = 3, SpikeWindow = 20, PostRoll = 2, JoinSeconds = 60;
    private const double DefaultSpikeMs = 50;
    internal const int MaxSeconds = 600; // the longest recording; the debug window's last duration chip

    private DebugFrames? _debug;
    private RuntimeEvents? _events; // allocation samples and lock waits, on while a capture runs
    private string _debugMode = "", _debugTrigger = "";
    private float _debugLeft, _debugTotal;
    private double _debugSpikeMs;
    private DateTimeOffset _debugStarted;
    private long _debugLogFrom;
    private Task? _debugWriting;
    private readonly Dictionary<string, (double Sum, int Intervals)> _debugMods = [];
    private readonly Dictionary<string, (string, double)[]> _debugModTop = [];
    private System.Func<string, string>? _rebuild; // the last protocol again in another language
    private bool _joinPending = true; // the world's first frame not yet seen: DebugOnJoin starts a recording there

    internal string LastProtocol { get; private set; } = "";
    internal DebugCompare LastCompare { get; private set; } = DebugCompare.None;

    internal (bool Running, bool Armed, double Elapsed, double Total, long Frames, int Spikes) DebugProgress =>
        _debug is { } debug
            ? (true, _debugMode == "spike" && _debugTrigger.Length == 0, debug.Now, _debugTotal, debug.Count, _spikes.Count)
            : (_debugWriting is { IsCompleted: false }, false, 0, 0, 0, 0);
    internal bool DebugRunning => _debug is not null || _debugWriting is { IsCompleted: false };
    internal double SpikeThresholdMs => _settings.ToastMs > 0 ? _settings.ToastMs : DefaultSpikeMs;

    private bool Forced => _benchLeft > 0 || _debug is not null;

    private void RegisterDebug(ICoreClientAPI capi)
    {
        if (!NotNull(capi.ChatCommands) || !NotNull(capi.ChatCommands.Parsers)) return;
        _ = capi.ChatCommands.GetOrCreate("komet").BeginSubCommand("debug")
            .WithDescription(KometText.Translate("cmd-debug"))
            .WithArgs(capi.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(args => DebugCommand(args[0] as string ?? "")).EndSubCommand();
    }

    // The world's first frame: with DebugOnJoin on, its first minute is recorded (joins compare with joins)
    private void RecordJoin()
    {
        _joinPending = false;
        if (!_settings.DebugOnJoin || DebugRunning || _benchLeft > 0) return;
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
            return TextCommandResult.Success(KometText.Translate(_settings.DebugOnJoin ? "cmd-debug-join-on" : "cmd-debug-join-off"));
        }

        var (mode, seconds, spikeMs, language) = ("record", DefaultSeconds, double.NaN, "");
        foreach (var word in words.Bounded(64))
        {
            var number = double.TryParse(word, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && value is > 0 and < 1e6;
            if (word is "en" or "de") language = word;
            else if (word is "now" or "stop" or "spike") mode = word;
            else if (number && mode == "spike") spikeMs = value;
            else if (number) seconds = (float)value;
            else return TextCommandResult.Error(KometText.Translate("cmd-debug-usage"));
        }

        if (language.Length > 0) _settings.DebugLanguage = language; // only from a line that parsed
        if (mode == "stop") return StopDebug();
        if (TryCapture(mode, seconds, spikeMs) is { } refused) return TextCommandResult.Error(KometText.Translate(refused));
        return TextCommandResult.Success(mode == "spike"
            ? KometText.Translate("cmd-debug-armed", DebugText.Num(_debugSpikeMs, "F0"))
            : KometText.Translate("cmd-debug-started", DebugText.Num(_debugLeft, "F0")));
    }

    private void StartDebug(string mode, float seconds, double spikeMs)
    {
        ResetMeasures();
        _debugMods.Clear();
        _debugModTop.Clear();
        _ = _events?.Stop();
        _debug = new DebugFrames();
        _events = RuntimeEvents.Start(); // right after the frames: one origin for frame and event times
        (_debugMode, _debugSpikeMs, _debugTrigger, _debugStarted) = (mode, spikeMs, "", DateTimeOffset.Now);
        _debugLeft = mode == "spike" ? float.PositiveInfinity : seconds;
        _debugTotal = mode == "spike" ? 0 : seconds;
        _debugLogFrom = DebugFiles.LogLength();
        _ = KometDebug.TakeTimings(); // what an earlier capture left behind
        KometDebug.Capturing = true;
        UpdateProfiler();
        Quiet(); // the first switch-on of the collectors patches the engine
    }

    private TextCommandResult StopDebug()
    {
        if (_debug is null) return TextCommandResult.Error(KometText.Translate("cmd-debug-idle"));
        if (_debugTrigger.Length == 0 && _debugMode == "spike") _debugTrigger = KometText.Translate("hud-dbg-stopped");
        FinishDebug();
        return TextCommandResult.Success(KometText.Translate("cmd-debug-stopped"));
    }

    private void DebugFrame(DebugFrames debug, in FrameRecord frame, bool steady)
    {
        var record = debug.Add(frame, _passes.FrameTop, steady);
        if (_debugMode != "spike" || _debugTrigger.Length > 0 || !steady || record.DtMs < _debugSpikeMs) return;
        _debugTrigger = KometText.Translate("hud-dbg-triggered", DebugText.Num(record.DtMs, "F1"), DebugText.Num(record.At, "F1"));
        _debugLeft = PostRoll;
    }

    private void DebugInterval(float elapsed)
    {
        if (!Assert(elapsed > 0) || _debug is null) return;
        foreach (var panel in _panels.Bounded(PanelCount)) panel.Accumulate();
        for (var i = 0; i < ModTimes.MaxMods; i++)
            if (_timings.ModName(i) is { Length: > 0 } mod && _timings.ModMs(i) is var ms && double.IsFinite(ms))
            {
                var (sum, intervals) = _debugMods.GetValueOrDefault(mod);
                _debugMods[mod] = (sum + ms, intervals + 1);
                _debugModTop[mod] = ModTop(i, _debugModTop.GetValueOrDefault(mod));
            }

        _debugLeft -= elapsed;
        if (_debugLeft <= 0) FinishDebug();
    }

    // The place's named details; the previous array again when the count holds (no LINQ chain per mod and interval)
    private (string, double)[] ModTop(int place, (string, double)[]? previous)
    {
        var count = 0;
        for (var r = 0; r < ModTimes.DetailCount; r++)
            if (_timings.DetailName(place, r).Length > 0) count++;
        var top = previous is { } same && same.Length == count ? same : new (string, double)[count];
        var n = 0;
        for (var r = 0; r < ModTimes.DetailCount; r++)
            if (_timings.DetailName(place, r) is { Length: > 0 } name && Index(n, top.Length))
            {
                top[n] = (name, _timings.DetailMs(place, r));
                n++;
            }

        return top;
    }

    private void FinishDebug()
    {
        if (_debug is not { } debug) return;
        _spikes.Rank();
        var input = DebugSnapshot(debug);
        input.Events = _events?.Stop() ?? RuntimeEvents.Snapshot.Empty;
        (_debug, _events, KometDebug.Capturing) = (null, null, false);
        UpdateProfiler();
        var (capi, from) = (_capi, _debugLogFrom);
        _debugWriting = Task.Run(() =>
        {
            var (text, folder, compare, rebuild) = DebugFiles.Write(capi, input, from);
            capi.Event.EnqueueMainThreadTask(() => Delivered(text, folder, compare, rebuild), "komet-debug");
        });
    }

    internal void Relabel(string code)
    {
        if (_rebuild is not { } rebuild || !Assert(code is "en" or "de")) return;
        LastProtocol = rebuild(code);
        _capi.Input.ClipboardText = LastProtocol;
    }

    private void Delivered(string text, string folder, DebugCompare compare, System.Func<string, string>? rebuild)
    {
        if (_disposed) return;
        if (text.Length == 0)
        {
            _capi.ShowChatMessage(KometText.Translate("cmd-debug-failed", folder));
            return;
        }

        _capi.Input.ClipboardText = text;
        _capi.ShowChatMessage(KometText.Translate("cmd-debug-done", folder));
        (LastProtocol, LastCompare, _rebuild) = (text, compare, rebuild);
        _debugWindow.Toggle(true);
    }

    // seconds 0: until stopped; spikeMs NaN: the overlay's threshold
    internal void Capture(string mode, float seconds, double spikeMs = double.NaN)
    {
        if (TryCapture(mode, seconds, spikeMs) is "cmd-debug-bench") _capi.ShowChatMessage(KometText.Translate("cmd-debug-bench"));
    }

    // The lang key of why no capture started, or null when one did
    private string? TryCapture(string mode, float seconds, double spikeMs)
    {
        if (!Assert(mode is "now" or "record" or "spike")) return "cmd-debug-usage";
        if (DebugRunning) return "cmd-debug-running";
        if (_benchLeft > 0) return "cmd-debug-bench";
        var time = seconds <= 0 ? MaxSeconds : Math.Clamp(seconds, 1, MaxSeconds);
        if (mode == "now") time = NowSeconds;
        StartDebug(mode, time, double.IsFinite(spikeMs) && spikeMs > 0 ? spikeMs : SpikeThresholdMs);
        return null;
    }

    // From a hint: armed at the overlay's threshold, with the window open on its progress
    private void SpikeCapture()
    {
        Capture("spike", 0);
        _debugWindow.Toggle(true);
    }

    internal void EndCapture()
    {
        if (_debug is null) return;
        _ = StopDebug();
    }
}
