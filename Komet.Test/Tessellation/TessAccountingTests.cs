using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Test.Tessellation;

// The buckets a pass lands in, the cumulative totals the benchmark takes differences of, and the patches on the engine's real
// TesselateChunk
public sealed class TessAccountingTests
{
    [SetUp]
    public void Fresh()
    {
        TessAccounting.Clear();
        Counting.Hud = true;
    }

    [TearDown]
    public void Reset()
    {
        Counting.Hud = false;
        TessAccounting.Clear();
    }

    // Buckets by number, the enum is internal to Komet: 0 full, 1 edge, 2 priority full, 3 priority edge, 4 skipped, 5 requeued
    [TestCase(false, false, false, false, 4)]
    [TestCase(false, false, true, true, 4)]
    [TestCase(false, true, false, false, 5)]
    [TestCase(true, true, false, true, 5)]
    [TestCase(true, false, false, false, 0)]
    [TestCase(true, false, false, true, 1)]
    [TestCase(true, false, true, false, 2)]
    [TestCase(true, false, true, true, 3)]
    public void APassLandsInItsBucket(bool processed, bool requeue, bool priority, bool edge, int expected)
    {
        Assert.That(TessAccounting.Classify(processed, requeue, priority, edge), Is.EqualTo((TessBucket)expected));
    }

    [Test]
    public void OnlyFullPassesWithoutVerticesCountAsZero()
    {
        TessAccounting.Record(TessBucket.Full, 100, true);
        TessAccounting.Record(TessBucket.PriorityFull, 50, true);
        TessAccounting.Record(TessBucket.Edge, 30, true); // an edge pass without vertices is a hidden edge, not rock
        TessAccounting.Record(TessBucket.Skipped, 5, true);
        TessAccounting.Record(TessBucket.Full, 200, false);
        var totals = TessAccounting.Totals();
        Assert.Multiple(() =>
        {
            Assert.That(totals.Passes, Is.EqualTo(4), "a skipped call is no pass");
            Assert.That(totals.Ticks, Is.EqualTo(380));
            Assert.That(totals.Edge, Is.EqualTo(1));
            Assert.That(TessAccounting.ZeroMs, Is.EqualTo(150 * 1000.0 / Stopwatch.Frequency).Within(1e-9));
            Assert.That(TessAccounting.ZeroPasses, Is.EqualTo(2));
            Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(1));
        });
    }

    // The totals only grow: the benchmark and the HUD take differences of them
    [Test]
    public void ASegmentIsTheDifferenceOfTwoTotals()
    {
        TessAccounting.Record(TessBucket.Full, 1000, false);
        var start = TessAccounting.Totals();
        TessAccounting.Record(TessBucket.Edge, 3000, false);
        var segment = TessAccounting.Totals().Since(start);
        Assert.Multiple(() =>
        {
            Assert.That((segment.Passes, segment.Ticks, segment.Edge), Is.EqualTo((1L, 3000L, 1L)));
            Assert.That(segment.Ms, Is.EqualTo(3000 * 1000.0 / Stopwatch.Frequency).Within(1e-9));
            Assert.That(TessAccounting.Totals().Passes, Is.EqualTo(2));
        });
    }

    // The patched engine method (Install checks the parameter names Harmony hands over), on a chunk the map does not have and on an
    // Empty one: the engine returns before any lock, and that is a skipped call
    [Test]
    public void ThePatchesCountTheEnginesCalls()
    {
        using var rig = new ChunkRig();
        var manager = (ChunkTesselatorManager)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselatorManager));
        ChunkRig.Set(manager, "game", rig.Game);
        var harmony = new Harmony("komet-test-tessaccounting");
        try
        {
            TessAccounting.Install(harmony);
            Assert.That(TessAccounting.Installed, Is.True);
            Assert.That(manager.TesselateChunk(3, 3, 3, false, false, out var requeue), Is.Zero);
            var empty = rig.Put(2, 2, 2, (_, _, _) => ChunkRig.Air, empty: true);
            Assert.That(manager.TesselateChunk(2, 2, 2, true, true, out requeue), Is.Zero);
            Assert.Multiple(() =>
            {
                Assert.That(requeue, Is.False);
                Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(2));
                Assert.That(TessAccounting.Totals().Passes, Is.Zero);
                Assert.That(ChunkRig.Get(empty, "quantityDrawn"), Is.EqualTo(1), "the engine's own path ran");
            });
            Counting.Hud = false;
            _ = manager.TesselateChunk(3, 3, 3, false, false, out _);
            Assert.That(TessAccounting.Count(TessBucket.Skipped), Is.EqualTo(2), "nothing while nobody looks");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }
}
