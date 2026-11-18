using Komet.Chunks;
using Komet.Core;
using Komet.Hud;
using Komet.Api;
using Komet.Options;
using Komet.Rendering;

namespace Komet.Test.Core;

[NonParallelizable]
public sealed class FeatureHubTests
{
    private CapturingLogger _log = null!;
    private int _mainThread;

    [SetUp]
    public void Attach()
    {
        _log = new CapturingLogger();
        ModEvents.Logger = _log;
        (_mainThread, RuntimeEnv.MainThreadId) = (RuntimeEnv.MainThreadId, Environment.CurrentManagedThreadId);
    }

    [TearDown]
    public void Close()
    {
        FeatureLifecycle.Close();
        OptionPages.Clear();
        ModEvents.Logger = null;
        RuntimeEnv.MainThreadId = _mainThread;
    }

    [Test]
    public void TheSnapshotListsKometsFeaturesInInstallOrder()
    {
        var snapshot = FeatureHub.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(f => f.Id), Is.EqualTo(BuiltIns.All().Select(f => f.Id)));
            Assert.That(snapshot.All(f => f.Owner == "komet" && f.Title == f.Id), Is.True);
            Assert.That(snapshot.Where(f => !f.CanHoldOff).Select(f => f.Id), Does.Contain("TessSafety"));
            Assert.That(snapshot.Select(f => f.State), Has.All.EqualTo(KapiFeatureState.Pending), "nothing installed");
            Assert.That(FeatureHub.StateOf("Nothing"), Is.EqualTo(KapiFeatureState.Unknown));
        });
    }

    [Test]
    public void HoldingAnUnknownIdOrAFeatureWithoutASwitchHoldsNothing()
    {
        using var unknown = FeatureHub.HoldOff("Nothing", "mymod", "test");
        using var safety = FeatureHub.HoldOff("TessSafety", "mymod", "test");
        using (Assert.EnterMultipleScope())
        {
            Assert.That((unknown.IsHolding, unknown.FeatureId), Is.EqualTo((false, "Nothing")));
            Assert.That(safety.IsHolding, Is.False);
            Assert.That(_log.Lines, Has.Count.EqualTo(2).And.All.Contain("mymod cannot hold off"));
            Assert.That(Features.HoldsText(), Is.EqualTo("none"));
        }
    }

    [Test]
    public void ASwitchNameHoldsItsFeature()
    {
        GameLang.EnsureLoaded();
        var before = ChunkBudget.CapMillis;
        Assert.That(before, Is.Not.EqualTo(ChunkBudget.Uncapped));
        using (var hold = FeatureHub.HoldOff("UploadCap", "mymod", "measuring"))
        {
            var info = FeatureHub.Snapshot().Single(f => f.Id == "ChunkBudget");
            using (Assert.EnterMultipleScope())
            {
                Assert.That((hold.FeatureId, hold.IsHolding), Is.EqualTo(("ChunkBudget", true)));
                Assert.That(ChunkBudget.CapMillis, Is.EqualTo(ChunkBudget.Uncapped));
                Assert.That((info.HeldBy, info.Reason), Is.EqualTo(("mymod", "measuring")));
                Assert.That(Features.LockText(Knobs.Find("UploadCap")), Does.EndWith("\nmymod: measuring"));
            }
        }

        Assert.That(ChunkBudget.CapMillis, Is.EqualTo(before));
    }

    // Release twice is one release: the second hold still keeps the feature off
    [Test]
    public void AReleaseIsIdempotent()
    {
        var first = FeatureHub.HoldOff("FrustumSweep", "mymod", "one");
        using var second = FeatureHub.HoldOff("FrustumSweep", "othermod", "two");
        first.Release();
        first.Release();
        first.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That((first.IsHolding, second.IsHolding), Is.EqualTo((false, true)));
            Assert.That(FrustumSweep.Enabled, Is.False);
            Assert.That(Features.HoldsText(), Is.EqualTo("FrustumSweep (1)"));
        }
        second.Release();
        Assert.That(FrustumSweep.Enabled, Is.True);
    }

    // The world's close ends every hold; a handle of the closed world releases nothing of the next
    [Test]
    public void ClosingTheWorldEndsTheHolds()
    {
        var old = FeatureHub.HoldOff("FrustumSweep", "mymod", "old");
        FeatureLifecycle.Close();
        Assert.That((old.IsHolding, FrustumSweep.Enabled), Is.EqualTo((false, true)));
        using var fresh = FeatureHub.HoldOff("FrustumSweep", "mymod", "new");
        old.Release();
        Assert.That((fresh.IsHolding, FrustumSweep.Enabled), Is.EqualTo((true, false)));
    }

    [Test]
    public void StateChangedFollowsHoldsAndThePlayer()
    {
        using var rig = new FeatureRig("MenuBlur");
        var before = Backdrop.Enabled;
        List<string> seen = [];
        try
        {
            Backdrop.Enabled = true;
            Features.Poll();
            FeatureHub.StateChanged += (_, e) => seen.Add($"{e.Id} {e.Previous}>{e.State}");
            FeatureHub.HoldOff("MenuBlur", "mymod", "test").Release();
            new HudSettings().Knobs.SetKnob(Knobs.Find("MenuBlur"), 0);
            Assert.That(string.Join(", ", seen),
                Is.EqualTo("MenuBlur Active>HeldOff, MenuBlur HeldOff>Active, MenuBlur Active>Off"));
        }
        finally
        {
            FeatureLifecycle.Close();
            Backdrop.Enabled = before;
        }
    }

    [Test]
    public void AThrowingSubscriberIsLoggedOnceAndRemoved()
    {
        using var rig = new FeatureRig("MenuBlur");
        var (thrown, heard) = (0, 0);
        FeatureHub.StateChanged += (_, _) =>
        {
            thrown++;
            throw new InvalidOperationException("a subscriber's bug");
        };
        FeatureHub.StateChanged += (_, _) => heard++;
        FeatureHub.HoldOff("MenuBlur", "mymod", "test").Release();
        Assert.Multiple(() =>
        {
            Assert.That((thrown, heard), Is.EqualTo((1, 2)));
            Assert.That(_log.Lines, Has.Count.EqualTo(1));
            Assert.That(_log.Lines[0], Does.Contain("komet.Features.StateChanged handler of Komet.Test threw"));
        });
    }

    [Test]
    public void AppliedReachesItsSubscribersUntilTheWorldCloses()
    {
        var (heard, thrown) = (0, 0);
        OptionPages.Applied += (_, _) => heard++;
        OptionPages.Applied += (_, _) =>
        {
            thrown++;
            throw new InvalidOperationException("a subscriber's bug");
        };
        OptionPages.RaiseApplied();
        OptionPages.RaiseApplied();
        OptionPages.Clear();
        OptionPages.RaiseApplied();
        Assert.Multiple(() =>
        {
            Assert.That((heard, thrown), Is.EqualTo((2, 1)));
            Assert.That(_log.Lines.Single(), Does.Contain("komet.Options.Applied handler of Komet.Test threw"));
        });
    }

    [Test]
    public void NothingThrowsIntoTheCallingMod() =>
        Assert.Multiple(() =>
        {
            Assert.That(FeatureHub.HoldOff(null!, "mymod", "test").IsHolding, Is.False);
            Assert.That(FeatureHub.Register("mymod", null!), Is.False);
            Assert.That(FeatureHub.StateOf(null!), Is.EqualTo(KapiFeatureState.Unknown));
            Assert.That(FeatureHub.Fingerprint(null!), Is.Zero);
            Assert.That(KapiKnob.Range(1, 1, 1, () => 1, _ => { }, _ => { }).Valid, Is.False);
            Assert.That(KapiKnob.Switch(null!, _ => { }, _ => { }).Valid, Is.False);
        });

    [Test]
    public void TheFingerprintIsEngineShapes()
    {
        MethodBase?[] methods =
        [
            AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.CalculateVisibleFaces),
                [typeof(bool), typeof(int), typeof(int), typeof(int)]),
            AccessTools.Method(typeof(Block), nameof(Block.DoEmitSideAo))
        ];
        Assert.That(FeatureHub.Fingerprint(methods), Is.EqualTo(EngineShape.Of(methods)).And.Not.Zero);
    }
}
