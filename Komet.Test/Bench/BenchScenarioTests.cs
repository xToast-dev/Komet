using System.Globalization;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.Common;
using Vintagestory.Server.Systems;

namespace Komet.Test.Bench;

// The run is a flat, bounded, deterministic list; the arms alternate in mirrored blocks; and the kinematic route moves the way the
// engine's own movement code points and never steps further than the server's position check accepts
public sealed class BenchScenarioTests
{
    private const string TwoArms =
        "\"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\", \"set\": { \"FrustumSweep\": false } } ]";

    private static readonly BenchKind[] Prelude = [BenchKind.Setup, BenchKind.Climb, BenchKind.Settle];

    private static readonly BenchKind[] Lap =
    [
        BenchKind.LapSettle, BenchKind.Still, BenchKind.Rotate, BenchKind.Out, BenchKind.Turn, BenchKind.Back,
        BenchKind.Turn
    ];

    private static readonly BenchKind[] Unmeasured =
        [BenchKind.Setup, BenchKind.Climb, BenchKind.Settle, BenchKind.LapSettle];

    private static readonly BenchKind[] LapWithoutRotate =
        [BenchKind.LapSettle, BenchKind.Still, BenchKind.Out, BenchKind.Turn, BenchKind.Back, BenchKind.Turn];

    private static readonly BenchKind[] LapWithoutRoute = [BenchKind.LapSettle, BenchKind.Rotate];
    private static readonly int[] Abba = [0, 1, 1, 0, 0, 1, 1, 0], Warmups = [0, 1], Abccba = [0, 1, 2, 2, 1, 0];

    private static BenchSegment[] Expand(string extra)
    {
        return BenchScenario.Expand(BenchConfig.Parse(BenchConfigTests.Json(extra)));
    }

    private static int[] LapArms(BenchSegment[] segments, bool warmup)
    {
        return [.. segments.Where(s => s.Kind == BenchKind.LapSettle && s.Warmup == warmup).Select(s => s.Arm)];
    }

    [Test]
    public void SetupClimbAndSettleComeBeforeTheLaps()
    {
        var segments = Expand(TwoArms);
        Assert.Multiple(() =>
        {
            Assert.That(segments.Take(3).Select(s => s.Kind), Is.EqualTo(Prelude));
            Assert.That(segments, Has.Length.EqualTo(3 + (2 + 8) * 7),
                "two warm-up and eight measured laps of seven segments");
            Assert.That(segments.Skip(3).Take(7).Select(s => s.Kind), Is.EqualTo(Lap));
            Assert.That(segments.Where(s => s.Far).Select(s => (s.Kind, s.Name)).ToArray(),
                Has.Length.EqualTo(10).And.All.EqualTo((BenchKind.Turn, "turn-far")));
            Assert.That(segments[2].Seconds, Is.EqualTo(150), "the settle is a fixed time");
            Assert.That(segments.Where(s => !s.Measured).Select(s => s.Kind).Distinct(),
                Is.EquivalentTo(Unmeasured));
        });
    }

    [Test]
    public void MeasuredLapsRunInMirroredBlocks()
    {
        var segments = Expand(TwoArms);
        Assert.Multiple(() =>
        {
            Assert.That(LapArms(segments, false), Is.EqualTo(Abba), "A B B A A B B A");
            Assert.That(LapArms(segments, true), Is.EqualTo(Warmups), "every arm warms up once");
            Assert.That(
                segments.Where(s => s.Lap >= 0).GroupBy(s => (s.Lap, s.Warmup))
                    .All(lap => lap.Select(s => s.Arm).Distinct().Count() == 1),
                "a lap never switches arms");
        });
    }

    [Test]
    public void ThreeArmsMirrorToo()
    {
        var segments =
            Expand("\"laps\": 6, \"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\" }, { \"name\": \"C\" } ]");
        Assert.That(LapArms(segments, false), Is.EqualTo(Abccba));
    }

    // The driver switches a lap's arm when it enters the lap's LapSettle, so every lap needs one even without settle time: a lap
    // settle of 0 once dropped the segment, and with it every switch, benchmarking the baseline in every arm
    [Test]
    public void EveryLapStartsWhereItsArmIsSwitchedEvenWithoutSettleTime()
    {
        var segments = Expand("\"settle\": { \"lapSeconds\": 0 }, " + TwoArms);
        var laps = segments.Where(s => s.Lap >= 0).GroupBy(s => (s.Lap, s.Warmup)).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(laps, Has.Length.EqualTo(2 + 8));
            Assert.That(laps.All(lap => lap.First().Kind == BenchKind.LapSettle),
                "each lap opens with its switch point");
            Assert.That(LapArms(segments, false), Is.EqualTo(Abba));
            Assert.That(segments.Where(s => s.Kind == BenchKind.LapSettle).All(s => s.Seconds == 0),
                "no settle time: the switch frame only");
        });
    }

    // A lap part of length 0 is left out; leg 0 drops the whole route (both legs and both turns), the lap settle stays
    [Test]
    public void ZeroLengthLapPartsAreLeftOut()
    {
        var noRotate = Expand("\"lap\": { \"rotate\": 0 }, " + TwoArms);
        var noRoute = Expand("\"lap\": { \"still\": 0, \"leg\": 0 }, " + TwoArms);
        Assert.Multiple(() =>
        {
            Assert.That(noRotate, Has.Length.EqualTo(3 + (2 + 8) * 6));
            Assert.That(noRotate.Skip(3).Take(6).Select(s => s.Kind), Is.EqualTo(LapWithoutRotate));
            Assert.That(noRoute, Has.Length.EqualTo(3 + (2 + 8) * 2));
            Assert.That(noRoute.Skip(3).Take(2).Select(s => s.Kind), Is.EqualTo(LapWithoutRoute));
            Assert.That(noRoute.Where(s => s.Lap >= 0).Select(s => s.Kind).Distinct(),
                Is.EquivalentTo(LapWithoutRoute));
        });
    }

    [Test]
    public void TheSegmentCapIsEnforced()
    {
        Assert.That(() => Expand("\"laps\": 40, \"warmupLaps\": 0"),
            Throws.TypeOf<InvalidDataException>().With.Message
                .Contains(BenchScenario.MaxSegments.ToString(CultureInfo.InvariantCulture)));
    }

    // EntityControls.CalcMovementVectors is what the engine walks for Forward at a level pitch of π; the out leg has to point the same way
    [TestCase(0.0)]
    [TestCase(1.0)]
    [TestCase(3.1416)]
    [TestCase(5.5)]
    public void TheOutLegFollowsTheEnginesForwardVector(double yaw)
    {
        var controls = new EntityControls { Forward = true };
        var pos = new EntityPos(0, 0, 0) { Yaw = (float)yaw, Pitch = MathF.PI };
        controls.CalcMovementVectors(pos, 1f);
        var fly = controls.FlyVector;
        var segment = new BenchSegment(BenchKind.Out, 0, 0, false, false, 40);
        var pose = BenchScenario.Pose(segment, 20, yaw, 440);
        var length = Math.Sqrt(fly.X * fly.X + fly.Z * fly.Z);
        var distance = Math.Sqrt(pose.X * pose.X + pose.Z * pose.Z);
        Assert.Multiple(() =>
        {
            Assert.That(fly.Y / length, Is.Zero.Within(1e-6), "pitch π is level, to float precision");
            Assert.That(pose.X / distance, Is.EqualTo(fly.X / length).Within(1e-4));
            Assert.That(pose.Z / distance, Is.EqualTo(fly.Z / length).Within(1e-4));
            Assert.That(distance, Is.EqualTo(220).Within(1e-9), "half the leg after half its time");
            Assert.That(pose.Yaw, Is.EqualTo(yaw), "the camera looks where it flies");
        });
    }

    [Test]
    public void ALapEndsWhereItStarted()
    {
        const double heading = 0.7, leg = 440;

        static BenchSegment Of(BenchKind kind, bool far = false)
        {
            return new BenchSegment(kind, 0, 0, false, far, kind == BenchKind.Turn ? 2 : 40);
        }

        var outEnd = BenchScenario.Pose(Of(BenchKind.Out), 40, heading, leg);
        var farTurn = BenchScenario.Pose(Of(BenchKind.Turn, true), 2, heading, leg);
        var backEnd = BenchScenario.Pose(Of(BenchKind.Back), 40, heading, leg);
        var homeTurn = BenchScenario.Pose(Of(BenchKind.Turn), 2, heading, leg);
        var rotated = BenchScenario.Pose(Of(BenchKind.Rotate), 40, heading, leg);
        Assert.Multiple(() =>
        {
            Assert.That((farTurn.X, farTurn.Z), Is.EqualTo((outEnd.X, outEnd.Z)),
                "the far turn stands where the out leg ended");
            Assert.That(farTurn.Yaw, Is.EqualTo(heading + Math.PI).Within(1e-12), "and faces home");
            Assert.That(Math.Abs(backEnd.X) + Math.Abs(backEnd.Z), Is.Zero.Within(1e-9), "the back leg ends at home");
            Assert.That(Math.Cos(homeTurn.Yaw), Is.EqualTo(Math.Cos(heading)).Within(1e-12), "facing out again");
            Assert.That(Math.Sin(rotated.Yaw), Is.EqualTo(Math.Sin(heading)).Within(1e-12), "a full turn");
            Assert.That(BenchScenario.Pose(Of(BenchKind.Out), 400, heading, leg), Is.EqualTo(outEnd),
                "an overshooting frame stops at the end");
        });
    }

    // One frame advances the scenario by at most BenchDriver.MaxStep; at the highest speeds the config allows, the step one position
    // packet carries still passes EntityPosExtensions.SetFromPacket, which rejects more than 128 blocks per axis
    [TestCase(BenchConfig.MaxSpeed)]
    [TestCase(BenchScenario.ClimbSpeed)]
    public void TheWorstFrameStaysUnderTheServersStepLimit(double speed)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Accepted(speed * BenchDriver.MaxStep), Is.True);
            Assert.That(Accepted(128.5), Is.False, "the check the test relies on is the engine's");
        });
    }

    private static bool Accepted(double step)
    {
        var pos = new EntityPos(1000, 150, 1000);
        var packet = new Packet_EntityPosition
        {
            X = CollectibleNet.SerializeDoublePrecise(1000 + step), Y = CollectibleNet.SerializeDoublePrecise(150),
            Z = CollectibleNet.SerializeDoublePrecise(1000 + step), MotionX = CollectibleNet.SerializeDoublePrecise(0),
            MotionY = CollectibleNet.SerializeDoublePrecise(0), MotionZ = CollectibleNet.SerializeDoublePrecise(0)
        };
        return pos.SetFromPacket(packet, new EntityAgent());
    }
}
