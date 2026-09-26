using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Komet.Test.Bench;

// bench.json fails loudly at load: an unknown key, a knob the table does not know, a value out of range or a relative path is an
// error, never a silently ignored wish that shows up as a wrong benchmark an hour later
public sealed class BenchConfigTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "komet-bench-test");
    private static readonly string[] Profiles = ["smoke", "aa", "full", "tess", "gcreg", "hud"], OnOff = ["on", "off"];

    internal static string Json(string extra = "")
    {
        var output = Path.Combine(Root, "result.json").Replace("\\", "\\\\", StringComparison.Ordinal);
        var mods = Path.Combine(Root, "Mods").Replace("\\", "\\\\", StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture,
            $$"""{ "output": "{{output}}", "modDir": "{{mods}}", "world": { "savegameId": "75695bba" }{{(extra.Length > 0 ? ", " + extra : "")}} }""");
    }

    private static string Error(string extra)
    {
        return Assert.Throws<InvalidDataException>(() => BenchConfig.Parse(Json(extra)))!.Message;
    }

    private static string BenchJson()
    {
        return Path.Combine(typeof(BenchConfigTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(attribute => attribute.Key == "KometDir").Value!, "..", "scripts", "bench.json");
    }

    [TearDown]
    public void Restore()
    {
        FrustumSweep.Enabled = true;
        ChunkBudget.CapMillis = ChunkBudget.DefaultCapMillis;
    }

    [Test]
    public void AMinimalConfigTakesTheDefaults()
    {
        var config = BenchConfig.Parse(Json());
        Assert.Multiple(() =>
        {
            Assert.That((config.Laps, config.WarmupLaps), Is.EqualTo((8, 1)), "one warm-up lap per arm");
            Assert.That(config.Arms, Has.Count.EqualTo(1));
            Assert.That((config.Settle, config.LapSettle), Is.EqualTo((150.0, 10.0)));
            Assert.That((config.Still, config.Rotate, config.Leg, config.Turn, config.Speed),
                Is.EqualTo((10.0, 20.0, 40.0, 2.0, 11.0)));
            Assert.That(config.Frames, Is.EqualTo(Path.Combine(Root, "frames.csv")));
            Assert.That(config.Status, Is.EqualTo(Path.Combine(Root, "result.json") + ".status"));
            Assert.That(config.SavegameId, Is.EqualTo("75695bba"));
        });
    }

    [Test]
    public void ArmsSetKnobsByTheirTableName()
    {
        var config = BenchConfig.Parse(Json("""
                                            "laps": 4, "revision": "3cb72fc-dirty", "env": { "DOTNET_gcServer": "1" },
                                            "arms": [ { "name": "on" }, { "name": "off", "engine": true, "set": { "FrustumSweep": false, "UploadCap": 6 } } ]
                                            """));
        var off = config.Arms[1];
        Assert.Multiple(() =>
        {
            Assert.That(config.Arms.Select(arm => arm.Name), Is.EqualTo(OnOff));
            Assert.That((config.Arms[0].Engine, off.Engine), Is.EqualTo((false, true)));
            Assert.That(config.WarmupLaps, Is.EqualTo(2));
            Assert.That(off.Settings, Does.Contain(new BenchSetting(Knobs.Find("FrustumSweep"), 0)));
            Assert.That(off.Settings, Does.Contain(new BenchSetting(Knobs.Find("UploadCap"), 6)));
            Assert.That(config.Raw.GetProperty("revision").GetString(), Is.EqualTo("3cb72fc-dirty"),
                "echoed into the result");
        });
    }

    // An engine arm starts from every knob's engine value, the others from the player's; the arm's own settings win either way
    [Test]
    public void AnEngineArmStartsFromTheEngineValues()
    {
        var config = BenchConfig.Parse(Json("""
                                            "laps": 2, "arms": [ { "name": "komet", "set": { "UploadCap": 6 } }, { "name": "engine", "engine": true, "set": { "FrustumSweep": true } } ]
                                            """));
        var baseline = Knobs.Snapshot();
        var values = BenchDriver.ArmValues(baseline, config.Arms);
        var expected = Knobs.EngineValues();
        expected[Knobs.Find("FrustumSweep")] = 1;
        Assert.Multiple(() =>
        {
            Assert.That(values[1], Is.EqualTo(expected));
            Assert.That(values[0][Knobs.Find("UploadCap")], Is.EqualTo(6));
            Assert.That(values[0].Where((_, k) => k != Knobs.Find("UploadCap")),
                Is.EqualTo(baseline.Where((_, k) => k != Knobs.Find("UploadCap"))));
        });
    }

    [Test]
    public void CommentsAndUnderscoreKeysAreIgnored()
    {
        var config = BenchConfig.Parse(Json("""
                                            // the smoke profile
                                            "_comment": "anything", "laps": 2, "sandbox": { "window": [1280, 720] }
                                            """));
        Assert.That(config.Laps, Is.EqualTo(2));
    }

    [TestCase("\"lapps\": 3", "unknown key lapps")]
    [TestCase("\"settle\": { \"quiet\": 3 }", "unknown key settle.quiet")]
    [TestCase("\"route\": { \"speed\": 100 }", "route.speed must be between")]
    [TestCase("\"laps\": 1.5", "laps must be a whole number")]
    [TestCase("\"laps\": 3, \"arms\": [ { \"name\": \"A\" }, { \"name\": \"B\" } ]", "a multiple of the 2 arms")]
    [TestCase("\"env\": { \"DOTNET_gcServer\": 1 }", "env.DOTNET_gcServer must be a string")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"Nonexistent\": true } } ]", "arms[0].set.Nonexistent")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"UploadCap\": 99 } } ]", "inside the knob's range")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"set\": { \"UploadCap\": 2, \"UploadCap\": 3 } } ]", "named once")]
    [TestCase("\"arms\": [ { \"name\": \"A\", \"engine\": 1 } ]", "arms[0].engine must be true or false")]
    [TestCase("\"arms\": [ { \"name\": \"A\" }, { \"name\": \"A\" } ]", "unique names")]
    [TestCase(
        "\"arms\": [ {\"name\":\"1\"},{\"name\":\"2\"},{\"name\":\"3\"},{\"name\":\"4\"},{\"name\":\"5\"},{\"name\":\"6\"},{\"name\":\"7\"},{\"name\":\"8\"},{\"name\":\"9\"} ]",
        "a list of 1 to")]
    public void ABadValueNamesItsKey(string extra, string expected)
    {
        Assert.That(Error(extra), Does.Contain(expected));
    }

    [TestCase("result.json")]
    [TestCase("/tmp/a\\u0000b.json")]
    public void TheOutputPathMustBeAbsolute(string output)
    {
        var json = Json().Replace(Path.Combine(Root, "result.json"), output, StringComparison.Ordinal);
        Assert.That(() => BenchConfig.Parse(json),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("output must be an absolute path"));
    }

    [Test]
    public void TheSavegameIdIsRequired()
    {
        var json = Json().Replace("\"world\": { \"savegameId\": \"75695bba\" }", "\"world\": { }",
            StringComparison.Ordinal);
        Assert.That(() => BenchConfig.Parse(json),
            Throws.TypeOf<InvalidDataException>().With.Message.Contains("world.savegameId"));
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
            foreach (var (key, value) in profile!.AsObject()) flat[key] = value!.DeepClone();
            flat["name"] = name;
            var config = BenchConfig.Parse(flat.ToJsonString());
            var knobs = profile["arms"]?.AsArray()
                .SelectMany(arm => arm!["set"]?.AsObject().Select(entry => entry.Key) ?? []) ?? [];
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(Knobs.Find), Is.All.GreaterThanOrEqualTo(0), name);
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
