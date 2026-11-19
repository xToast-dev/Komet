using Komet.Core;

namespace Komet.Options;

// The in-game docs: Markdown in Komet's assets under config/komet-docs/<language>/<name>.md (the game loads only its own
// asset categories, so not a docs folder), the game's language, else English. One section, Help, in two groups: the
// players' pages, then Modding's for mod authors and designers. One page each, drawn by the options screen's reader,
// parsed once per language.
internal static class KometDocs
{
    private const string Fallback = "en";

    public const string SdkDomain = "kometsdk";

    // Help's pages, then Modding's; the Templates page (rows, not a doc) goes after Modding's first page
    public static readonly string[] Help = ["komet", "menu", "hud", "vulkan", "problems", "glossary"];

    public static readonly string[] Modding =
    [
        "modding", "tutorial", "integration", "compatibility", "shaders", "vulkan-shaders", "styles", "api",
        "api-options", "api-features", "api-shaders", "api-rendering", "api-styles", "api-debug"
    ];

    public static readonly string[] Names = [.. Help, .. Modding];

    private static (string Locale, OptionPage[] Pages)? _built;

    // The section's title and its two groups' headings: the screen puts rows pages beside the docs with these
    public static string Section => KometText.Translate("settings-section-docs");
    private static string PlayersGroup => KometText.Translate("settings-docs-group-players");
    public static string ModdingGroup => KometText.Translate("settings-section-modding");

    public static OptionPage[] Pages(ICoreClientAPI api)
    {
        var locale = Lang.CurrentLocale is { Length: > 0 } current ? current : Fallback;
        if (_built is { } built && built.Locale == locale) return built.Pages;

        if (api.Assets is null) return [];

        var (section, players, modding) = (Section, PlayersGroup, ModdingGroup);
        List<OptionPage> pages = [];
        foreach (var name in Names.Bounded(Names.Length))
        {
            var text = Read(api, locale, name) ?? Read(api, Fallback, name);
            if (text is null) continue;

            var blocks = DocText.Parse(text);
            var title = blocks.Find(b => b.Kind == DocText.Kind.Title)?.Text ?? name;
            pages.Add(new OptionPage("docs-" + name, title, section)
                { Doc = blocks, Heading = Array.IndexOf(Help, name) >= 0 ? players : modding });
        }

        _built = (locale, [.. pages]);
        return [.. pages];
    }

    // The docs with a rows page after Modding's first page (the Templates: buttons sit beside the guides that use them)
    public static OptionPage[] With(OptionPage[] docs, OptionPage page)
    {
        if (!NotNull(docs)) return [];

        if (!NotNull(page)) return docs;

        var at = Array.FindIndex(docs, p => p.Id == "docs-" + Modding[0]) + 1;
        if (at <= 0) return docs; // no Komet SDK: no Modding section, no templates

        return [.. docs[..at], page, .. docs[at..]];
    }

    // Help's pages are in the komet domain, Modding's in the Shader Studio's (kometsdk); both ship in Komet's zip
    private static string? Read(ICoreClientAPI api, string locale, string name) =>
        api.Assets
            .TryGet(new AssetLocation(Array.IndexOf(Help, name) >= 0 ? "komet" : SdkDomain,
                string.Join('/', "config", "komet-docs", locale, name + ".md")))
            ?.ToText();
}
