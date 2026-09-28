using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Hud;

// komet-hud.json and the corner row of the settings window. Clicking the corner that is already chosen while panels are pinned only
// clears the pins; Set() saw the same corner and raised nothing, so the window kept showing "custom" and the cleared pins were never saved.
public sealed class HudCornerTests
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
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "NearFirst": false, "PoolFragments": 0, "EntityTessBudget": 7, "UploadCap": 99, "CullGrid": true }""")
            !;
        var logger = new CapturingLogger();
        settings.ApplyKnobs(logger);
        var file = JObject.Parse(JsonConvert.SerializeObject(settings));
        Assert.Multiple(() =>
        {
            Assert.That((settings.Knob(schedule), settings.Knob(pool), settings.Knob(budget)), Is.EqualTo((0, 0, 7)));
            Assert.That((TessSchedule.Enabled, MeshPool.Enabled, EntityTessBudget.Millis),
                Is.EqualTo((false, false, 7)), "applied");
            Assert.That(settings.Knob(Knobs.Find("UploadCap")), Is.EqualTo(_defaults[Knobs.Find("UploadCap")]));
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap"));
            Assert.That(file["NearFirst"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That(file["PoolFragments"]?.Type, Is.EqualTo(JTokenType.Boolean));
            Assert.That((int?)file["EntityTessBudget"], Is.EqualTo(7));
            Assert.That(file["CullGrid"], Is.Null, "gone with its knob");
        });
    }

    // Json.NET reads the first two as BigInteger; they fall back like any value out of range instead of throwing out of Load
    [Test]
    public void AnIntegerBeyondLongIsOutOfRange()
    {
        var settings = JsonConvert.DeserializeObject<HudSettings>(
                """{ "UploadCap": 100000000000000000000, "NearFirst": -100000000000000000000, "EntityTessBudget": 4294967296 }""")
            !;
        var (logger, fresh) = (new CapturingLogger(), new HudSettings());
        int[] knobs = [Knobs.Find("UploadCap"), Knobs.Find("TessSchedule"), Knobs.Find("EntityTessBudget")];
        settings.ApplyKnobs(logger);
        Assert.Multiple(() =>
        {
            Assert.That(knobs.Select(settings.Knob), Is.EqualTo(knobs.Select(fresh.Knob)), "the defaults apply");
            Assert.That(logger.Lines, Has.Count.EqualTo(1).And.Some.Contains("UploadCap").And.Some
                .Contains("NearFirst")
                .And.Some.Contains("EntityTessBudget"));
        });
    }

    private static (HudSettings Settings, Func<int> Changes) Counted()
    {
        var settings = new HudSettings();
        var count = 0;
        settings.Changed += () => count++;
        return (settings, () => count);
    }
}

// Where the HUD puts its panels: down the columns from its corner, and an unpinned panel whose column slot is taken at the nearest
// free spot, or the one that covers the least; and the queue that hands out the pending panels to draw
public sealed class PanelPlacerTests
{
    private const double Screen = 1000, Margin = 10, Gap = 5, Slack = 6;
    private static readonly int[] RoundRobin = [1, 3, 5, 1];

    [Test]
    public void OverlapIsTheSharedArea()
    {
        var a = new PanelRect(0, 0, 100, 50);
        Assert.Multiple(() =>
        {
            Assert.That(a.Overlap(new PanelRect(50, 25, 100, 100)), Is.EqualTo(50 * 25));
            Assert.That(a.Overlap(new PanelRect(100, 0, 10, 10)), Is.Zero, "touching edges do not overlap");
            Assert.That(a.Overlap(new PanelRect(200, 200, 10, 10)), Is.Zero);
        });
    }

    [Test]
    public void AFreeSlotIsKept()
    {
        var placer = new PanelPlacer(4);
        placer.Take(new PanelRect(500, 500, 100, 100));
        Assert.That(placer.Place(new PanelRect(Margin, Margin, 200, 300), Screen, Screen, Margin, Gap, Slack),
            Is.EqualTo((Margin, Margin, true)));
    }

    // The user's column had grown a pixel into a panel pinned a gap beside it, and the whole column moved away
    [Test]
    public void AnOverlapWithinTheSlackKeepsTheSlot()
    {
        var placer = new PanelPlacer(4);
        placer.Take(new PanelRect(586.5, 10, 730, 1080));
        var slot = new PanelRect(Margin, Margin, 577.5, 400);
        Assert.Multiple(() =>
        {
            Assert.That(slot.Depth(new PanelRect(586.5, 10, 730, 1080)), Is.EqualTo(1).Within(1e-9));
            Assert.That(placer.Place(slot, 2560, 1440, Margin, Gap, Slack), Is.EqualTo((Margin, Margin, true)));
            var (x, y, free) = placer.Place(slot with { Width = 600 }, 2560, 1440, Margin, Gap, Slack);
            Assert.That((x, y), Is.Not.EqualTo((Margin, Margin)),
                "reaching past the padding into the pinned panel's text");
            Assert.That(free, Is.True, "the screen has room elsewhere");
        });
    }

    [Test]
    public void APanelOnAPinnedOneMovesToTheNearestFreeSpot()
    {
        var placer = new PanelPlacer(4);
        var pinned = new PanelRect(0, 0, 100, 100);
        placer.Take(pinned);
        var slot = new PanelRect(Margin, Margin, 50, 50);
        var (x, y, _) = placer.Place(slot, Screen, Screen, Margin, Gap, Slack);
        var placed = slot with { X = x, Y = y };
        Assert.Multiple(() =>
        {
            Assert.That(placed.Overlap(pinned), Is.Zero);
            Assert.That(Math.Abs(x - slot.X) + Math.Abs(y - slot.Y), Is.EqualTo(pinned.Right + Gap - Margin),
                "beside or below the pinned panel, not further");
        });
    }

    [Test]
    public void WithNoRoomLeftItCoversTheLeast()
    {
        var placer = new PanelPlacer(4);
        placer.Take(new PanelRect(0, 0, 150, 100));
        var (x, y, free) = placer.Place(new PanelRect(0, 0, 100, 50), 200, 100, Margin, Gap, Slack);
        Assert.Multiple(() =>
        {
            Assert.That(free, Is.False, "squeezed in: the HUD draws it beneath the panel it covers");
            Assert.That(x, Is.EqualTo(200 - Margin - 100),
                "pushed against the far edge, where it covers 60 of its 100 columns");
            Assert.That(y, Is.InRange(0, 100 - 50));
        });
    }

    // Nowhere fits, so it stays in its column, at the top edge where the canvas would draw it anyway
    [Test]
    public void APanelTallerThanTheScreenKeepsItsColumn()
    {
        var placer = new PanelPlacer(4);
        placer.Take(new PanelRect(0, 0, 100, 100));
        Assert.That(placer.Place(new PanelRect(Margin, Margin, 50, 2 * Screen), Screen, Screen, Margin, Gap, Slack),
            Is.EqualTo((Margin, 0.0, false)));
    }

    // The canvas pushes a column that starts too far right back over its neighbour; the placer sees it there and moves it
    [Test]
    public void AColumnPastTheScreenEdgeDoesNotLandOnItsNeighbour()
    {
        var placer = new PanelPlacer(4);
        var neighbour = new PanelRect(400, 0, 500, 500);
        placer.Take(neighbour);
        var (x, y, _) = placer.Place(new PanelRect(950, Margin, 300, 200), Screen, Screen, Margin, Gap, Slack);
        var placed = new PanelRect(x, y, 300, 200);
        Assert.Multiple(() =>
        {
            Assert.That(placed is { X: >= 0, Y: >= 0, Right: <= Screen, Bottom: <= Screen }, Is.True,
                $"{placed} leaves the screen");
            Assert.That(placed.Overlap(neighbour), Is.Zero);
        });
    }

    // The user's layout: three pinned panels, then the column slots of two new ones on top of the first
    [Test]
    public void PanelsPlacedInTurnStayOnScreenAndApart()
    {
        var placer = new PanelPlacer(8);
        PanelRect[] pinned = [new(586, 10, 730, 1080), new(745, 1033, 723, 211), new(1505, 10, 640, 677)];
        foreach (var rect in pinned) placer.Take(rect);
        List<PanelRect> placed = [];
        foreach (var slot in (PanelRect[])[new PanelRect(600, 10, 730, 520), new PanelRect(600, 540, 730, 330)])
        {
            var (x, y, _) = placer.Place(slot, 2560, 1440, Margin, Gap, Slack);
            placed.Add(slot with { X = x, Y = y });
            placer.Take(placed[^1]);
        }

        Assert.Multiple(() =>
        {
            foreach (var rect in placed)
            {
                Assert.That(rect is { X: >= 0, Y: >= 0, Right: <= 2560, Bottom: <= 1440 }, Is.True,
                    $"{rect} leaves the screen");
                Assert.That(pinned.Sum(rect.Overlap), Is.Zero, $"{rect} covers a pinned panel");
            }

            Assert.That(placed[0].Overlap(placed[1]), Is.Zero);
        });
    }

    // A column taller than the screen goes on beside itself, the other corners mirror the flow, and nothing moves while nothing changed
    [Test]
    public void ColumnsFlowFromTheCornerAndWrap()
    {
        var layout = new PanelLayout(3);
        PanelBox[] boxes = [new(true, 0, 200, 600, null), new(true, 0, 150, 600, null), new(false, 0, 100, 100, null)];
        var frame = new LayoutFrame(Screen, Screen, HudCorner.TopLeft, Gap, Margin, Slack, -1);
        Assert.Multiple(() =>
        {
            Assert.That(layout.Update(boxes, frame), Is.True);
            Assert.That(layout.Drawn(0), Is.EqualTo(new PanelRect(Margin, Margin, 200, 600)));
            Assert.That(layout.Drawn(1), Is.EqualTo(new PanelRect(Margin + 200 + Gap, Margin, 150, 600)),
                "below would pass the bottom");
            Assert.That(layout.Layer(2), Is.EqualTo(PanelLayout.Hidden), "never drawn, so it takes no room");
            Assert.That(layout.Update(boxes, frame), Is.False, "nothing changed");
            Assert.That(layout.Update(boxes, frame with { Corner = HudCorner.BottomRight }), Is.True);
            Assert.That(layout.Drawn(0),
                Is.EqualTo(new PanelRect(Screen - Margin - 200, Screen - Margin - 600, 200, 600)));
        });
    }

    // A pinned panel draws between the squeezed ones and the columns; a click takes the panel on top
    [Test]
    public void AClickTakesThePanelOnTop()
    {
        var layout = new PanelLayout(2);
        PanelBox[] boxes = [new(true, 0, 300, 300, null), new(true, 0, 100, 100, (Margin, Margin))];
        _ = layout.Update(boxes, new LayoutFrame(Screen, Screen, HudCorner.TopLeft, Gap, Margin, Slack, -1));
        Assert.Multiple(() =>
        {
            Assert.That((layout.Layer(0), layout.Layer(1)), Is.EqualTo((2, 1)),
                "the column panel moved off the pinned one, free");
            Assert.That(layout.Drawn(0).Overlap(layout.Drawn(1)), Is.Zero);
            Assert.That(layout.Topmost(Margin + 1, Margin + 1), Is.EqualTo(1));
            Assert.That(layout.Topmost(Screen - 1, Screen - 1), Is.EqualTo(PanelLayout.Hidden));
        });
    }

    // Within the snap distance another panel's edge beats the grid; the dragged panel is not a target of its own
    [Test]
    public void ADraggedPanelSnapsToTheOthersEdges()
    {
        var layout = new PanelLayout(2);
        var frame = new LayoutFrame(Screen, Screen, HudCorner.TopLeft, Gap, Margin, Slack, 1);
        PanelBox[] boxes = [new(true, 0, 200, 200, null), new(true, 0, 100, 100, (500, 500))];
        _ = layout.Update(boxes, frame);
        Assert.Multiple(() =>
        {
            Assert.That(layout.Snap(Margin + 200 + Gap + 3, 100, true, frame, 8, 12), Is.EqualTo(Margin + 200 + Gap),
                "a gap beside it");
            Assert.That(layout.Snap(403, 100, true, frame, 8, 12), Is.EqualTo(400), "the grid, with no edge near");
        });
    }

    [Test]
    public void TheQueueHandsOutEachPendingPanelOnceRoundRobin()
    {
        var queue = new PanelQueue(8);
        foreach (var panel in (int[])[5, 1, 3, 1]) queue.Add(panel);
        var first = queue.Next();
        queue.Add(1); // due again before the others were drawn
        var order = new List<int> { first };
        for (var i = 0; i < 8; i++) order.Add(queue.Next());
        Assert.Multiple(() =>
        {
            Assert.That(order.Take(4), Is.EqualTo(RoundRobin), "the cursor moves on, so 1 waits behind 3 and 5");
            Assert.That(order.Skip(4), Is.All.EqualTo(-1));
        });
    }

    [Test]
    public void ARemovedPanelIsNotDrawnAgain()
    {
        var queue = new PanelQueue(4);
        queue.Add(2);
        queue.Remove(2);
        Assert.That(queue.Next(), Is.EqualTo(-1));
    }
}
