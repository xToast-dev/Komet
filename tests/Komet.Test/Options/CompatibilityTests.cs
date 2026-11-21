using Komet.Core;
using Komet.Api;

namespace Komet.Test.Options;

[NonParallelizable]
public sealed class CompatibilityTests
{
    private string _assets = "";

    [OneTimeSetUp]
    public void Language()
    {
        _assets = GameLang.LoadKomet();
    }

    [OneTimeTearDown]
    public void RestoreLanguage()
    {
        GameLang.LoadEnglish();
        Directory.Delete(_assets, true);
    }

    [TearDown]
    public void Clear() => CompatibilityLog.Clear();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Target() => 1;

    private static void Observe()
    {
        // An observable Harmony patch that leaves the target's behaviour unchanged.
    }

    [Test]
    public void GuardEvidenceNamesTheTargetOwnerAndPatchAndClearsWhenResolved()
    {
        var target = AccessTools.Method(typeof(CompatibilityTests), nameof(Target));
        using var harmony = new TestHarmony("other-mod.compat");
        _ = harmony.Patch(target,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatibilityTests), nameof(Observe))));
        _ = EngineShape.Report(null, "Example", false, true, [target], KapiPatchKinds.Postfix);
        Assert.That(CompatibilityLog.Details("Example"),
            Does.Contain("Target()").And.Contain("other-mod.compat").And.Contain("Observe()"));
        _ = EngineShape.Report(null, "Example", true, false);
        Assert.That(CompatibilityLog.Details("Example"), Is.Empty);
    }

    [Test]
    public void TheMenuFallbackNeedsThe123DpiScaleAndEveryTab()
    {
        var shaped = Komet.Options.GraphicsMenu.Shaped();
        Assert.That(Komet.Options.GraphicsMenu.Structured(shaped), Is.True);
        var broken = (MethodBase?[])shaped.Clone();
        broken[2] = null;
        Assert.That(Komet.Options.GraphicsMenu.Structured(broken), Is.False);
        Assert.That(Komet.Options.GraphicsMenu.PointRelease123(shaped), Is.False,
            "1.22.7 has no DPI scale and keeps its stand-down");
    }

    [Test]
    public void FingerprintMismatchNamesExpectedFoundAndMethods()
    {
        var target = AccessTools.Method(typeof(CompatibilityTests), nameof(Target));
        var hash = EngineShape.Of([target]);
        Assert.That(EngineShape.Matches([target], hash ^ 1UL, "Example", null), Is.False);
        Assert.That(CompatibilityLog.Details("Example"),
            Does.Contain("Expected").And.Contain("found").And.Contain("Target()"));
        Assert.That(EngineShape.Matches([target], hash, "Example", null), Is.True);
        Assert.That(CompatibilityLog.Details("Example"), Is.Empty);
    }
}
