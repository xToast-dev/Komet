using System.Diagnostics;

namespace Komet.Core;

// The engine lets a tick listener go only on the main thread and throws on any other. A world torn down from another
// thread (a crash path, or the bench ending a run it refused) drops its listeners with its event manager anyway, so
// there the call is left out instead of throwing out of Dispose and crashing the client.
internal static class Listeners
{
    public static void Unregister(IEventAPI? events, long listener)
    {
        if (events is null || listener == 0 || Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return;

        events.UnregisterGameTickListener(listener);
    }
}

// A tick that also runs while the game is paused. The engine's game tick listeners stand still in a paused singleplayer
// game, but chunks keep arriving, tessellating and uploading: a scan that follows them (FarLod, DeepCulling, FarFoliage)
// then fell behind and the distance showed half-made states until the game went on. Runs first in the Before stage,
// every everyMs of real time, with the real seconds since its last run.
internal sealed class FrameTimer : IRenderer
{
    private readonly ICoreClientAPI _api;
    private readonly long _every;
    private readonly Action<float> _run;
    private long _last;

    private FrameTimer(ICoreClientAPI api, int everyMs, Action<float> run) =>
        (_api, _every, _run, _last) = (api, everyMs * Stopwatch.Frequency / 1000, run,
            Stopwatch.GetTimestamp());

    public double RenderOrder => 0.001;
    public int RenderRange => 0;

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        var now = Stopwatch.GetTimestamp();
        if (now - _last < _every) return;

        var seconds = (float)((now - _last) / (double)Stopwatch.Frequency);
        _last = now;
        _run(Math.Min(seconds, 1f));
    }

    public void Dispose()
    {
    }

    public static FrameTimer Start(ICoreClientAPI api, string name, int everyMs, Action<float> run)
    {
        var timer = new FrameTimer(api, Math.Max(1, everyMs), run);
        api.Event.RegisterRenderer(timer, EnumRenderStage.Before, name);
        return timer;
    }

    public void Stop()
    {
        if (Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId)
        {
            _api.Event.UnregisterRenderer(this, EnumRenderStage.Before);
        }
    }
}
