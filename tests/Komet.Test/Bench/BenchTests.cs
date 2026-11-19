using System.Text.Json;
using System.Text.Json.Nodes;
using Komet.Bench;
using Komet.Chunks;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Hud;
using Komet.Rendering;
using Newtonsoft.Json;
using Vintagestory.Server.Systems;
using static Komet.Testing.Profiles;

namespace Komet.Test.Bench;

// bench.json fails loudly at load: an unknown key, a knob the table does not know, a value out of range or a relative path is an
// error, never a silently ignored wish that shows up as a wrong benchmark an hour later
public sealed class BenchConfigTests
{
    // Two arms, the second without FrustumSweep: the report and scenario tests' A/B run
    internal const string TwoArms =
        "\"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\", \"set\": { \"FrustumSweep\": false } } ]";

    private static readonly string Root = Path.Combine(Path.GetTempPath(), "komet-bench-test");

    private static readonly string[] Profiles =
        [
            "quick", "load", "debug", "smoke", "aa", "full", "tess", "gcreg", "culling", "culling1440", "hud", "vulkan", "map", "shadertest"
        ];

    // What komet.json holds, as the HUD's settings save it
    private static readonly string[] SettingsKeys =
        [.. JsonNode.Parse(JsonConvert.SerializeObject(new HudSettings()))!.AsObject().Select(entry => entry.Key)];

    internal static string Json(string extra = "", string? output = null,
        string world = "{ \"savegameId\": \"75695bba\" }")
    {
        output ??= Path.Combine(Root, "result.json").Replace("\\", "\\\\", StringComparison.Ordinal);
        var mods = Path.Combine(Root, "Mods").Replace("\\", "\\\\", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{ "output": "{{output}}", "modDir": "{{mods}}", "world": {{world}}{{(extra.Length > 0 ? ", " + extra : "")}} }""");
    }

    private static string BenchJson() => Path.Combine(Paths.Repo, "scripts", "bench.json");

    [TearDown]
    public void Restore()
    {
        FrustumSweep.Enabled = true;
        ChunkBudget.CapMillis = ChunkBudget.DefaultCapMillis;
    }

    [TestCase("\"lapps\": 3", "unknown key lapps")]
    [TestCase("\"settle\": { \"quiet\": 3 }", "unknown key settle.quiet")]
    public void ABadValueNamesItsKey(string extra, string expected, string? output = null,
        string world = "{ \"savegameId\": \"75695bba\" }")
    {
        var error = Assert.Throws<InvalidDataException>(() => BenchConfig.Parse(Json(extra, output, world)))!.Message;
        Assert.That(error, Does.Contain(expected));
    }

    // Every profile scripts/bench.sh can pick, flattened the way the script does it, parses: each knob an arm names resolves through
    // Knobs.Find, so removing or renaming a feature fails here instead of at the start of a benchmark run
    [Test]
    public void EveryProfileInBenchJsonParsesAndNamesOnlyKnownKnobs()
    {
        var file = JsonNode.Parse(File.ReadAllText(BenchJson()),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        var profiles = file["profiles"]!.AsObject();
        Assert.That(profiles.Select(profile => profile.Key), Is.EquivalentTo(Profiles));
        foreach (var (name, profile) in profiles)
        {
            var flat = JsonNode.Parse(Json())!.AsObject();
            foreach (var (key, value) in profile!.AsObject())
            {
                flat[key] = value!.DeepClone();
            }

            flat["name"] = name;
            var config = BenchConfig.Parse(flat.ToJsonString());
            var knobs = profile["arms"]?.AsArray()
                .SelectMany(arm => arm!["set"]?.AsObject().Select(entry => entry.Key) ?? []) ?? [];
            var hud = profile["sandbox"]?["hud"]?.AsObject().Select(entry => entry.Key) ?? [];
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(Knobs.Find), Is.All.GreaterThanOrEqualTo(0), name);
                Assert.That(hud, Is.SubsetOf(SettingsKeys), name + ": sandbox.hud names komet.json keys");
                Assert.That(() => BenchScenario.Expand(config), Throws.Nothing, name);
            });
        }
    }

    // ShaderUseCache.Enabled = true empties its cache even when it already was true: an arm switch writes only differing values
    [Test]
    public void ApplyWritesOnlyWhatDiffers()
    {
        var baseline = Knobs.Snapshot();
        Assert.That(Knobs.Apply(baseline), Is.Zero);
        var changed = (int[])baseline.Clone();
        changed[Knobs.Find("FrustumSweep")] = 0;
        changed[Knobs.Find("UploadCap")] = 5;
        Assert.Multiple(() =>
        {
            Assert.That(Knobs.Apply(changed), Is.EqualTo(2));
            Assert.That((FrustumSweep.Enabled, ChunkBudget.CapMillis), Is.EqualTo((false, 5)));
            Assert.That(Knobs.Apply(baseline), Is.EqualTo(2));
            Assert.That(FrustumSweep.Enabled, Is.True);
        });
    }
}

// FrameClock hands the recorder frame N as N+1 starts, with N's own dt, GC pause and collections, and the recorder books it to the
// stamp the driver left during N
public sealed class BenchRecorderTests
{
    private const int AllocationWindows = 5;

    private static BenchRecorder Started(int capacity = 1000, int segments = 4, int spikes = 3)
    {
        var recorder = new BenchRecorder(capacity, segments, spikes);
        recorder.Start();
        return recorder;
    }

    private static void Feed(BenchRecorder recorder, int segment, float ms, BenchFrameTags tags = BenchFrameTags.None,
        ProfileEntryRange? root = null)
    {
        recorder.Stamp(segment, tags, 0, 0, 0);
        recorder.Frame(new FrameRecord(0, ms, 0, 0, 0, 0, root), default);
    }

    // The sink runs inside window_RenderFrame's prefix every frame: nothing allocates, spikes and their marks included, as long as
    // the spike frames collected nothing. Every window pushes new spikes, and one of them has to come out at zero: once a test
    // has patched anything, Harmony hooks the JIT, and a compile on this thread (the window's own on-stack replacement, a
    // callee's tier-up) then allocates once. A frame that allocates fails every window.
    [Test]
    public void RecordingAllocatesNothingPerFrame()
    {
        var recorder = Started(30_000, 2, 10);
        ProfileEntryRange[] trees = [Tree(), Tree()]; // FrameProfilerUtil.Begin builds a new root every frame
        for (var i = 0; i < 1000; i++)
        {
            Feed(recorder, i < 500 ? 0 : 1, 10, root: trees[i % 2]);
        }

        var allocated = new long[AllocationWindows];
        for (var window = 0; window < allocated.Length; window++)
        {
            allocated[window] = Window(recorder, trees, 20 + 10 * window);
        }

        Assert.That(allocated.Min(), Is.Zero, $"bytes per window: {string.Join(", ", allocated)}");
    }

    private static long Window(BenchRecorder recorder, ProfileEntryRange[] trees, int ms)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 5000; i++)
        {
            Feed(recorder, 1, ms + i % 7, root: trees[i % 2]);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

// The run is a flat, bounded, deterministic list; the arms alternate in mirrored blocks; and the kinematic route moves the way the
// engine's own movement code points and never steps further than the server's position check accepts
public sealed class BenchScenarioTests
{
    [Test]
    public void ALapEndsWhereItStarted()
    {
        const double heading = 0.7, leg = 440;

        static BenchSegment Of(BenchKind kind, bool far = false) =>
            new(kind, 0, 0, false, far, kind == BenchKind.Turn ? 2 : 40);

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
    public void TheWorstFrameStaysUnderTheServersStepLimit(double speed) =>
        Assert.Multiple(() =>
        {
            Assert.That(Accepted(speed * BenchDriver.MaxStep), Is.True);
            Assert.That(Accepted(128.5), Is.False, "the check the test relies on is the engine's");
        });

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
