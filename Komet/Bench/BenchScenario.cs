namespace Komet.Bench;

internal enum BenchKind : byte
{
    Setup, // fly mode, server commands
    Climb, // to the route's start, kinematically
    Settle, // a fixed time for the world around the start to stream in
    LapSettle, // the same between laps; the lap's arm is switched on as it begins
    Still,
    Rotate,
    Out,
    Turn,
    Back
}

// Lap -1 for the segments before the first lap. Far marks the turn at the far end of the route.
internal readonly record struct BenchSegment(BenchKind Kind, int Lap, int Arm, bool Warmup, bool Far, double Seconds)
{
    private static readonly string[] Names =
        ["setup", "climb", "settle", "lapsettle", "still", "rotate", "out", "turn", "back"];

    public bool Measured => Kind >= BenchKind.Still; // Setup's Seconds is its timeout, Climb's comes from the distance
    public string Name => Far ? "turn-far" : KindName(Kind);

    public static string KindName(BenchKind kind)
    {
        return Index((int)kind, Names.Length) && Assert(Names.Length == (int)BenchKind.Back + 1)
            ? Names[(int)kind]
            : "";
    }
}

// Offset from the route's start (home) and the yaw. Forward at yaw y is (sin y, 0, cos y), which is what
// EntityControls.CalcMovementVectors walks for a level pitch of π.
internal readonly record struct BenchPose(double X, double Z, double Yaw);

// The whole run as a flat list: setup, climb, settle, warm-up laps, measured laps. Every lap flies out along one line and back
// along the same line, so chunk streaming repeats identically, and the arms alternate in mirrored blocks (A B | B A | A B ...)
// in one process: drift and the out/back asymmetry cancel, and the numbers never cross a process boundary.
internal static class BenchScenario
{
    public const int MaxSegments = 256, SegmentsPerLap = 7, Prelude = 3;

    // The route runs at the absolute height y = Altitude in the player's column (x, z), heading +Z; Climb gets there from
    // wherever the player is, up or down. Nothing checks the terrain: in testwelt the player starts at y ≈ 272 over a column
    // whose topmost solid block is y = 241, so noclip starts the route below the surface. Setup waits ModeTimeout seconds for
    // the server to grant fly mode. The first Discard seconds of every segment (after a move or an arm switch) are not counted.
    public const double ModeTimeout = 10, ClimbSpeed = 40, Altitude = 200, Heading = 0, Discard = 1;

    public static BenchSegment[] Expand(BenchConfig config)
    {
        if (!NotNull(config) || !Assert(config.Arms.Count is > 0 and <= BenchConfig.MaxArms))
            throw new InvalidDataException("bench.json: no arms");
        var laps = config.WarmupLaps + config.Laps;
        if (Prelude + (long)laps * SegmentsPerLap > MaxSegments)
            throw new InvalidDataException(
                $"bench.json: warmupLaps + laps is {laps}, more than {MaxSegments} segments allow");
        List<BenchSegment> list =
        [
            new(BenchKind.Setup, -1, 0, false, false, ModeTimeout),
            new(BenchKind.Climb, -1, 0, false, false, 0),
            new(BenchKind.Settle, -1, 0, false, false, config.Settle)
        ];
        for (var lap = 0; lap < Math.Min(laps, BenchConfig.MaxLaps + BenchConfig.MaxArms); lap++)
        {
            var warmup = lap < config.WarmupLaps;
            var number = warmup ? lap : lap - config.WarmupLaps;
            var arm = warmup ? lap % config.Arms.Count : ArmOf(number, config.Arms.Count);
            AddLap(list, config, number, arm, warmup);
        }

        return Assert(list.Count <= MaxSegments)
            ? [.. list]
            : throw new InvalidDataException("bench.json: too many segments");
    }

    // Mirrored blocks: lap order A B B A A B B A for two arms, A B C C B A for three
    public static int ArmOf(int lap, int arms)
    {
        if (!Assert(lap >= 0) || !Assert(arms is > 0 and <= BenchConfig.MaxArms)) return 0;
        var place = lap % arms;
        return lap / arms % 2 == 0 ? place : arms - 1 - place;
    }

    private static void AddLap(List<BenchSegment> list, BenchConfig config, int lap, int arm, bool warmup)
    {
        if (!Index(arm, config.Arms.Count) || !Assert(lap >= 0)) return;
        // Always there, even with no settle time (it then ends after its first frame): the driver switches the lap's arm when it
        // enters this segment, so a lap without one would silently run under the previous lap's switches.
        list.Add(new BenchSegment(BenchKind.LapSettle, lap, arm, warmup, false, config.LapSettle));
        if (config.Still > 0) list.Add(new BenchSegment(BenchKind.Still, lap, arm, warmup, false, config.Still));
        if (config.Rotate > 0) list.Add(new BenchSegment(BenchKind.Rotate, lap, arm, warmup, false, config.Rotate));
        if (config.Leg <= 0) return;
        list.Add(new BenchSegment(BenchKind.Out, lap, arm, warmup, false, config.Leg));
        list.Add(new BenchSegment(BenchKind.Turn, lap, arm, warmup, true, config.Turn));
        list.Add(new BenchSegment(BenchKind.Back, lap, arm, warmup, false, config.Leg));
        list.Add(new BenchSegment(BenchKind.Turn, lap, arm, warmup, false, config.Turn));
    }

    // t is clamped to the segment, so a frame that overshoots its end still lands exactly on the next segment's start.
    // leg is the route's length in blocks: Out and Back cover it, the far turn stands at its end.
    public static BenchPose Pose(BenchSegment segment, double t, double heading, double leg)
    {
        if (!Finite(t) || !Finite(heading) || !Assert(leg >= 0)) return new BenchPose(0, 0, 0);
        var s = segment.Seconds > 0 ? Math.Clamp(t / segment.Seconds, 0, 1) : 1;
        var (sin, cos) = Math.SinCos(heading);
        return segment.Kind switch
        {
            BenchKind.Rotate => new BenchPose(0, 0, heading + 2 * Math.PI * s),
            BenchKind.Out => new BenchPose(leg * s * sin, leg * s * cos, heading),
            BenchKind.Turn when segment.Far => new BenchPose(leg * sin, leg * cos, heading + Math.PI * s),
            BenchKind.Turn => new BenchPose(0, 0, heading + Math.PI + Math.PI * s),
            BenchKind.Back => new BenchPose(leg * (1 - s) * sin, leg * (1 - s) * cos, heading + Math.PI),
            _ => new BenchPose(0, 0, heading)
        };
    }
}
