using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Komet.Core;
using Komet.Diagnostics;

namespace Komet.Bench;

// Flies the scenario from a Before-stage renderer at RenderOrder -1, ahead of camera, tesselator, physics and HUD,
// inert until LevelFinalize. Kinematic like SystemCinematicCamera.AdvanceTo; the clock is the engine's dt.
internal sealed partial class BenchDriver : IRenderer
{
    // Seconds of scenario time one frame may advance; route speeds are capped against it
    public const double MaxStep = 2;

    public const int Spikes = 10; // per segment
    private const int PulseMs = 2000, Burst = 6;
    private const double BurstAt = 1.0, BurstGap = 0.25;
    private const string Done = "komet bench done", LapCommand = "/time set 10:00";

    // Time keeps running (with /time stop particles pile to the pool limit); the hour is reset in each lap settle
    private static readonly string[] Commands =
    [
        LapCommand, "/weather setprecip -1", "/weather acp off", "/weather set clearsky", "/weather setw still",
        "/weather setev noevent", "/serverconfig entityspawning false"
    ];

    private readonly ICoreClientAPI _capi;
    private readonly Timer? _pulse;
    private readonly BenchRecorder _recorder;
    private readonly ResourceStats _resources = new();
    private readonly BenchRun _run;
    private readonly BenchShots? _shots;
    private int _burst;
    private bool _captured;
    private int _command, _index = -1;
    private long _frames;
    private (double X, double Y, double Z) _home, _from, _landing, _lift;
    private float _joinYaw = float.NaN;
    private long _joinedAt;
    private bool _modeRequested, _profilerOwned, _resourcesFailed;
    private Phase _phase = Phase.Waiting;
    private (double X, double Y, double Z, double Yaw) _pose;
    private double _t, _clock, _climbSeconds;

    public BenchDriver(ICoreClientAPI capi, BenchRun run, BenchRecorder recorder)
    {
        (_capi, _run, _recorder) = (capi, run, recorder);
        if (!NotNull(capi.Event) || !Assert(run.Segments.Length > 0)) return;

        if (run.Config.Shots)
        {
            _shots = new BenchShots(Path.GetDirectoryName(run.Config.Output) ?? ".");
            capi.Event.RegisterRenderer(_shots, EnumRenderStage.Done, "komet-bench-shots");
        }

        // bench.sh's freeze watchdog: a timer thread writes the frame count, so a hung frame shows as a stopped count
        if (run.Config.Output is { Length: > 0 } output)
        {
            _pulse = new Timer(_ => Pulse(output + ".pulse"), null, PulseMs, PulseMs);
        }

        capi.Event.LevelFinalize += Finalized;
        capi.Event.LeaveWorld += Left;
        capi.Event.ChatMessage += Chat;
    }

    double IRenderer.RenderOrder => -1.0;
    int IRenderer.RenderRange => int.MaxValue;

    // LeaveWorld fires at the top of DestroyGameSession, before renderers are disposed: the run is over here
    public void Dispose()
    {
        _ = Assert(_phase != Phase.Running) && Assert(!ReferenceEquals(FrameClock.Sink, _recorder));
        _shots?.Dispose();
        _pulse?.Dispose();
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
    {
        _ = Interlocked.Increment(ref _frames);
        if (_phase is Phase.Waiting or Phase.Finished || !Assert(stage == EnumRenderStage.Before) ||
            !Finite(deltaTime))
        {
            return;
        }

        try
        {
            if (_phase == Phase.Boot)
            {
                Boot();
            }
            else
            {
                Frame(Math.Clamp(deltaTime, 0f, (float)MaxStep));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _capi.Logger.Error("Komet bench: {0}", e);
            Finish("exception: " + e.Message);
        }
    }

    private void Pulse(string path)
    {
        try
        {
            var frames = Interlocked.Read(ref _frames);
            File.WriteAllText(path, string.Create(CultureInfo.InvariantCulture,
                $"{frames} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _capi.Logger.Warning("Komet bench: pulse not written: {0}", e.Message);
        }
    }

    // Each arm: the player's values (engine values with "engine"), then its settings; error names the first bad knob
    internal static int[][] ArmValues(int[] baseline, IReadOnlyList<BenchArm> arms, out string? error)
    {
        var values = new int[arms.Count][];
        error = null;
        if (!Assert(baseline.Length == Knobs.Count) || !Assert(arms.Count <= BenchConfig.MaxArms)) return values;

        for (var a = 0; a < Math.Min(arms.Count, BenchConfig.MaxArms); a++)
        {
            values[a] = arms[a].Engine ? Knobs.Snapshot(true) : (int[])baseline.Clone();
            foreach (var setting in arms[a].Settings.Bounded(Knobs.MaxKnobs))
            {
                var knob = Knobs.Find(setting.Key);
                if (knob >= 0 && Knobs.InRange(knob, setting.Value) && Index(knob, values[a].Length))
                {
                    values[a][knob] = setting.Value;
                }
                else
                {
                    error ??= Unresolved(a, setting.Key, knob >= 0);
                }
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

    // Our own exit lands here too, after Finish; earlier means someone left mid-run
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

    // The world is up: everything the result states about the run is read once, here
    private void Boot()
    {
        var player = _capi.World.Player;
        if (!_capi.PlayerReadyFired || player?.Entity == null || _capi.IsGamePaused || !NotNull(_run.Config)) return;

        var id = _capi.World.SavegameIdentifier;
        if (!_run.Config.AnyWorld && !string.Equals(id, _run.Config.SavegameId, StringComparison.Ordinal))
        {
            Finish($"savegame {id} is not the configured {_run.Config.SavegameId}: a new world would be benchmarked");
            return;
        }

        var position = player.Entity.Pos;
        if (!Finite(position.X) || !Finite(position.Z)) return;

        if (_run.Config.CaptureSeconds > 0 && !_captured) Capture((float)_run.Config.CaptureSeconds);

        if (Joining()) return;

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

    // The world as joined, before the setup sets hour and weather (JoinSeconds): true while waiting. gui "help": the
    // camera sweeps over the ground blocks around the player (the block interaction help recomposes at each), under a
    // debug capture that lands in Logs/komet-debug
    private bool Joining()
    {
        var wait = _run.Config.JoinSeconds;
        if (wait <= 0 || _joinedAt < 0) return false;

        var now = Stopwatch.GetTimestamp();
        if (_joinedAt == 0) _joinedAt = now;

        if (_run.Config.Gui == BenchGui.Help) Sweep((now - _joinedAt) / (double)Stopwatch.Frequency);

        if (now - _joinedAt < wait * Stopwatch.Frequency) return true;

        _shots?.Take("shot-join.png");
        _joinedAt = -1;
        return
            Assert(wait > 0) && Assert(_joinedAt < 0); // the shot is taken this frame; the setup starts with the next
    }

    // Komet's own debug capture, as a player's ".komet debug" (ExecuteUnparsed drops the leading dot)
    private void Capture(float seconds)
    {
        _captured = true;
        if (!NotNull(_capi.ChatCommands) || !Assert(seconds > 0) || _capi.World?.Player is not { } player) return;

        var caller = new Caller { Type = EnumCallerType.Player, Player = player, CallerPrivileges = ["*"] };
        _capi.ChatCommands.ExecuteUnparsed(string.Create(CultureInfo.InvariantCulture, $".komet debug {seconds:0} en"),
            new TextCommandCallingArgs { Caller = caller },
            result => _capi.Logger.Notification("Komet bench: capture: {0}", result.StatusMessage));
    }

    // Yaw swings 60° either way at 0.4 rad/s, the view pitched to the ground a few blocks ahead
    private void Sweep(double seconds)
    {
        if (_capi.World?.Player?.Entity?.Pos is not { } pos || !Finite(seconds)) return;

        if (float.IsNaN(_joinYaw)) _joinYaw = pos.Yaw;

        var yaw = _joinYaw + (float)(Math.Sin(seconds * 0.4) * Math.PI / 3);
        var pitch = MathF.PI + 0.55f;
        (pos.Yaw, pos.Pitch) = (yaw, pitch);
        (_capi.Input.MouseYaw, _capi.Input.MousePitch) = (yaw, pitch);
    }

    // ResourceStats rethrows its first failure, then stays silent: no cpu and memory, the run goes on
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
}
