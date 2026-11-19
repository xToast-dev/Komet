using System.Text;
using Komet.Api;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Options;

internal static class CompatibilityPage
{
    internal const string Id = "komet-compatibility", Guide = "docs-compatibility";
    private const int MaxRows = 220;

    internal static bool Relevant(KapiFeatureInfo feature) =>
        feature.State is KapiFeatureState.HeldOff or KapiFeatureState.StoodDown
            or KapiFeatureState.EngineChanged or KapiFeatureState.Failed or KapiFeatureState.Unknown ||
        CompatibilityLog.HasDetails(feature.Id);

    internal static OptionPage Build(ICoreClientAPI? api, string section, Action<string> navigate, KometActions actions,
        out int notices)
    {
        var issues = FeatureHub.Snapshot().Where(Relevant).ToArray();
        var (changes, failure) = HarmonyAudit.KometChanges();
        notices = issues.Length + changes.Length + (failure.Length > 0 ? 1 : 0);
        var page = new OptionPage(Id, T("compat-title"), section) { Capacity = OptionPage.MaxRows };
        _ = page.Group(T("compat-summary", issues.Length, changes.Length))
            .Button(T("compat-refresh"), T("do-reload"), () => navigate(Id), T("compat-snapshot"))
            .Button(T("compat-copy"), T("do-copy"), () => Copy(api,
                Report(issues, changes, api?.ModLoader?.GetMod(KometMod.Id)?.Info?.Version ?? "unknown", failure)))
            .Button(T("compat-guide"), T("do-open"), () => navigate(Guide), T("compat-guide-hint"))
            .Button(T("checksum"), T("do-verify"), actions.Verify, T("compat-integrity"))
            .Button(T("debugwindow", HudSettings.KeyOf(api, "window")), T("do-open"), actions.Debug);
        _ = page.Group(T("compat-features"));
        if (issues.Length == 0) _ = page.Group(T("compat-none"));

        foreach (var issue in issues.Bounded(MaxRows / 2))
        {
            var detail = FeatureDetail(issue);
            _ = page.Button(Label(issue), T("compat-details"), () => Copy(api, detail), detail)
                .Tagged(() => issue.State == KapiFeatureState.HeldOff ? [] : [(TagIcon.Warning, TagKind.Warn)]);
        }

        _ = page.Group(T("compat-modified"));
        if (failure.Length > 0)
        {
            _ = page.Button(T("compat-audit-failed"), T("compat-details"), () => Copy(api, failure), failure)
                .Tagged(() => [(TagIcon.Warning, TagKind.Warn)]);
        }
        else if (changes.Length == 0)
        {
            _ = page.Group(T("compat-unmodified"));
        }

        foreach (var method in changes.Bounded(MaxRows / 2))
        {
            var detail = T("compat-patch-info") + "\n\n" + PatchDetail(method);
            var owners = string.Join(", ", method.Chain.Select(p => p.Owner).Distinct());
            _ = page.Button(owners, T("compat-details"), () => Copy(api, detail), detail);
        }

        if (issues.Length > MaxRows / 2 || changes.Length > MaxRows / 2) _ = page.Group(T("compat-truncated"));

        return page;
    }

    private static string Label(KapiFeatureInfo feature)
    {
        var key = "komet:settings-" + feature.Id.ToLowerInvariant();
        var translated = Lang.Get(key);
        return translated == key ? feature.Title : translated;
    }

    internal static string FeatureDetail(KapiFeatureInfo feature)
    {
        var explanation = feature.State switch
        {
            KapiFeatureState.HeldOff => T("compat-held"),
            KapiFeatureState.StoodDown => T("compat-stooddown"),
            KapiFeatureState.EngineChanged => T("compat-engine"),
            KapiFeatureState.Failed => T("compat-failed"),
            _ => T("compat-observed")
        };
        var holder = feature.HeldBy is { } mod ? $"\n{T("compat-requested", mod)}\n{feature.Reason}" : "";
        var evidence = CompatibilityLog.Details(feature.Id);
        return $"{explanation}{holder}\n\nFeature: {feature.Id}\nOwner: {feature.Owner}\nState: {feature.State}\n" +
               (evidence.Length > 0 ? evidence : T("compat-no-evidence"));
    }

    private static string PatchDetail(PatchedMethod method) =>
        method.Name + "\n" + string.Join("\n", method.Chain.Select(p =>
            $"Harmony ID: {p.Owner}\n{p.Kind}, priority {p.Priority}\nPatch: {p.Method}"));

    internal static string Report(KapiFeatureInfo[] issues, PatchedMethod[] changes, string version = "unknown",
        string failure = "")
    {
        var text = new StringBuilder(
            $"Komet compatibility snapshot\nKomet: {version}\nBuild ID: {KometMod.Assembly.ManifestModule.ModuleVersionId}\n" +
            $"Game: {GameVersion.ShortGameVersion}\nOS: {Environment.OSVersion}\n\n");
        _ = text.AppendLine(T("compat-snapshot")).AppendLine(T("compat-patch-info"));
        if (failure.Length > 0) _ = text.AppendLine(T("compat-audit-failed")).AppendLine(failure);

        foreach (var issue in issues.Bounded(Features.MaxFeatures))
        {
            _ = text.AppendLine().AppendLine(FeatureDetail(issue));
        }

        foreach (var method in changes.Bounded(HarmonyAudit.MaxMethods))
        {
            _ = text.AppendLine().AppendLine(PatchDetail(method));
        }

        return text.ToString();
    }

    private static void Copy(ICoreClientAPI? api, string text)
    {
        if (api?.Input is { } input) input.ClipboardText = text;
    }
}
