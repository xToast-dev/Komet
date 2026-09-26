using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Hud;

// komet-hud.json and the corner row of the settings window. Clicking the corner that is already chosen while panels are pinned only
// clears the pins; Set() saw the same corner and raised nothing, so the window kept showing "custom" and the cleared pins were never saved.
public sealed class HudCornerTests
{
    [Test]
    public void ClearingThePinsAtTheSameCornerIsAChange()
    {
        var (settings, changes) = Counted();
        settings.Pinned[0] = [16, 24];
        settings.SetCorner(settings.Corner);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Pinned, Is.Empty);
            Assert.That(changes(), Is.EqualTo(1), "the window redraws and the settings are saved");
        });
    }

    [Test]
    public void TheSameCornerWithoutPinsChangesNothing()
    {
        var (settings, changes) = Counted();
        settings.SetCorner(settings.Corner);
        Assert.That(changes(), Is.Zero);
    }

    [Test]
    public void AnotherCornerIsOneChange()
    {
        var (settings, changes) = Counted();
        settings.Pinned[3] = [8, 8];
        var other = settings.Corner == HudCorner.BottomRight ? HudCorner.TopLeft : HudCorner.BottomRight;
        settings.SetCorner(other);
        Assert.Multiple(() =>
        {
            Assert.That(settings.Corner, Is.EqualTo(other));
            Assert.That(settings.Pinned, Is.Empty);
            Assert.That(changes(), Is.EqualTo(1));
        });
    }

    // Each knob is a top-level key, a bool for a switch, under the name it had before a rename; a key of a knob that is gone is
    // dropped, and a value out of range falls back to the default with one warning
    [Test]
    public void KnobsKeepTheirKeysInTheFile()
    {
        var (schedule, pool, budget) =
            (Knobs.Find("TessSchedule"), Knobs.Find("MeshPool"), Knobs.Find("EntityTessBudget"));
        var defaults = Knobs.Snapshot();
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "NearFirst": false, "PoolFragments": 0, "EntityTessBudget": 7, "UploadCap": 99, "CullGrid": true }""")
            !;
        var logger = new CapturingLogger();
        try
        {
            settings.ApplyKnobs(logger);
            var file = JObject.Parse(JsonConvert.SerializeObject(settings));
            Assert.Multiple(() =>
            {
                Assert.That((settings.Knob(schedule), settings.Knob(pool), settings.Knob(budget)),
                    Is.EqualTo((0, 0, 7)));
                Assert.That((TessSchedule.Enabled, MeshPool.Enabled, EntityTessBudget.Millis),
                    Is.EqualTo((false, false, 7)), "applied");
                Assert.That(settings.Knob(Knobs.Find("UploadCap")), Is.EqualTo(defaults[Knobs.Find("UploadCap")]));
                Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap"));
                Assert.That(file["NearFirst"]?.Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That(file["PoolFragments"]?.Type, Is.EqualTo(JTokenType.Boolean));
                Assert.That((int?)file["EntityTessBudget"], Is.EqualTo(7));
                Assert.That(file["CullGrid"], Is.Null, "gone with its knob");
            });
        }
        finally
        {
            _ = Knobs.Apply(defaults);
        }
    }

    // Json.NET reads the first two as BigInteger; they fall back like any value out of range instead of throwing out of Load
    [Test]
    public void AnIntegerBeyondLongIsOutOfRange()
    {
        var defaults = Knobs.Snapshot();
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "UploadCap": 100000000000000000000, "NearFirst": -100000000000000000000, "EntityTessBudget": 4294967296 }""")
            !;
        var (logger, fresh) = (new CapturingLogger(), new HudSettings());
        int[] knobs = [Knobs.Find("UploadCap"), Knobs.Find("TessSchedule"), Knobs.Find("EntityTessBudget")];
        try
        {
            settings.ApplyKnobs(logger);
            Assert.Multiple(() =>
            {
                Assert.That(knobs.Select(settings.Knob), Is.EqualTo(knobs.Select(fresh.Knob)), "the defaults apply");
                Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap").And.Some
                    .Contains("NearFirst")
                    .And.Some.Contains("EntityTessBudget"));
            });
        }
        finally
        {
            _ = Knobs.Apply(defaults);
        }
    }

    private static (HudSettings Settings, Func<int> Changes) Counted()
    {
        var settings = new HudSettings();
        var count = 0;
        settings.Changed += () => count++;
        return (settings, () => count);
    }
}
