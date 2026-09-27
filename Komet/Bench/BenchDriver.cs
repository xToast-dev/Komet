using System.ComponentModel;
using System.Globalization;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Bench;

internal readonly record struct BenchChat(double Seconds, string Type, string Text);

// What the driver saw of a segment once it ended; the frames themselves are in the recorder. NaN cpu: no resource sample.
internal readonly record struct BenchSegmentLog(
    bool Ran, double Seconds, double CpuPercent, double SystemCpuPercent, long WorkingSetMb, long ManagedMb);

// Everything the result is written from, apart from the frames
internal sealed class BenchRun
{
    public const int MaxChat = 64, MaxChatText = 300, MaxInfo = 64;

    public BenchRun(BenchConfig config, BenchSegment[] segments)
    {
        if (!NotNull(config) || !Assert(segments.Length is > 0 and <= BenchScenario.MaxSegments))
            segments = [new BenchSegment(BenchKind.Setup, -1, 0, false, false, 1)];
        (Config, Segments, Logs) = (config, segments, new BenchSegmentLog[segments.Length]);
        Array.Fill(Logs, new BenchSegmentLog(false, 0, double.NaN, double.NaN, 0, 0));
    }

    public BenchConfig Config { get; }
    public BenchSegment[] Segments { get; }
    public BenchSegmentLog[] Logs { get; }
    public List<BenchChat> Chat { get; } = [];
    public List<KeyValuePair<string, string>> Info { get; } = [];
    public int[] Baseline { get; set; } = [];
    public int[][] ArmValues { get; set; } = [];
    public string? Error { get; private set; }
    public bool Complete { get; set; }
    public DateTime Started { get; } = DateTime.UtcNow;
    public DateTime Finished { get; set; }

    // The first error wins: later ones are usually its consequences
    public void Fail(string error)
    {
        if (!Assert(error.Length > 0) || !Assert(error.Length < 4096)) error = "unknown error";
        Error ??= error;
    }

    // One line the script's watchdog polls, written at phase changes only, all of them outside measured frames. A run with no
    // "loaded" in time hangs before the mods (at a login screen, for instance), one with no "booted" before the world.
    public void Status(string phase)
    {
        if (!Assert(phase.Length > 0) || !Assert(Config.Status.Length > 0)) return;
        try
        {
            var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.WriteAllText(Config.Status, string.Create(CultureInfo.InvariantCulture, $"{phase} {seconds}\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail("status file not written: " + e.Message);
        }
    }

    public void AddChat(double seconds, string type, string text)
    {
        if (!Finite(seconds) || !NotNull(text) || Chat.Count >= MaxChat) return;
        Chat.Add(new BenchChat(seconds, type, text.Length > MaxChatText ? text[..MaxChatText] : text));
    }

    public void AddInfo(string key, string? value)
    {
        if (!Assert(key.Length > 0) || !Assert(Info.Count < MaxInfo)) return;
        Info.Add(new KeyValuePair<string, string>(key, value ?? ""));
    }
}

// Flies the scenario. A renderer at RenderOrder -1 in the Before stage: ClientEventManager.RegisterRenderer sorts ascending, so it
// runs ahead of PlayerCamera (0), the chunk tesselator (0.99), the player physics (1.0) and the HUD (0.9), and every one of them sees
// this frame's pose. It stays inert until LevelFinalize: the world is still being built out of the main-thread queue before that.
// Movement is kinematic, the way SystemCinematicCamera.AdvanceTo does it: position, yaw and the mouse angles written every frame,
// motion zeroed, after RequestMode has made the player a noclip flyer. The scenario clock is the engine's dt, standing still while
// the game is paused.
internal sealed class BenchDriver : IRenderer
{
    // Seconds of scenario time one frame may advance; route speeds are capped against it
    public const double MaxStep = 2;

    public const int Spikes = 10; // per segment
    private const string Done = "komet bench done", LapCommand = "/time set 10:00";

    // Time keeps running: particles age with the calendar (ParticlePoolQuads.OnNewFrame scales dt by SpeedOfTime / 60), so with
    // /time stop they never die, pile up to the pool limit and make particles-tick a cost no player sees. The clock is put back to
    // the same hour at every lap instead, in the lap settle, which is not measured.
    private static readonly string[] Commands =
    [
        LapCommand, "/weather setprecip -1", "/weather acp off", "/weather set clearsky", "/weather setw still",
        "/weather setev noevent", "/serverconfig entityspawning false"
    ];

    private readonly ICoreClientAPI _capi;
    private readonly BenchRecorder _recorder;
    private readonly ResourceStats _resources = new();
    private readonly BenchRun _run;
    private int _command, _index = -1;
    private (double X, double Y, double Z) _home, _from;
    private bool _modeRequested, _profilerOwned, _resourcesFailed;
    private Phase _phase = Phase.Waiting;
    private (double X, double Y, double Z, double Yaw) _pose;
    private double _t, _clock, _climbSeconds;

    public BenchDriver(ICoreClientAPI capi, BenchRun run, BenchRecorder recorder)
    {
        (_capi, _run, _recorder) = (capi, run, recorder);
        if (!NotNull(capi.Event) || !Assert(run.Segments.Length > 0)) return;
        capi.Event.LevelFinalize += Finalized;
        capi.Event.LeaveWorld += Left;
        capi.Event.ChatMessage += Chat;
    }

    double IRenderer.RenderOrder => -1.0;
    int IRenderer.RenderRange => int.MaxValue;

    // LeaveWorld fires at the top of ClientMain.DestroyGameSession, before the engine disposes renderers, so the run is over here
    public void Dispose() =>
        _ = Assert(_phase != Phase.Running) && Assert(!ReferenceEquals(FrameClock.Sink, _recorder));

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        if (_phase is Phase.Waiting or Phase.Finished || !Assert(stage == EnumRenderStage.Before) ||
            !Finite(deltaTime)) return;
        try
        {
            if (_phase == Phase.Boot) Boot();
            else Frame(Math.Clamp(deltaTime, 0f, (float)MaxStep));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _capi.Logger.Error("Komet bench: {0}", e);
            Finish("exception: " + e.Message);
        }
    }

    // Each arm is the player's values, or with "engine" every knob's engine value, and then the arm's own settings. Names resolve
    // here, after LevelFinalize: error names the first that no registered knob has, or whose value is out of its range.
    internal static int[][] ArmValues(int[] baseline, IReadOnlyList<BenchArm> arms, out string? error)
    {
        var values = new int[arms.Count][];
        error = null;
        if (!Assert(baseline.Length == Knobs.Count) || !Assert(arms.Count <= BenchConfig.MaxArms)) return values;
        for (var a = 0; a < Math.Min(arms.Count, BenchConfig.MaxArms); a++)
        {
            values[a] = arms[a].Engine ? Knobs.Snapshot(engine: true) : (int[])baseline.Clone();
            foreach (var setting in arms[a].Settings.Bounded(Knobs.MaxKnobs))
            {
                var knob = Knobs.Find(setting.Key);
                if (knob >= 0 && Knobs.InRange(knob, setting.Value) && Index(knob, values[a].Length))
                    values[a][knob] = setting.Value;
                else error ??= Unresolved(a, setting.Key, knob >= 0);
            }
        }

        return values;
    }

    private static string Unresolved(int arm, string key, bool known)
    {
        if (!Index(arm, BenchConfig.MaxArms) || !Assert(key.Length > 0)) return "bench.json: an arm names no knob";
        var at = string.Create(CultureInfo.InvariantCulture, $"bench.json: arms[{arm}].set.{key}");
        return at + (known ? " must be a value inside the knob's range" : " names no registered knob");
    }

    private void Finalized()
    {
        if (!Assert(_phase == Phase.Waiting) || !NotNull(_capi.Event)) return;
        _capi.Event.LevelFinalize -= Finalized;
        _phase = Phase.Boot;
        _run.Status("booted");
    }

    // Our own exit arrives here too, after Finish; anything earlier means someone left the world mid-run
    private void Left()
    {
        if (!NotNull(_capi.Event) || !Assert(_run.Segments.Length > 0)) return;
        _capi.Event.LeaveWorld -= Left;
        _capi.Event.ChatMessage -= Chat;
        if (_phase is Phase.Boot or Phase.Running) Finish("the world was left before the run ended", false);
    }

    private void Chat(int groupId, string message, EnumChatType chattype, string data)
    {
        if (_phase != Phase.Running || !NotNull(message) || !Finite(_clock)) return;
        _run.AddChat(_clock, chattype.ToString(), message);
    }

    // The world is up; everything a result must state about the run is read once, here
    private void Boot()
    {
        var player = _capi.World.Player;
        if (!_capi.PlayerReadyFired || player?.Entity == null || _capi.IsGamePaused || !NotNull(_run.Config)) return;
        var id = _capi.World.SavegameIdentifier;
        if (!string.Equals(id, _run.Config.SavegameId, StringComparison.Ordinal))
        {
            Finish($"savegame {id} is not the configured {_run.Config.SavegameId}: a new world would be benchmarked");
            return;
        }

        var position = player.Entity.Pos;
        if (!Finite(position.X) || !Finite(position.Z)) return;
        Benchmark.Collect(_capi, _run);
        _run.AddInfo("komet.holds", Features.HoldsText()); // a held knob stays at its engine value in every arm
        _run.Baseline = Knobs.Snapshot();
        _run.ArmValues = ArmValues(_run.Baseline, _run.Config.Arms, out var unresolved);
        if (unresolved is not null)
        {
            Finish(unresolved);
            return;
        }

        _home = (position.X, BenchScenario.Altitude, position.Z);
        _pose = (position.X, position.Y, position.Z, position.Yaw);
        // the HUD's pattern: Begin right away, or End of this frame finds no root
        var profiler = _capi.World.FrameProfiler;
        if (NotNull(profiler) && !profiler.Enabled)
        {
            _profilerOwned = profiler.Enabled = true; // switched off again in Finish; one the HUD runs stays the HUD's
            profiler.Begin();
        }

        SampleResources();
        Counting.Bench = true; // every arm alike: tessNearAvg and the tess columns count only while Counting.On
        _recorder.Start();
        FrameClock.Sink = _recorder;
        _phase = Phase.Running;
        Enter(0);
    }

    // ResourceStats rethrows its first failure of the process handle or /proc and stays silent afterwards: the segments then
    // report no cpu and memory, and the run goes on
    private void SampleResources()
    {
        if (_resourcesFailed || !Assert(_phase != Phase.Finished)) return;
        try
        {
            _resources.Sample();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Win32Exception or NotSupportedException)
        {
            _resourcesFailed = true;
            _capi.Logger.Warning("Komet bench: resource sampling failed, segments report no cpu or memory: {0}",
                e.Message);
        }
    }

    private void Frame(float dt)
    {
        if (!Index(_index, _run.Segments.Length) || !Finite(dt)) return;
        var paused = _capi.IsGamePaused;
        if (!paused)
        {
            (_t, _clock) = (_t + dt, _clock + dt);
            if (!Step()) return;
        }

        Stamp(paused);
    }

    // False once the run has ended in this frame
    private bool Step()
    {
        var segment = _run.Segments[_index];
        if (!Assert(_phase == Phase.Running) || !Assert(_t >= 0)) return false;
        if (_index > 0 && _recorder.Count == 0 && _recorder.Dropped == 0)
        {
            // Setup alone spans several frames, so an empty recorder here means FrameClock's window_RenderFrame patch never ran
            Finish("the recorder saw no frame: FrameClock is not installed");
            return false;
        }

        var done = segment.Kind switch
        {
            BenchKind.Setup => Setup(),
            BenchKind.Climb => _t >= _climbSeconds,
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
        switch (segment.Kind)
        {
            case BenchKind.Climb:
                Climb();
                break;
            case BenchKind.LapSettle when Index(segment.Arm, _run.ArmValues.Length):
                _ = Knobs.Apply(_run.ArmValues[segment.Arm]); // the lap settle and the discard window follow
                _capi.SendChatMessage(LapCommand); // every lap starts at the hour the setup set
                if (segment.Lap == 0) _run.Status(segment.Warmup ? "warmup" : "measuring");
                break;
            case BenchKind.Setup or BenchKind.Settle:
                _run.Status(segment.Name);
                break;
        }
    }

    // From wherever fly mode left the player, which may have drifted since the boot position was read
    private void Climb()
    {
        if (_capi.World.Player?.Entity?.Pos is { } position) _pose = (position.X, position.Y, position.Z, _pose.Yaw);
        _from = (_pose.X, _pose.Y, _pose.Z);
        var (dx, dy, dz) = (_home.X - _from.X, _home.Y - _from.Y, _home.Z - _from.Z);
        var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        _climbSeconds = Finite(distance) && Assert(distance < 1e7)
            ? Math.Max(1, distance / BenchScenario.ClimbSpeed) : 1;
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

    // RequestMode once, then wait for the server's answer, then one command per frame so the replies keep their order
    private bool Setup()
    {
        if (_capi.World is not ClientMain game || _capi.World.Player.WorldData is not ClientWorldPlayerData data)
        {
            Finish("not a ClientMain world");
            return false;
        }

        if (!_modeRequested)
        {
            // Creative rather than Spectator: EntityPlayerShapeRenderer hides the held item for spectators and the HUD differs
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
        _capi.SendChatMessage(Commands[_command++]);
        return false;
    }

    // The player stands where it spawned until the server has made it a flyer
    private void Place()
    {
        var segment = _run.Segments[_index];
        var entity = _capi.World.Player?.Entity;
        if (segment.Kind == BenchKind.Setup || !NotNull(entity) || !Finite(_t)) return;
        if (segment.Kind == BenchKind.Climb)
        {
            var s = Math.Clamp(_t / _climbSeconds, 0, 1);
            _pose = (_from.X + (_home.X - _from.X) * s, _from.Y + (_home.Y - _from.Y) * s,
                _from.Z + (_home.Z - _from.Z) * s, BenchScenario.Heading);
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

    // Writes the result, puts the knobs back the way the player had them, and leaves when the run was unattended
    private void Finish(string? error, bool exit = true)
    {
        if (_phase == Phase.Finished || !NotNull(_run)) return;
        _phase = Phase.Finished;
        if (error is not null) _run.Fail(error);
        if (ReferenceEquals(FrameClock.Sink, _recorder)) FrameClock.Sink = null;
        if (_recorder.Recording) _recorder.Stop();
        Counting.Bench = false;
        _run.Finished = DateTime.UtcNow;
        _ = Assert(_run.Finished >= _run.Started);
        if (_run.Baseline.Length == Knobs.Count) _ = Knobs.Apply(_run.Baseline);
        var profiler = _capi.World?.FrameProfiler; // only ours: End() of this frame then returns early, as for the HUD
        if (_profilerOwned && profiler is not null) profiler.Enabled = false;
        _profilerOwned = false;
        _run.Status("writing");
        try
        {
            BenchReport.Write(_run, _recorder);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Whatever went wrong, the exit below still has to happen: an unattended run must not idle until the script's budget
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
