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

}
