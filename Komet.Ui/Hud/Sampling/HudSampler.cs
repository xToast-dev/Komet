using System.Text;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Rendering;
using Vintagestory.Client;

namespace Komet.Hud;

// What the shown tab needs beyond the overlay's numbers: collectors that run only while it shows
[Flags]
internal enum HudNeeds
{
    None = 0,
    Threads = 1, // RenderCost's thread meters and the worker pool's timing
    Occlusion = 2, // GPU culling's read-back
    Log = 4 // the game log's tail
}

// A frame FrameClock recorded, as the sampler took it
internal delegate void FrameSink(in FrameRecord frame, bool steady);

// The sampling hub: every collector, the interval that samples them into HudLive, which of them run (the overlay,
// the shown tab's HudNeeds, a bench or capture), and the HUD's own bench.
internal sealed partial class HudSampler(ICoreClientAPI capi, HudSettings settings, Func<bool> showing) : IDisposable
{
    private const int WarmupMs = 5000, SettleMs = 30000;

    // Not steady after the HUD appears or around a dialog: JIT, first Cairo passes and composition owned the 0.1 % low
    // ~20 s.
    private const int QuietMs = 1000;

    // ReSharper disable once ReplaceWithPrimaryConstructorParameter - the second partial declaration below uses it
    private readonly ICoreClientAPI _capi = capi;
    private float _benchLeft, _sinceInterval;
    private long _consumed; // FrameClock records taken
    private bool _disposed, _profilerOwned, _resumed, _settled, _wasVisible;
    private bool _endNow; // a settings change wants fresh numbers at the next frame instead of an interval later
    private int _interval, _skip;
    private double _lastInterval;
    private (bool Showing, bool Threads) _primed;
    private long _quietUntil;
    private Action? _sample;
    private Task? _sampling;


    public FrameStats Frames { get; } = new();
    public SpikeLedger Spikes { get; } = new();
    public GpuStats Gpu { get; } = new();
    public ResourceStats Resources { get; } = new();
    public RenderPassStats Passes { get; } = new();
    public ModTimes Timings { get; } = new();
    public LogStats Log { get; } = new("client-main.log");
    public HudLive Live { get; } = new();

    // The report's rows, built once by HudPanels: what a bench averages and the protocol's HUD section quotes
    public HudPanel[] Panels { get; set; } = [];

    // The shown tab's collectors; Refresh applies a change
    public HudNeeds Needs { get; set; }

    // A capture runs: every collector on, panels accumulated by its owner
    public bool Capturing { get; set; }

    // The window is open (any tab): VRAM is read in full for its numbers
    public bool WindowOpen { get; set; }

    public bool Benching => _benchLeft > 0;
    public bool Forced => _benchLeft > 0 || Capturing;

    // Each interval after the live snapshot, before the collectors reset (elapsed seconds)
    public event Action<float>? Interval;

    // Each recorded frame, after the spike ledger saw it
    public FrameSink? Recorded { get; set; }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        Gpu.Dispose();
        if (_profilerOwned) _capi.World.FrameProfiler.Enabled = false;

        ModTimes.Enabled = FrameClock.Stats = Counting.Hud = WorkerPool.Timed = false;
    }

    public void Quiet() => _quietUntil = Math.Max(_quietUntil, Environment.TickCount64 + QuietMs);

    public void Skip()
    {
        if (!_disposed) _skip = 2;
    }

    // The overlay shown or the interval changed: fresh numbers at the next frame
    public void SettingsChanged()
    {
        var shown = settings.Visible && !_wasVisible;
        if (shown) Quiet();

        if (shown || !HudParts.Near(settings.Interval, _lastInterval)) (_endNow, _interval) = (true, 0);

        (_wasVisible, _lastInterval) = (settings.Visible, settings.Interval);
        Refresh();
    }

    // Switches the collectors to what is wanted now. Only disables what Komet enabled and Begin()s at once, or End() of
    // the running frame crashes without a root entry.
    public void Refresh()
    {
        if (_disposed) return;

        var measuring = Forced || showing();
        var (patched, costPatched, epoch) = (ModTimes.Patched, RenderCost.Patched, Counting.Epoch);
        var threads = (Needs & HudNeeds.Threads) != 0;
        Counting.Hud = Forced;
        if (Counting.Epoch != epoch)
        {
            foreach (var panel in Panels.Bounded(HudPanels.Count))
            {
                panel.Prime();
            }
        }

        ModTimes.Enabled = measuring;
        // opt-in even for the HUD's bench: its probes would skew a bench of the rest
        RenderCost.Enabled = Capturing || threads;
        WorkerPool.Timed = threads; // the worker meters, which only these tabs show
        PrimeRates(threads);
        // first switch-on patches the engine this frame: 3-29 ms of Harmony in tests
        if (ModTimes.Patched != patched || RenderCost.Patched != costPatched) Quiet();

        FrameClock.Stats = measuring;
        Profiler(measuring);
    }

    private void Profiler(bool measuring)
    {
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
        var primed = (showing(), threads);
        if ((primed.Item1 && !_primed.Showing) || (threads && !_primed.Threads)) Threads(new HudLive());

        _primed = primed;
    }

    // Per Ortho/Before stage frame while the HUD measures: the clock's record, and the interval when it is due
    public void Frame(float deltaTime, FrameProfilerUtil profiler)
    {
        var steady = Steady();
        if (FrameClock.Completed != _consumed)
        {
            Record(FrameClock.Last, profiler, steady);
        }
        else
        {
            Frames.Record(deltaTime, float.NaN, steady);
        }

        (_sinceInterval, _frameTotal, _elapsedTotal) =
            (_sinceInterval + deltaTime, _frameTotal + 1, _elapsedTotal + deltaTime);
        if (_sinceInterval >= settings.Interval || (_endNow && Frames.Frames > 0)) EndInterval();
    }

    private void Record(in FrameRecord frame, FrameProfilerUtil profiler, bool steady)
    {
        var consumed = _consumed;
        _consumed = FrameClock.Completed; // taken even when broken, or the HUD would stop at it
        if (!Assert(frame.Index >= consumed) || !Assert(frame.DtMs >= 0) || !Finite(frame.GcMs)) return;

        Frames.Record((float)(frame.DtMs / 1000), (float)frame.GcMs, steady);
        if (profiler.Enabled && frame.Root != null)
        {
            Passes.AddFrame(frame, steady);
        }
        else
        {
            Passes.AddFrame(null, steady); // clears the previous frame's marks
        }

        _ = Spikes.Add(frame, Passes.FrameTop, steady, _capi.InWorldEllapsedMilliseconds / 1000.0);
        if (double.IsFinite(frame.RunQueueMs))
        {
            (_runQueueSum, _runQueues) = (_runQueueSum + frame.RunQueueMs, _runQueues + 1);
        }

        Recorded?.Invoke(frame, steady);
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
        if (!Assert(_sinceInterval > 0) || !Assert(Frames.Frames > 0))
        {
            (_sinceInterval, _endNow) = (0, false);
            return;
        }

        Frames.SampleWindow();
        Passes.UpdateOrder();
        Spikes.Rank();
        if (ModTimes.Enabled) Timings.Update(Frames.Frames, _sinceInterval);

        Gpu.Sample();
        RenderCost.Sample();
        if (Forced || (Needs & HudNeeds.Occlusion) != 0 || settings.ShowDetails) // the details card shows culling too
        {
            OcclusionCulling.Sample(); // bench and protocol rows read it too
        }

        _capi.World.FrameProfiler.Mark("komet-hud-readback"); // the GPU's numbers, each read a wait for Mesa's glthread
        if (_interval % HudSettings.SlowEvery == 0) SlowInterval();

        if (showing()) TakeLive();

        if (_benchLeft > 0) Bench(_sinceInterval);

        Interval?.Invoke(_sinceInterval);
        foreach (var panel in Panels.Bounded(HudPanels.Count))
        {
            panel.ResetPeaks();
        }

        Frames.Reset();
        Passes.Reset();
        (_sinceInterval, _interval, _endNow) = (0, _interval + 1, false);
        (_runQueueSum, _runQueues) = (0, 0);
    }

    private void SlowInterval()
    {
        Gpu.SampleVram(WindowOpen || Forced);
        if (_sampling?.Exception?.GetBaseException() is { } failed)
        {
            _capi.Logger.Warning("Komet HUD: sampling failed ({0})", failed.Message);
        }

        if (_sampling?.IsCompleted != false) _sampling = Task.Run(_sample ??= SampleSlow);
    }

    private void SampleSlow()
    {
        if (_disposed) return;

        Resources.Sample();
        if ((Needs & HudNeeds.Log) != 0) Log.Sample();
    }
}

// The HUD's own bench (".komet bench", the options' bench button): the panels' means over its seconds, copied when done
internal sealed partial class HudSampler
{
    public void StartBench(float seconds)
    {
        if (!Assert(seconds is > 0 and <= 3600)) return;

        if (Capturing)
        {
            _capi.ShowChatMessage(KometText.Translate("cmd-debug-running"));
            return;
        }

        var running = _benchLeft > 0; // not restarted, only its time left is told
        _capi.ShowChatMessage(KometText.Translate("hud-bench-start", running ? MathF.Ceiling(_benchLeft) : seconds));
        if (running) return;

        ResetMeasures();
        _benchLeft = seconds;
        Refresh();
    }

    // A bench or capture measures from its own start: worst frames, spikes and the panels' means begin anew
    public void ResetMeasures()
    {
        foreach (var panel in Panels.Bounded(HudPanels.Count))
        {
            panel.ResetBench();
        }

        Frames.ResetHistory();
        Passes.ResetWorst();
        Spikes.Reset();
    }

    public void Accumulate()
    {
        foreach (var panel in Panels.Bounded(HudPanels.Count))
        {
            panel.Accumulate();
        }
    }

    private void Bench(float elapsed)
    {
        if (!Assert(elapsed > 0) || !Assert(_benchLeft > 0)) return;

        Accumulate();
        _benchLeft -= elapsed;
        if (_benchLeft > 0) return;

        Spikes.Rank(); // the report reads the ledger's rows, which otherwise wait for the next interval
        var report = Means();
        if (report.Length > 0)
        {
            _capi.Input.ClipboardText = report;
            _capi.Logger.Notification("{0}", report);
            _capi.ShowChatMessage(KometText.Translate("hud-bench-done"));
        }

        Refresh();
    }

    // The panels' means and the recent spikes, as the bench copies and the protocol's HUD section quotes them
    public string Means()
    {
        var text = new StringBuilder();
        foreach (var panel in Panels.Bounded(HudPanels.Count))
        {
            panel.AppendMeans(text);
        }

        if (text.Length > 0) AppendSpikes(text);

        return text.ToString();
    }

    private void AppendSpikes(StringBuilder text)
    {
        if (Spikes.Stored == 0) return;

        _ = text.Append("\n\n[").Append(KometText.Translate("hud-spikes-recent")).Append(']');
        var marks = new StringBuilder();
        for (var age = 0; age < Math.Min(Spikes.Stored, SpikeLedger.Recent); age++)
        {
            var spike = Spikes.Latest(age);
            _ = marks.Clear();
            for (var rank = 0; rank < SpikeLedger.TopMarks; rank++)
            {
                if (Spikes.LatestMark(age, rank) is not { Name: { } mark } entry) continue;

                _ = marks.Append(marks.Length > 0 ? ", " : "").Append(HudParts.Describe(mark)).Append(' ')
                    .Append(HudParts.Millis(entry.Ms)).Append(" ms");
                if (entry.Calls > 1) _ = marks.Append(" ×").Append(entry.Calls);
            }

            _ = text.Append('\n').Append(KometText.Translate("hud-spike-line", KometText.Format(spike.AtSeconds, "F1"),
                HudParts.Millis(spike.DtMs), HudParts.Describe(spike.Cause), HudParts.Millis(spike.GcMs),
                HudParts.Millis(spike.OutsideMs), HudParts.Millis(spike.JitMs), HudParts.Millis(spike.RunQueueMs),
                marks.ToString()));
        }
    }
}
