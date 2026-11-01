using Komet.Api;
using Komet.Core;

namespace Komet.Options;

// The in-game docs: Markdown in Komet's assets under config/komet-docs/<language>/<name>.md (the game loads only its own
// asset categories, so not a docs folder), the game's language, else English. Two sections: Help for players, Modding for
// mod authors and designers. One page each, drawn by the options screen's reader, parsed once per language.
internal static class KometDocs
{
    // Help's pages, then Modding's; the Templates page (rows, not a doc) goes after Modding's first page
    public static readonly string[] Help = ["komet", "menu", "hud", "vulkan", "problems", "glossary"];
    public static readonly string[] Modding = ["modding", "tutorial", "shaders", "vulkan-shaders", "styles", "api"];
    public static readonly string[] Names = [.. Help, .. Modding];
    private const string Fallback = "en";

    private static (string Locale, OptionPage[] Pages)? _built;

    // The sections' titles: the screen puts rows pages beside the docs with these
    public static string HelpSection => KometText.Translate("settings-section-docs");
    public static string ModdingSection => KometText.Translate("settings-section-modding");

    public static OptionPage[] Pages(ICoreClientAPI api)
    {
        var locale = Lang.CurrentLocale is { Length: > 0 } current ? current : Fallback;
        if (_built is { } built && built.Locale == locale) return built.Pages;
        if (!NotNull(api) || api.Assets is null) return [];
        var (help, modding) = (HelpSection, ModdingSection);
        List<OptionPage> pages = [];
        foreach (var name in Names.Bounded(Names.Length))
        {
            var text = Read(api, locale, name) ?? Read(api, Fallback, name);
            if (text is null) continue;
            var blocks = DocText.Parse(text);
            var title = blocks.Find(b => b.Kind == DocText.Kind.Title)?.Text ?? name;
            pages.Add(new OptionPage("docs-" + name, title, Array.IndexOf(Help, name) >= 0 ? help : modding) { Doc = blocks });
        }

        _built = (locale, [.. pages]);
        return [.. pages];
    }

    // The docs with a rows page after Modding's first page (the Templates: buttons sit beside the guides that use them)
    public static OptionPage[] With(OptionPage[] docs, OptionPage page)
    {
        if (!NotNull(docs) || !NotNull(page)) return docs ?? [];
        var at = Array.FindIndex(docs, p => p.Id == "docs-" + Modding[0]) + 1;
        if (at <= 0) at = docs.Length; // the Modding pages missing from the assets: the page closes the list
        return [.. docs[..at], page, .. docs[at..]];
    }

    private static string? Read(ICoreClientAPI api, string locale, string name)
    {
        return api.Assets.TryGet(new AssetLocation("komet", string.Join('/', "config", "komet-docs", locale, name + ".md")))?.ToText();
    }
}
