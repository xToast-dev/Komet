using Vintagestory.Client.NoObf;
using static Komet.Api.KapiFeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Core;

// Core's own: the worker pool (steered every 250 ms by the world), background thread priority, and the GC latency.
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class CoreFeatures
{
    private const int SteerMs = 250;

    internal static readonly Feature[] All =
    [
        new("WorkerPool")
        {
            Order = 810,
            Knobs =
            [
                new Knob("WorkerThreads", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => WorkerPool.Wanted,
                    v => WorkerPool.Wanted = v) { Weighed = true }
            ],
            Install = c => StartPool(c.Api),
            Stop = c => StopPool(c?.Api),
            Probe = () => WorkerPool.FrameOff || WorkerPool.BackgroundOff ? Failed : Active
        },
        new("BackgroundPriority")
        {
            Order = 880,
            Install = _ => BackgroundPriority.Start(),
            Knobs =
            [
                Switch("BackgroundPriority", "chunks", "threads", () => BackgroundPriority.Enabled,
                    on => BackgroundPriority.Enabled = on)
            ],
            Stop = _ => BackgroundPriority.Stop()
        },
        new("GcLatency")
        {
            Order = 890,
            Install = _ => GcLatency.Install(),
            Knobs =
            [
                Switch("GcLatency", "misc", "garbage", () => GcLatency.Enabled, on => GcLatency.Enabled = on)
                    with
                    {
                        Weighed = true
                    }
            ],
            Stop = _ => GcLatency.Stop()
        }
    ];

    private static long _steer;

    // The pool follows the world's threads and the wanted count on the main thread's tick (no tick without a client: tests)
    private static void StartPool(ICoreClientAPI? api)
    {
        if (api?.Event is { } events)
        {
            _steer = events.RegisterGameTickListener(_ => WorkerPool.Steer(api.World as ClientMain), SteerMs);
        }
    }

    // One still in a job after WorkerPool.JoinMs fails the assertion
    private static void StopPool(ICoreClientAPI? api)
    {
        Listeners.Unregister(api?.Event, _steer);
        _steer = 0;
        _ = Assert(WorkerPool.Stop());
    }
}
