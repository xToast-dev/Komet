using System.Globalization;

namespace Komet.Bench;

// BenchDriver: per-frame work and the still, ground and turn shots of a segment.
internal sealed partial class BenchDriver
{
    private void Frame(float dt)
    {
        if (!Index(_index, _run.Segments.Length) || !Finite(dt)) return;

        BenchGui.Switching(_capi);
        var paused = _capi.IsGamePaused;
        if (!paused)
        {
            (_t, _clock) = (_t + dt, _clock + dt);
            TurnShot(dt);
            StillShots(dt);
            GroundShots(dt);
            if (!Step()) return;

            if (_run.Config.Gui == BenchGui.Creative && _run.Segments[_index].Kind == BenchKind.Still)
            {
                BenchGui.Scroll(_capi, dt);
            }
        }

        Stamp(paused);
    }

    // Still, a second in: six consecutive frames, then six a quarter second apart. The camera does not move, so what differs
    // between them flickers (a shadow map kept and redrawn, far foliage, a cache)
    private void StillShots(float dt)
    {
        var segment = _run.Segments[_index];
        if (_shots is null || segment.Kind != BenchKind.Still || segment.Warmup ||
            !Index(segment.Arm, _run.Config.Arms.Count))
        {
            _burst = 0;
            return;
        }

        if (_t < BurstAt || _burst >= 2 * Burst) return;

        if (_burst >= Burst && _t < BurstAt + (_burst - Burst + 1) * BurstGap) return;

        _shots.Take(string.Create(CultureInfo.InvariantCulture,
            $"burst-{_run.Config.Arms[segment.Arm].Name}-lap{segment.Lap}-{_burst:D2}.png"));
        _burst++;
        _ = Assert(dt >= 0);
    }

    private static int Quarter(BenchSegment segment, double t) => Math.Clamp((int)(4 * t / segment.Seconds), 0, 3);

    // Standing on the ground: one shot near the end of each quarter turn, the terrain around the player as it settled
    private void GroundShots(float dt)
    {
        var segment = _run.Segments[_index];
        if (_shots is null || segment.Kind != BenchKind.Ground || segment.Seconds <= 0) return;

        var quarter = Quarter(segment, _t);
        var at = (quarter + 0.9) * segment.Seconds / 4;
        if (_t < at || _t - dt >= at) return;

        _shots.Take(string.Create(CultureInfo.InvariantCulture, $"shot-ground-{quarter}.png"));
    }

    // Mid-turn as well: what a cache or reprojection gets wrong shows while the camera turns
    private void TurnShot(float dt)
    {
        var segment = _run.Segments[_index];
        if (_shots is null || segment.Kind != BenchKind.Rotate || segment.Warmup ||
            !Index(segment.Arm, _run.Config.Arms.Count) ||
            _t < segment.Seconds / 2 || _t - dt >= segment.Seconds / 2)
        {
            return;
        }

        _shots.Take(string.Create(CultureInfo.InvariantCulture,
            $"shot-{_run.Config.Arms[segment.Arm].Name}-lap{segment.Lap}-turn.png"));
    }
}
