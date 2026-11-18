using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class ShaderAutoPairsTests
{
    [TestCase("(", "()", 1)]
    [TestCase("[", "[]", 1)]
    [TestCase("{", "{}", 1)]
    [TestCase("\"", "\"\"", 1)]
    public void AddsMatchingClosingCharacter(string input, string expected, int caret)
    {
        var edit = ShaderAutoPairs.ForInput("", 0, 0, input);

        Assert.That(edit, Is.Not.Null);
        Assert.That(edit!.Value.Text, Is.EqualTo(expected));
        Assert.That(edit.Value.Caret, Is.EqualTo(caret));
        Assert.That(edit.Value.Skip, Is.False);
    }

    [Test]
    public void WrapsSelectedTextAndKeepsCaretInside()
    {
        var edit = ShaderAutoPairs.ForInput("value", 0, 5, "{");

        Assert.That(edit!.Value.Text, Is.EqualTo("{value}"));
        Assert.That(edit.Value.Caret, Is.EqualTo(6));
    }

    [Test]
    public void SkipsExistingClosingCharacter()
    {
        var edit = ShaderAutoPairs.ForInput("x)", 1, 1, ")");

        Assert.That(edit!.Value.Skip, Is.True);
        Assert.That(edit.Value.Caret, Is.EqualTo(2));
        Assert.That(edit.Value.Text, Is.Empty);
    }

    [Test]
    public void IgnoresOtherCharactersAndMultiCharacterPaste()
    {
        Assert.That(ShaderAutoPairs.ForInput("", 0, 0, "x"), Is.Null);
        Assert.That(ShaderAutoPairs.ForInput("", 0, 0, "()"), Is.Null);
    }
}
