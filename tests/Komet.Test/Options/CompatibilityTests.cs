using System.Text.Json;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Api;
using Komet.Options;

namespace Komet.Test.Options;

[NonParallelizable]
public sealed class CompatibilityTests
{
    private static readonly KometActions Actions = new(() => { }, _ => { }, () => { }, () => { }, () => { });
    private string _assets = "";

    [OneTimeSetUp]
    public void Language()
    {
        _assets = Directory.CreateTempSubdirectory("komet-compat-lang").FullName;
        using var json =
            JsonDocument.Parse(File.ReadAllText(Path.Join(Paths.KometDir, "assets", "komet", "lang", "en.json")));
        var entries = json.RootElement.EnumerateObject().ToDictionary(p => "komet:" + p.Name, p => p.Value.GetString());
        _ = Directory.CreateDirectory(Path.Join(_assets, "game", "lang"));
        File.WriteAllText(Path.Join(_assets, "game", "lang", "en.json"), JsonSerializer.Serialize(entries));
        var english = new TranslationService("en", new QuietLogger());
        english.PreLoad(_assets);
        Lang.AvailableLanguages["en"] = english;
        Lang.ChangeLanguage("en");
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

    private static void BreakAudit() => throw new InvalidOperationException("audit unavailable");

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
    public void ADirectPatchOnKometIsVisibleWithoutBeingCalledAnIncompatibility()
    {
        using var harmony = new TestHarmony("external-komet-extension");
        var target = AccessTools.Method(typeof(FeatureHub), nameof(FeatureHub.StateOf));
        _ = harmony.Patch(target,
            postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatibilityTests), nameof(Observe))));
        var methods = HarmonyAudit.OnKomet();
        Assert.That(methods.Any(m => m.Chain.Any(p => p.Owner == harmony.Id)), Is.True);
        var report = CompatibilityPage.Report([], methods);
        Assert.That(report,
            Does.Contain("not proof of an incompatibility").And.Contain(harmony.Id).And.Contain("StateOf("));
    }

    [Test]
    public void RequestedHoldsAreExplainedAsDeliberateCooperation()
    {
        var feature = new KapiFeatureInfo("Example", "Example", "komet", KapiFeatureState.HeldOff, true)
            { HeldBy = "render-mod", Reason = "custom render pass" };
        var detail = CompatibilityPage.FeatureDetail(feature);
        Assert.That(detail, Does.Contain("not an error").And.Contain("render-mod").And.Contain("custom render pass"));
        Assert.That(CompatibilityPage.Relevant(feature), Is.True);
        Assert.That(CompatibilityPage.Relevant(new KapiFeatureInfo("Off", "Off", "komet", KapiFeatureState.Off, true)),
            Is.False);
    }

    [Test]
    public void ASubpathFindingIsVisibleEvenWhenTheParentFeatureRemainsActive()
    {
        CompatibilityLog.Record("Example JSON", "patches", "a JSON method has another patch");
        var feature = new KapiFeatureInfo("Example", "Example", "komet", KapiFeatureState.Active, true);
        Assert.That(CompatibilityPage.Relevant(feature), Is.True);
        Assert.That(CompatibilityPage.FeatureDetail(feature), Does.Contain("subpaths").And.Contain("JSON method"));
    }

    [Test]
    public void CompatibilityPageOpensItsDeveloperGuide()
    {
        var destination = "";
        var page = CompatibilityPage.Build(null, "Komet", id => destination = id, Actions, out _);
        var guide = Enumerable.Range(0, page.Count).Select(i => page[i])
            .Single(r => r.Label == "Guide for mod developers");
        guide.Set(0);
        Assert.That(destination, Is.EqualTo(CompatibilityPage.Guide));
        Assert.That(KometDocs.Names, Does.Contain("compatibility"));
    }

    [Test]
    public void TheReportButtonCopiesTheSnapshot()
    {
        var copied = "";
        var input = Answers.Of<IInputAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
        {
            ["set_ClipboardText"] = args =>
            {
                copied = (string)args![0]!;
                return null;
            }
        });
        var api = Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
            { ["get_Input"] = _ => input });
        var page = CompatibilityPage.Build(api, "Komet", _ => { }, Actions, out _);
        var copy = Enumerable.Range(0, page.Count).Select(i => page[i])
            .Single(r => r.Label == "Copy compatibility report");
        copy.Set(0);
        Assert.That(copied, Does.Contain("Komet compatibility snapshot").And.Contain("Build ID:").And.Contain("Game:"));
    }

    [Test]
    public void AFailedAuditShowsAnErrorInsteadOfAFalseCleanResult()
    {
        using var harmony = new TestHarmony("audit-failure-test");
        _ = harmony.Patch(AccessTools.Method(typeof(HarmonyAudit), nameof(HarmonyAudit.OnKomet)),
            new HarmonyMethod(AccessTools.Method(typeof(CompatibilityTests), nameof(BreakAudit))));
        var page = CompatibilityPage.Build(null, "Komet", _ => { }, Actions, out var notices);
        var rows = Enumerable.Range(0, page.Count).Select(i => page[i]).ToArray();
        Assert.That(notices, Is.GreaterThan(0));
        Assert.That(rows.Select(r => r.Label), Does.Contain("Patch scan failed; details available"));
        Assert.That(rows.Select(r => r.Label), Does.Not.Contain("No external Harmony patches on Komet detected."));
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
