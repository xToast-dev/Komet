namespace Komet.Test.Api;

// What another mod sees of Komet's features: their snapshot and states, holds by id or switch name that end once (or with the world),
// the events with a handler that throws taken out, and the helpers that mirror Komet's own (Fingerprint, PatchKinds, the page ids).
// Nothing throws into the calling mod. Every test closes the registry again.
[NonParallelizable]
public sealed class KometFeaturesTests
{
    private CapturingLogger _log = null!;

    [SetUp]
    public void Attach()
    {
        _log = new CapturingLogger();
        ApiEvents.Logger = _log;
    }

    [TearDown]
    public void Close()
    {
        Features.Close();
        KometOptions.Clear();
        ApiEvents.Logger = null;
    }

    [Test]
    public void TheSnapshotListsKometsFeaturesInInstallOrder()
    {
        var snapshot = KometFeatures.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Select(f => f.Id), Is.EqualTo(Features.All.ToArray().Select(f => f.Id)));
            Assert.That(snapshot.All(f => f.Owner == "komet" && f.Title == f.Id), Is.True);
            Assert.That(snapshot.Where(f => !f.CanHoldOff).Select(f => f.Id), Does.Contain("TessSafety"));
            Assert.That(snapshot.Select(f => f.State), Has.All.EqualTo(FeatureState.Pending), "nothing installed");
            Assert.That(KometFeatures.StateOf("Nothing"), Is.EqualTo(FeatureState.Unknown));
        });
    }

    [Test]
    public void HoldingAnUnknownIdOrAFeatureWithoutASwitchHoldsNothing()
    {
        using var unknown = KometFeatures.HoldOff("Nothing", "mymod", "test");
        using var safety = KometFeatures.HoldOff("TessSafety", "mymod", "test");
        Assert.Multiple(() =>
        {
            Assert.That((unknown.IsHolding, unknown.FeatureId), Is.EqualTo((false, "Nothing")));
            Assert.That(safety.IsHolding, Is.False);
            Assert.That(_log.Lines, Has.Count.EqualTo(2).And.All.Contain("mymod cannot hold off"));
            Assert.That(Features.HoldsText(), Is.EqualTo("none"));
        });
    }

    // A switch's name holds its feature: ChunkBudget at the engine's upload, until the handle goes
    [Test]
    public void ASwitchNameHoldsItsFeature()
    {
        LoadALanguage();
        var before = ChunkBudget.CapMillis;
        Assert.That(before, Is.Not.EqualTo(ChunkBudget.Uncapped));
        using (var hold = KometFeatures.HoldOff("UploadCap", "mymod", "measuring"))
        {
            var info = KometFeatures.Snapshot().Single(f => f.Id == "ChunkBudget");
            Assert.Multiple(() =>
            {
                Assert.That((hold.FeatureId, hold.IsHolding), Is.EqualTo(("ChunkBudget", true)));
                Assert.That(ChunkBudget.CapMillis, Is.EqualTo(ChunkBudget.Uncapped));
                Assert.That((info.HeldBy, info.Reason), Is.EqualTo(("mymod", "measuring")));
                Assert.That(Features.LockText(Knobs.Find("UploadCap")), Does.EndWith("\nmymod: measuring"));
            });
        }

        Assert.That(ChunkBudget.CapMillis, Is.EqualTo(before));
    }

    // Release twice is one release: the second hold still keeps the feature off
    [Test]
    public void AReleaseIsIdempotent()
    {
        var first = KometFeatures.HoldOff("FrustumSweep", "mymod", "one");
        using var second = KometFeatures.HoldOff("FrustumSweep", "othermod", "two");
        first.Release();
        first.Release();
        first.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That((first.IsHolding, second.IsHolding), Is.EqualTo((false, true)));
            Assert.That(FrustumSweep.Enabled, Is.False);
            Assert.That(Features.HoldsText(), Is.EqualTo("FrustumSweep (1)"));
        });
        second.Release();
        Assert.That(FrustumSweep.Enabled, Is.True);
    }

    // The world's close ends every hold; a handle of the closed world releases nothing of the next
    [Test]
    public void ClosingTheWorldEndsTheHolds()
    {
        var old = KometFeatures.HoldOff("FrustumSweep", "mymod", "old");
        Features.Close();
        Assert.That((old.IsHolding, FrustumSweep.Enabled), Is.EqualTo((false, true)));
        using var fresh = KometFeatures.HoldOff("FrustumSweep", "mymod", "new");
        old.Release();
        Assert.That((fresh.IsHolding, FrustumSweep.Enabled), Is.EqualTo((true, false)));
    }

    // A hold, its release and the player's switch each tell the subscribers
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
            KometFeatures.StateChanged += (_, e) => seen.Add($"{e.Id} {e.Previous}>{e.State}");
            KometFeatures.HoldOff("MenuBlur", "mymod", "test").Release();
            new HudSettings().SetKnob(Knobs.Find("MenuBlur"), 0);
            Assert.That(string.Join(", ", seen),
                Is.EqualTo("MenuBlur Active>HeldOff, MenuBlur HeldOff>Active, MenuBlur Active>Off"));
        }
        finally
        {
            Features.Close();
            Backdrop.Enabled = before;
        }
    }

    // One handler that throws is logged once, with its assembly, and unsubscribed; the others hear every change
    [Test]
    public void AThrowingSubscriberIsLoggedOnceAndRemoved()
    {
        using var rig = new FeatureRig("MenuBlur");
        var (thrown, heard) = (0, 0);
        KometFeatures.StateChanged += (_, _) =>
        {
            thrown++;
            throw new InvalidOperationException("a subscriber's bug");
        };
        KometFeatures.StateChanged += (_, _) => heard++;
        KometFeatures.HoldOff("MenuBlur", "mymod", "test").Release();
        Assert.Multiple(() =>
        {
            Assert.That((thrown, heard), Is.EqualTo((1, 2)));
            Assert.That(_log.Lines, Has.Count.EqualTo(1));
            Assert.That(_log.Lines[0], Does.Contain("KometFeatures.StateChanged handler of Komet.Test threw"));
        });
    }

    // After Apply or Done in the options screen; the world's close drops the subscribers
    [Test]
    public void AppliedReachesItsSubscribersUntilTheWorldCloses()
    {
        var (heard, thrown) = (0, 0);
        KometOptions.Applied += (_, _) => heard++;
        KometOptions.Applied += (_, _) =>
        {
            thrown++;
            throw new InvalidOperationException("a subscriber's bug");
        };
        KometOptions.RaiseApplied();
        KometOptions.RaiseApplied();
        KometOptions.Clear();
        KometOptions.RaiseApplied();
        Assert.Multiple(() =>
        {
            Assert.That((heard, thrown), Is.EqualTo((2, 1)));
            Assert.That(_log.Lines.Single(), Does.Contain("KometOptions.Applied handler of Komet.Test threw"));
        });
    }

    [Test]
    public void NothingThrowsIntoTheCallingMod()
    {
        Assert.Multiple(() =>
        {
            Assert.That(KometFeatures.HoldOff(null!, "mymod", "test").IsHolding, Is.False);
            Assert.That(KometFeatures.Register(null!), Is.False);
            Assert.That(KometFeatures.StateOf(null!), Is.EqualTo(FeatureState.Unknown));
            Assert.That(KometFeatures.Fingerprint(null!), Is.Zero);
            Assert.That(FeatureKnob.Range(1, 1, 1, () => 1, _ => { }, _ => { }).Valid, Is.False);
            Assert.That(FeatureKnob.Switch(null!, _ => { }, _ => { }).Valid, Is.False);
        });
    }

    // The helper a mod pins its bodies with is Komet's own fingerprint
    [Test]
    public void TheFingerprintIsEngineShapes()
    {
        MethodBase?[] methods = [VisibleFaces.Target(), AccessTools.Method(typeof(Block), nameof(Block.DoEmitSideAo))];
        Assert.That(KometFeatures.Fingerprint(methods), Is.EqualTo(EngineShape.Of(methods)).And.Not.Zero);
    }

    [Test]
    public void PatchKindsAreEngineShapesKinds()
    {
        var kinds = Enum.GetValues<EngineShape.Kinds>().Select(k => (k.ToString(), (int)k));
        Assert.That(Enum.GetValues<PatchKinds>().Select(k => (k.ToString(), (int)k)), Is.EqualTo(kinds));
    }

    [Test]
    public void ThePageIdsAreKometsPages()
    {
        LoadALanguage();
        var pages = new KometPages(new HudSettings(), () => { }, _ => { }, () => { }).Build().Select(p => p.Id);
        string[] ids = [KometFeatures.RenderPage, KometFeatures.ChunksPage, KometFeatures.MiscPage];
        Assert.That(pages, Is.SupersetOf(ids));
    }

    // Lang.Get throws until a language is loaded: the game's English, unless one is loaded already
    internal static void LoadALanguage()
    {
        if (Lang.CurrentLocale is { } locale && Lang.AvailableLanguages.ContainsKey(locale)) return;
        GameLang.LoadEnglish();
    }
}
