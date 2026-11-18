using Komet.Core;
using Vintagestory.Client;
using Vintagestory.Server;

namespace Komet.Chunks;

// The integrated server feeds a local client as fast as it can: per 20 ms tick it requests up to 4 × 4 chunk columns
// and sends up to 192 × 8 chunks (MagicNum, ServerSystemSendChunks). At a join with a large view distance that is tens of
// thousands of chunks within seconds, far more than the client tessellates; the flood (disk reads, worldgen, packets,
// decoding) allocated ~250 MB/s and the GC paused the game a fifth of the time. Every 250 ms the pacer scales both
// numbers by one factor: down while the GC pause share is high or the tessellation backlog piles up, back up once both
// calm down. The server requests nearest first, so the terrain around the player still comes first; only the far rest
// arrives later. Only the integrated server (singleplayer); the values the world started with return when it ends or
// the switch goes off.
internal static class JoinPacer
{
    // Loading should not show: the server is slowed as soon as collections take 3 % of the time (a quiet world: ~1-2 %),
    // not from 8 % (a user's exploring protocol: 5.5 % went unchecked); far terrain arrives later for it
    private const int TickMs = 250, CriticalBacklog = 6000, MaxBacklog = 3000, CalmBacklog = 1000;
    private const double HighPause = 0.03, CriticalPause = 0.12, CalmPause = 0.015;
    private const double InitialScale = 0.25, Down = 0.6, Up = 1.2, MinScale = 1.0 / 256;

    private static ICoreClientAPI? _api;
    private static long _listener;
    private static (int Request, int Send)? _engine; // MagicNum as the world started
    private static (TimeSpan Pause, long At) _last;
    private static bool _enabled = true;

    private static double Scale { get; set; } = 1;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value) Apply(1);
        }
    }

    public static void Install(ICoreClientAPI api)
    {
        Stop();
        if (!NotNull(api.Event)) return;

        _api = api;
        _engine = (MagicNum.ChunksColumnsToRequestPerTick, MagicNum.ChunksToSendPerTick);
        (_last, Scale) = ((GC.GetTotalPauseDuration(), Environment.TickCount64), 1);
        _listener = api.Event.RegisterGameTickListener(Step, TickMs);
        if (_enabled) Apply(InitialScale);
    }

    // The world ends or the feature stops: the server's numbers as they were
    public static void Stop()
    {
        Listeners.Unregister(_api?.Event, _listener);
        Apply(1);
        (_api, _listener, _engine) = (null, 0, null);
    }

    private static void Step(float dt)
    {
        var (pause, now) = (GC.GetTotalPauseDuration(), Environment.TickCount64);
        var (wall, paused) = (now - _last.At, (pause - _last.Pause).TotalMilliseconds);
        _last = (pause, now);
        if (!_enabled || wall <= 0) return;

        Apply(NextScale(Scale, paused / wall, RuntimeStats.chunksAwaitingTesselation));
    }

    internal static double NextScale(double scale, double pauseShare, int backlog)
    {
        if (backlog >= CriticalBacklog && pauseShare >= CriticalPause) return MinScale;

        if (pauseShare > HighPause || backlog > MaxBacklog)
        {
            return Math.Max(MinScale, scale * Down);
        }

        return pauseShare < CalmPause && backlog < CalmBacklog ? Math.Min(1, scale * Up) : scale;
    }

    private static void Apply(double scale)
    {
        if (_engine is not var (request, send)) return;

        Scale = scale;
        if (scale >= 1)
        {
            (MagicNum.ChunksColumnsToRequestPerTick, MagicNum.ChunksToSendPerTick) = (request, send);
            return;
        }

        MagicNum.ChunksColumnsToRequestPerTick = Math.Max(1, (int)Math.Round(request * scale));
        MagicNum.ChunksToSendPerTick = Math.Max(1, (int)Math.Round(send * scale));
    }
}
