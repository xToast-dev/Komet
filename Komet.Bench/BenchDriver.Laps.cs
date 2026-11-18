using System.Globalization;
using Komet.Core;
using Komet.Hud;
using Komet.Rendering;
using Vintagestory.API.MathTools;

namespace Komet.Bench;

// BenchDriver: stepping through segments and laps, their settings, and the flight between them.
internal sealed partial class BenchDriver
{
    private bool Step()
    {
        var segment = _run.Segments[_index];
        if (!Assert(_phase == Phase.Running) || !Assert(_t >= 0)) return false;

        if (_index > 0 && _recorder is { Count: 0, Dropped: 0 })
        {
            // Setup spans several frames, so an empty recorder means FrameClock's window_RenderFrame patch never ran
            Finish("the recorder saw no frame: FrameClock is not installed");
            return false;
        }

        var done = segment.Kind switch
        {
            BenchKind.Setup => Setup(),
            BenchKind.Climb or BenchKind.Land => _t >= _climbSeconds,
            _ => _t >= segment.Seconds
        };
        if (_phase != Phase.Running || (done && !Next())) return false;

        Place();
        return true;
    }

    private bool Next()
    {
        if (!Index(_index, _run.Segments.Length) || !Assert(_phase == Phase.Running)) return false;

        End(_index);
        if (_index + 1 < _run.Segments.Length)
        {
            Enter(_index + 1);
            return true;
        }

        _run.Complete = true;
        Finish(null);
        return false;
    }

    private void Enter(int index)
    {
        if (!Index(index, _run.Segments.Length) || !Assert(index > _index)) return;

        var segment = _run.Segments[index];
        (_index, _t) = (index, 0);
        CapturePreviousLap(index);

        switch (segment.Kind)
        {
            case BenchKind.Climb:
                Climb();
                break;
            case BenchKind.Land:
                Land();
                break;
            case BenchKind.LapSettle when Index(segment.Arm, _run.ArmValues.Length):
                EnterLapSettle(segment);
                break;
            case BenchKind.Setup or BenchKind.Settle:
                _run.Status(segment.Name);
                break;
        }
    }

    private void CapturePreviousLap(int index)
    {
        if (_shots is not null && index > 0 &&
            _run.Segments[index - 1] is { Kind: BenchKind.LapSettle, Warmup: false } settled &&
            Index(settled.Arm, _run.Config.Arms.Count))
        {
            _shots.Take(string.Create(CultureInfo.InvariantCulture,
                $"shot-{_run.Config.Arms[settled.Arm].Name}-lap{settled.Lap}.png"));
        }
    }

    private void EnterLapSettle(BenchSegment segment)
    {
        if (segment.Lap == 0 && segment.Warmup == _run.Config.WarmupLaps > 0) StartLate();

        ApplyLapSettings(segment);
        _capi.SendChatMessage(Time());
        if (segment.Lap == 0) _run.Status(segment.Warmup ? "warmup" : "measuring");

        OpenLapViews(segment);
    }

    private void ApplyLapSettings(BenchSegment segment)
    {
        if (_run.Config.Gui == BenchGui.Options)
        {
            var name = string.Create(CultureInfo.InvariantCulture,
                $"shot-menu-{(segment.Warmup ? "warmup-" : "")}lap{segment.Lap}.png");
            BenchGui.Switch(_capi, _run.ArmValues[segment.Arm], _run.Config.MenuPage,
                _shots is { } shots ? () => shots.Take(name) : null);
        }
        else
        {
            _ = Knobs.Apply(_run.ArmValues[segment.Arm]);
        }
    }

    private void OpenLapViews(BenchSegment segment)
    {
        if (_run.Config.HudWindow == "cycle") HudOverlay.ShowForBench?.Invoke(segment.Lap);

        if (_run.Config.Gui == BenchGui.Creative) BenchGui.Open(_capi);

        if (_run.Config.Gui == BenchGui.Map) BenchGui.OpenMap(_capi);

        if (_run.Config.Gui == BenchGui.Debug) HudOverlay.CaptureForBench?.Invoke(BenchGui.CaptureSeconds);
    }

    private void StartLate()
    {
        foreach (var id in _run.Config.LateStart.Bounded(16))
        {
            var mod = ShaderMods.All(_capi).Find(m => m.Id == id);
            var started = mod is not null && ShaderMods.Start(_capi, mod);
            _capi.Logger.Notification("Komet bench: late start of {0}: {1}", id, started);
        }
    }

    private void Climb()
    {
        if (_capi.World.Player?.Entity?.Pos is { } position) _pose = (position.X, position.Y, position.Z, _pose.Yaw);

        _from = (_pose.X, _pose.Y, _pose.Z);
        _landing = _from;
        _climbSeconds = GlideSeconds(_from, _home);
    }

    // From where the laps left the player down to the ground spot the world was joined at (Climb's start)
    private void Land()
    {
        // down where the player is: the join spot after a lap home, the terrain just flown to without route.back
        var ground =
            _capi.World?.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)_pose.X, (int)_pose.Y, (int)_pose.Z,
                0)) ?? -1;
        if (ground > 0 && Math.Abs(_pose.X - _landing.X) + Math.Abs(_pose.Z - _landing.Z) > 8)
        {
            _landing = (_pose.X, ground + 1.5, _pose.Z);
        }

        _lift = (_pose.X, _pose.Y, _pose.Z);
        _climbSeconds = GlideSeconds(_lift, _landing);
        _run.Status("landing");
    }

    // A straight flight at ClimbSpeed: its seconds, at least one
    private static double GlideSeconds((double X, double Y, double Z) from, (double X, double Y, double Z) to)
    {
        var (dx, dy, dz) = (to.X - from.X, to.Y - from.Y, to.Z - from.Z);
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return Finite(distance) && Assert(distance < 1e7) ? Math.Max(1, distance / BenchScenario.ClimbSpeed) : 1;
    }

    // Where that flight is after t of its seconds, facing the route's heading
    private static (double X, double Y, double Z, double Yaw) Glide((double X, double Y, double Z) from,
        (double X, double Y, double Z) to, double t, double seconds)
    {
        var s = Assert(seconds >= 1) && Finite(t) ? Math.Clamp(t / seconds, 0, 1) : 1;
        return (from.X + (to.X - from.X) * s, from.Y + (to.Y - from.Y) * s, from.Z + (to.Z - from.Z) * s,
            BenchScenario.Heading);
    }

    private void End(int index)
    {
        if (!Index(index, _run.Segments.Length) || !Assert(index == _index)) return;

        SampleResources(); // closes the interval the previous sample opened: this segment
        _run.Logs[index] = _resourcesFailed
            ? new BenchSegmentLog(true, _t, double.NaN, double.NaN, 0, 0)
            : new BenchSegmentLog(true, _t, _resources.CpuPercent, _resources.SystemCpuPercent, _resources.WorkingSetMb,
                _resources.ManagedUsedMb);
    }
}
