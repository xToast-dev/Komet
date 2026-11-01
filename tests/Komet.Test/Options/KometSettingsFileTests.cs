using Komet.Chunks;
using Komet.Core;
using Komet.Hud;
using Komet.Tessellation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Options;

// The knobs' part of komet.json: saved names, values out of range
public sealed class KometSettingsFileTests
{
    private int[] _defaults = [];

    [SetUp]
    public void Save()
    {
        _defaults = Knobs.Snapshot();
    }

    [TearDown]
    public void Restore()
    {
        _ = Knobs.Apply(_defaults);
    }

    // NearFirst is the name a knob had before a rename; the file keeps it
    [Test]
    public void KnobsKeepTheirKeysInTheFile()
    {
        var (schedule, budget) = (Knobs.Find("TessSchedule"), Knobs.Find("ChunkLoadBudget"));
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "NearFirst": false, "PoolFragments": 0, "ChunkLoadBudget": 7, "UploadCap": 99, "CullGrid": true }""")
            !;
        var logger = new CapturingLogger();
        settings.Knobs.Apply(logger, HudSettings.FileName);
        var file = JObject.Parse(JsonConvert.SerializeObject(settings));
        Assert.Multiple(() =>
        {
            Assert.That((settings.Knobs.Knob(schedule), settings.Knobs.Knob(budget)), Is.EqualTo((0, 7)));
            Assert.That((TessSchedule.Enabled, ChunkLoadBudget.Millis), Is.EqualTo((false, 7)), "applied");
            Assert.That(settings.Knobs.Knob(Knobs.Find("UploadCap")), Is.EqualTo(_defaults[Knobs.Find("UploadCap")]));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap"));
            Assert.That(file["NearFirst"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(file["PoolFragments"], Is.Null, "gone with its knob");
            Assert.That((int?)file["ChunkLoadBudget"], Is.EqualTo(7));
            Assert.That(file["CullGrid"], Is.Null, "gone with its knob");
        });
    }

    // Json.NET reads the first two as BigInteger; they fall back like any value out of range instead of throwing out of Load
    [Test]
    public void AnIntegerBeyondLongIsOutOfRange()
    {
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "UploadCap": 100000000000000000000, "NearFirst": -100000000000000000000, "ChunkLoadBudget": 4294967296 }""")
            !;
        var (logger, fresh) = (new CapturingLogger(), new HudSettings());
        int[] knobs = [Knobs.Find("UploadCap"), Knobs.Find("TessSchedule"), Knobs.Find("ChunkLoadBudget")];
        settings.Knobs.Apply(logger, HudSettings.FileName);
        Assert.Multiple(() =>
        {
            Assert.That(knobs.Select(settings.Knobs.Knob), Is.EqualTo(knobs.Select(fresh.Knobs.Knob)), "the defaults apply");
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap").And.Some
                .Contains("NearFirst")
                .And.Some.Contains("ChunkLoadBudget"));
        });
    }
}
