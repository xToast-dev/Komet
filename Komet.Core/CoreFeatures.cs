using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Core;

// Core's own: the worker pool, background thread priority, and the GC latency.
// In install order; Core's Features places them among the other modules'.
internal static class CoreFeatures
{
    internal static readonly Feature[] All =
    [
        new("WorkerPool")
        {
            Knobs = [new("WorkerThreads", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => WorkerPool.Wanted,
                v => WorkerPool.Wanted = v) { Weighed = true }],
            Stop = _ => StopPool(),
            Probe = () => WorkerPool.FrameOff || WorkerPool.BackgroundOff ? Failed : Active
        },
        new("BackgroundPriority")
        {
            Install = _ => BackgroundPriority.Start(),
            Knobs = [Switch("BackgroundPriority", "chunks", "threads", () => BackgroundPriority.Enabled, on => BackgroundPriority.Enabled = on)],
            Stop = _ => BackgroundPriority.Stop()
        },
        new("GcLatency")
        {
            Install = _ => GcLatency.Install(),
            Knobs = [Switch("GcLatency", "misc", "garbage", () => GcLatency.Enabled, on => GcLatency.Enabled = on)
                with { Weighed = true }],
            Stop = _ => GcLatency.Stop()
        }
    ];

    // One still in a job after WorkerPool.JoinMs fails the assertion
    private static void StopPool() => _ = Assert(WorkerPool.Stop());
}
