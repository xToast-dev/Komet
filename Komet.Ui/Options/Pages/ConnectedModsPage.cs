using System.Globalization;
using Komet.Api;
using Komet.Core;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// The mods connected through KAPI: what each registered, its render steps and their cost, and the mods Komet refused.
// A mod's switch turns its render steps off for the session (the player's way out of a misbehaving mod).
internal static class ConnectedModsPage
{
    internal const string Id = "komet-connected";
    private const int MaxSteps = 16; // listed per mod in its card

    internal static OptionPage Build(ICoreClientAPI? api, string section, Action<string> navigate)
    {
        var mods = Kapi.Mods;
        var refused = Kapi.Refusals;
        var page = new OptionPage(Id, T("connected-title"), section) { Capacity = OptionPage.MaxRows };
        _ = page.Group(T("connected-summary", Kapi.Version, mods.Count))
            .Button(T("connected-refresh"), T("do-reload"), () => navigate(Id), T("connected-hint"));
        if (mods.Count == 0) _ = page.Group(T("connected-none"));

        foreach (var mod in mods.Bounded(Kapi.MaxMods))
        {
            var id = mod.ModId;
            _ = page.Switch(Name(api, id), () => !Kapi.IsSilenced(id), on => Kapi.Silence(id, !on),
                    T("connected-switch-hint"))
                .Default(1).Live()
                .Tagged(() => Tags(mod))
                .Card(() => Facts(mod));
            if (SettingsOf(id) is { } settings)
            {
                _ = page.Button("   " + T("connected-open-settings"), T("do-open"), () => navigate(settings),
                    T("connected-open-settings-hint"));
            }
        }

        if (refused.Count == 0) return page;

        _ = page.Group(T("connected-refused"));
        foreach (var (modId, version, why) in refused.Bounded(Kapi.MaxMods))
        {
            var detail = T("connected-refused-detail", modId, version.Length > 0 ? version : "?", Kapi.Version, why);
            _ = page.Button(Name(api, modId), T("do-copy"), () => Copy(api, detail), detail)
                .Tagged(() => [(TagIcon.Warning, TagKind.Warn)]);
        }

        return page;
    }

    // The first options page the mod registered, its id
    private static string? SettingsOf(string modId)
    {
        for (var i = 0; i < Math.Min(OptionPages.Count, OptionPages.MaxPages); i++)
        {
            var id = OptionPages.At(i).Id;
            if (id == modId || id.StartsWith(modId + ":", StringComparison.Ordinal)) return id;
        }

        return null;
    }

    private static string Name(ICoreClientAPI? api, string modId) =>
        api?.ModLoader?.GetMod(modId)?.Info?.Name is { Length: > 0 } name ? name : modId;

    private static List<(TagIcon Icon, TagKind Kind)> Tags(KapiMod mod)
    {
        List<(TagIcon, TagKind)> tags = [(TagIcon.Link, TagKind.Speaks), (TagIcon.Code, TagKind.Plain)];
        if (Kapi.IsSilenced(mod.ModId)) tags.Add((TagIcon.Pause, TagKind.Plain));

        if (Steps(mod).Any(s => s.Failure is not null)) tags.Add((TagIcon.Warning, TagKind.Warn));

        return tags;
    }

    private static List<(TagIcon Icon, TagKind Kind, string Text)> Facts(KapiMod mod)
    {
        List<(TagIcon, TagKind, string)> facts =
        [
            (TagIcon.Link, TagKind.Speaks, T("connected-fact-kapi", mod.Version, Kapi.Version)),
            (TagIcon.Code, TagKind.Plain, T("connected-fact-made", mod.Count(KapiKind.Step), mod.Count(KapiKind.Pass),
                mod.Count(KapiKind.Shader), mod.Count(KapiKind.Page), mod.FeatureCount, mod.Count(KapiKind.Hold),
                mod.Count(KapiKind.Style)))
        ];
        if (Kapi.IsSilenced(mod.ModId)) facts.Add((TagIcon.Pause, TagKind.Plain, T("connected-fact-silenced")));

        foreach (var step in Steps(mod).Bounded(MaxSteps))
        {
            facts.Add(step.Failure is { } failure
                ? (TagIcon.Warning, TagKind.Warn, T("connected-fact-failed", step.Step.Id, failure))
                : (TagIcon.Shader, TagKind.Plain, T("connected-fact-step", step.Step.Id, Stage(step.Step.Stage),
                    step.AverageMs.ToString("0.000", CultureInfo.InvariantCulture))));
        }

        return facts;
    }

    private static StepEntry[] Steps(KapiMod mod) => [.. RenderSteps.All.Where(s => s.ModId == mod.ModId)];

    private static string Stage(KapiRenderStage stage) => T("connected-stage-" + stage.ToString().ToLowerInvariant());

    private static void Copy(ICoreClientAPI? api, string text)
    {
        if (api?.Input is { } input) input.ClipboardText = text;
    }
}
