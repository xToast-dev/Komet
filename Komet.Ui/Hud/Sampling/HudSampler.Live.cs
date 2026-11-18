using Komet.Core;
using Komet.Diagnostics;
using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Hud;

// Snapshot taken each interval before collectors reset: views draw from it anytime, never a half interval.
internal sealed class HudLive
{
    public const int MaxPasses = 9, MaxThreads = 8;

    public readonly double[]
        KindBusy = [.. Enumerable.Repeat(double.NaN, RenderCost.Kinds)]; // by RenderCost kind, NaN unseen

    public readonly (string Key, double Ms)[] Passes = new (string, double)[MaxPasses];
    public readonly (string Name, double Busy, double MsPerFrame)[] Threads = new (string, double, double)[MaxThreads];
    public int DrawCalls, Triangles;

    public double Fps = double.NaN,
        Low1 = double.NaN,
        Low01 = double.NaN,
        AvgMs = double.NaN,
        P99 = double.NaN,
        GpuMs = double.NaN,
        ModsMs,
        RunQueueMs = double.NaN,
        CulledPercent = double.NaN,
        DrawnTriangles = double.NaN; // the engine's count less what Komet's GPU culling dropped

    public double Locks = double.NaN;
    public int PassCount;
    public int ThreadCount;
    public double TotalMs;

    public double Under8, Under12, Under25, Over25; // shares of the frame history, 0..100
    public (double Draws, double Left, double Segments, long MemoryMb, string[] Missing) Vulkan = (0, 0, 0, 0, []);
    public double WorkerFrame = double.NaN, WorkerTess = double.NaN; // percent of one thread, per worker
}

// The live snapshot and the rates rows read: counter growth per frame or second since the row's last read
internal sealed partial class HudSampler
{
    private readonly float[] _sortedHistory = new float[HudCanvas.GraphFrames * 4];

    private readonly Func<double>?[] _threadBusy = new Func<double>?[RenderCost.Kinds],
        _threadMs = new Func<double>?[RenderCost.Kinds];

    private double _elapsedTotal;
    private long _frameTotal; // frames and seconds since the HUD began, what the rows divide counter growth by
    private Func<double>? _locks, _workerFrame, _workerTess;
    private double _runQueueSum; // the interval's frames with a run-queue time
    private int _runQueues;

    private void TakeLive()
    {
        var l = Live;
        (l.Fps, l.Low1, l.Low01, l.AvgMs) = (Frames.Fps, Frames.Low1Fps, Frames.Low01Fps, Frames.AverageMs);
        (l.GpuMs, l.DrawCalls, l.Triangles) = (RenderCost.FrameGpuMs(Gpu.Ms), Frames.DrawCallsPerFrame,
            Frames.RenderedTriangles);
        (l.ModsMs, l.TotalMs, l.RunQueueMs) = (Timings.TotalExcept("game"), Passes.AverageTotalMs,
            _runQueues > 0 ? _runQueueSum / _runQueues : double.NaN);
        l.CulledPercent = OcclusionCulling.Enabled ? OcclusionCulling.CulledPercent : double.NaN;
        // the engine counts what it hands the GPU, before Komet's culling holds those draws and drops the hidden ranges
        l.DrawnTriangles = OcclusionCulling.Enabled && double.IsFinite(OcclusionCulling.CulledTriangles)
            ? Math.Max(0, l.Triangles - OcclusionCulling.CulledTriangles)
            : l.Triangles;
        History(l);
        l.PassCount = 0;
        for (var i = 0; i < Math.Min(RenderPassStats.Count, HudLive.MaxPasses); i++)
        {
            if (Passes.Key(i) is { Length: > 0 } key && Passes.AverageMs(i) > 0.005)
            {
                l.Passes[l.PassCount++] = (key, Passes.AverageMs(i));
            }
        }

        Threads(l);
        l.Vulkan = VulkanRenderer.Snapshot();
    }

    // scale × growth of part per growth of whole since this row's last read
    public static Func<double> Ratio(Func<double> part, Func<double> whole, double scale = 1)
    {
        Growth grown = new(part), of = new(whole);
        return () => scale * grown.Next() / of.Next();
    }

    public static Func<double> Share(Func<double> part, Func<double> rest) => Ratio(part, () => part() + rest(), 100);

    public Func<double> PerFrame(Func<double> total, double scale = 1) => Ratio(total, () => _frameTotal, scale);

    public Func<double> PerSecond(Func<double> total, double scale = 1) => Ratio(total, () => _elapsedTotal, scale);

    private void History(HudLive l)
    {
        var n = Math.Min(Frames.Recorded, Math.Min(Frames.HistoryLength, _sortedHistory.Length));
        if (n == 0)
        {
            (l.P99, l.Under8, l.Under12, l.Under25, l.Over25) = (double.NaN, 0, 0, 0, 0);
            return;
        }

        var first = Frames.HistoryLength - n;
        for (var i = 0; i < Math.Min(n, HudCanvas.GraphFrames * 4); i++)
        {
            _sortedHistory[i] = Frames.HistoryMs(first + i);
        }

        var sorted = _sortedHistory.AsSpan(0, n);
        sorted.Sort();
        l.P99 = FrameStats.Percentile(sorted, 990);
        int a = 0, b = 0, c = 0;
        foreach (var ms in sorted.Bounded(_sortedHistory.Length))
        {
            if (ms < 8)
            {
                a++;
            }
            else if (ms < 12)
            {
                b++;
            }
            else if (ms < 25)
            {
                c++;
            }
        }

        (l.Under8, l.Under12, l.Under25, l.Over25) =
            (100.0 * a / n, 100.0 * b / n, 100.0 * c / n, 100.0 * (n - a - b - c) / n);
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
            {
                l.Threads[l.ThreadCount++] = (KometText.Cached("hud-rc-", RenderCost.KindKey(k)), busy, ms);
            }
        }

        _workerFrame ??= PerSecond(static () => WorkerPool.FrameTicks * FrameClock.TickMs, 0.1);
        _workerTess ??= PerSecond(static () => WorkerPool.BackgroundTicks * FrameClock.TickMs, 0.1);
        _locks ??= PerSecond(() => Monitor.LockContentionCount);
        var running = Math.Max(1, WorkerPool.Running);
        (l.WorkerFrame, l.WorkerTess, l.Locks) = (_workerFrame() / running, _workerTess() / running, _locks());
    }

    private static Func<double> KindNs(int kind) => () => RenderCost.ThreadNs(kind);
}
