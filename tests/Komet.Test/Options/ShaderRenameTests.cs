using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class ShaderRenameTests
{
    [Test]
    public void ReplacesWholeWordsOutsideCommentsAndStrings()
    {
        var text = ShaderRename.ReplaceWord("float fog = fogDensity * fog; // fog\n/* fog */ #x \"fog\"\nfog", "fog", "haze",
            out var count);
        Assert.That(text, Is.EqualTo("float haze = fogDensity * haze; // fog\n/* fog */ #x \"fog\"\nhaze"));
        Assert.That(count, Is.EqualTo(3));
    }

}
