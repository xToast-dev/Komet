using Komet.Chunks;
using Komet.Core;
using Komet.Api;

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

}
