using System.Globalization;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Hud;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Bench;

// BenchDriver: setup, placing the player, stamps and writing the result.
internal sealed partial class BenchDriver
{
    // RequestMode once, await the server, then one command per frame so replies keep their order
    private bool Setup()
    {
        if (_capi.World is not ClientMain game || _capi.World.Player.WorldData is not ClientWorldPlayerData data)
        {
            Finish("not a ClientMain world");
            return false;
        }

        if (!_modeRequested)
        {
            // Creative, not Spectator: spectators hide the held item and the HUD differs
            data.RequestMode(game, data.MoveSpeedMultiplier, data.PickingRange, EnumGameMode.Creative, true, true,
                EnumFreeMovAxisLock.None, ClientSettings.RenderMetaBlocks);
            game.AllowCameraControl = false;
            _modeRequested = true;
            return false;
        }

        if (data.CurrentGameMode != EnumGameMode.Creative || !data.FreeMove || !data.NoClip)
        {
            if (_t >= BenchScenario.ModeTimeout) Finish("the server did not grant creative fly mode with noclip");

            return false;
        }

        if (_command >= Commands.Length) return Assert(_command == Commands.Length);

        if (!Index(_command, Commands.Length)) return true;

        var command = Commands[_command++];
        if (_run.Config.Rain)
        {
            command = command.Replace("setprecip -1", "setprecip 1", StringComparison.Ordinal)
                .Replace("set clearsky", "set overcast", StringComparison.Ordinal);
        }

        if (!_run.Config.Spawning || !command.Contains("entityspawning", StringComparison.Ordinal))
        {
            _capi.SendChatMessage(command == LapCommand ? Time() : command);
        }

        return false;
    }

    // The configured hour as the game's command (LapCommand stands for it in the setup's list)
    private string Time()
    {
        var hour = _run.Config.Hour;
        return string.Create(CultureInfo.InvariantCulture,
            $"/time set {(int)hour:00}:{(int)((hour - (int)hour) * 60):00}");
    }

    // The player stands where it spawned until the server has made it a flyer
    private void Place()
    {
        var segment = _run.Segments[_index];
        var entity = _capi.World.Player?.Entity;
        if (segment.Kind == BenchKind.Setup || !NotNull(entity) || !Finite(_t)) return;

        if (segment.Kind == BenchKind.Climb)
        {
            _pose = Glide(_from, _home, _t, _climbSeconds);
        }
        else if (segment.Kind == BenchKind.Land)
        {
            _pose = Glide(_lift, _landing, _t, _climbSeconds);
        }
        else if
            (segment.Kind == BenchKind.Ground) // a quarter turn each quarter of the time: GroundShots looks all around
        {
            _pose = (_landing.X, _landing.Y, _landing.Z, BenchScenario.Heading + Math.PI / 2 * Quarter(segment, _t));
        }
        else
        {
            var pose = BenchScenario.Pose(segment, _t, BenchScenario.Heading, _run.Config.Speed * _run.Config.Leg);
            _pose = (_home.X + pose.X, _home.Y, _home.Z + pose.Z, pose.Yaw);
        }

        var pos = entity.Pos;
        // a NaN position would stick to the entity
        if (!Finite(_pose.X) || !Finite(_pose.Y) || !Finite(_pose.Z)) return;

        _ = pos.SetPos(_pose.X, _pose.Y, _pose.Z);
        (pos.Yaw, pos.Pitch) = ((float)_pose.Yaw, MathF.PI); // pitch π looks level
        _ = pos.Motion.Set(0, 0, 0);
        (_capi.Input.MouseYaw, _capi.Input.MousePitch) = ((float)_pose.Yaw, MathF.PI);
    }

    private void Stamp(bool paused)
    {
        if (!Index(_index, _run.Segments.Length) || !Finite(_pose.Yaw)) return;

        var segment = _run.Segments[_index];
        var flags = BenchFrameTags.None;
        if (paused) flags |= BenchFrameTags.Paused;

        if (!ScreenManager.Platform.IsFocused) flags |= BenchFrameTags.Unfocused;

        if (_t < BenchScenario.Discard) flags |= BenchFrameTags.Discard;

        if (segment.Warmup) flags |= BenchFrameTags.Warmup;

        if (!segment.Measured) flags |= BenchFrameTags.Unmeasured;

        _recorder.Stamp(_index, flags, (float)_pose.X, (float)_pose.Z, (float)_pose.Yaw);
    }

    private void Finish(string? error, bool exit = true)
    {
        if (_phase == Phase.Finished || !NotNull(_run)) return;

        FinishState(error);
        WriteResult(exit);
    }

    private void FinishState(string? error)
    {
        _phase = Phase.Finished;
        if (error is not null) _run.Fail(error);
        if (ReferenceEquals(FrameClock.Sink, _recorder)) FrameClock.Sink = null;

        if (_recorder.Recording) _recorder.Stop();

        Counting.Bench = false;
        // an options switch cut short (an exception mid-switch): Reset drops it, so unpause and close the menu here,
        // or the single-player server stays suspended into the exit
        if (_run.Config.Gui == BenchGui.Options && _capi is { IsGamePaused: true, World: ClientMain paused })
        {
            paused.PauseGame(false);
            HudOverlay.OptionsForBench?.Invoke(false, null);
        }

        BenchGui.Reset();
        _run.Finished = DateTime.UtcNow;
        if (_run.Baseline.Length == Knobs.Count) _ = Knobs.Apply(_run.Baseline);

        var profiler = _capi.World?.FrameProfiler; // only ours: End() of this frame then returns early, as for the HUD
        if (_profilerOwned && profiler is not null) profiler.Enabled = false;

        _profilerOwned = false;
    }

    private void WriteResult(bool exit)
    {
        _run.Status("writing");
        try
        {
            BenchReport.Write(_run, _recorder);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever went wrong, the exit must happen: an unattended run must not idle until the script's budget
            _run.Fail("result not written: " + e.Message);
            _capi.Logger.Error("Komet bench: result not written: {0}", e);
        }

        _run.Status(_run.Error is null ? "exiting" : "error");
        _capi.Logger.Notification("Komet bench: {0}, result in {1}", _run.Error ?? "complete", _run.Config.Output);
        if (_capi.World is not ClientMain game) return;

        game.AllowCameraControl = true;
        if (exit && _capi.IsSinglePlayer) Benchmark.Exit(game, Done);
    }

    private enum Phase
    {
        Waiting, // before LevelFinalize
        Boot, // waiting for the player
        Running,
        Finished
    }
}
