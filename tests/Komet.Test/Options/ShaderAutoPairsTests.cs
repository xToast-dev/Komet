using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class ShaderAutoPairsTests
{
    [TestCase("(", "()", 1)]
    [TestCase("[", "[]", 1)]
    public void AddsMatchingClosingCharacter(string input, string expected, int caret)
    {
        var edit = ShaderAutoPairs.ForInput("", 0, 0, input);

        Assert.That(edit, Is.Not.Null);
        Assert.That(edit!.Value.Text, Is.EqualTo(expected));
        Assert.That(edit.Value.Caret, Is.EqualTo(caret));
        Assert.That(edit.Value.Skip, Is.False);
    }

}
