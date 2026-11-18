using Komet.Core;
using Komet.Options;
using Newtonsoft.Json;

namespace Komet.Test.Options;

// Komet's own styles and its example template are what designers copy designers copy: every key they set is one the screen reads
public sealed class StyleTests
{
    private static string Folder([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here), "../../.."));

    [TestCase("Komet/assets/komet/config/komet-styles/pragmatic")]
    [TestCase("Komet.Sdk/assets/kometsdk/config/komet-templates/style-example")]
    public void BuiltInStylesUseOnlyKnownKeys(string id)
    {
        var style = JsonConvert.DeserializeObject<StyleData>(File.ReadAllText(Path.Join(Folder(), id, "style.json")))!;
        Assert.Multiple(() =>
        {
            Assert.That(style.Colors.Keys, Is.SubsetOf(StyleSheet.ColorKeys));
            Assert.That(style.Sizes.Keys, Is.SubsetOf(StyleSheet.SizeKeys));
            Assert.That(style.Fonts.Keys, Is.SubsetOf(StyleSheet.FontKeys));
            Assert.That(style.Colors.Values.Select(StyleSheet.Parse), Has.None.Null);
        });
    }

    // A style names only what it changes; the rest keeps its value
    [Test]
    public void AStyleNamesOnlyWhatItChanges()
    {
        var sheet = StyleSheet.From(new StyleData { Id = "ember", Colors = { ["accent"] = "#E2672F80" } }, null, null,
            null);
        Assert.That((sheet["accent"].R, sheet["accent"].A, sheet["faint"].A, sheet.Size("logo")),
            Is.EqualTo((0xE2 / 255.0, 0x80 / 255.0, 0x5C / 255.0, 96)));
    }

    // A player in Chinese (pre.3 report) or Thai gets the game's font, chosen for the language; others test their letters
    [TestCase("zh-cn", true)]
    [TestCase("zh-tw", true)]
    [TestCase("ja", true)]
    [TestCase("ko", true)]
    [TestCase("th", true)]
    [TestCase("de", false)]
    [TestCase("ru", false)]
    public void ScriptsNoStyleFontShipsTakeTheGamesFont(string lang, bool own)
    {
        Assert.That(StyleFonts.OwnScript(lang), Is.EqualTo(own));
        if (own)
        {
            Assert.That(StyleFonts.Reads("Saira Semi Condensed", lang), Is.False);
        }
        else
        {
            Assert.That(StyleFonts.Sample(lang), Is.Not.Empty, "its letters are tested");
        }

        Assert.That(StyleFonts.Reads("Saira Semi Condensed", "en"), Is.True, "English needs no test");
    }
}
