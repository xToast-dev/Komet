using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class ShaderBracketMatcherTests
{
    [Test]
    public void MatchesNestedBrackets()
    {
        var pairs = ShaderBracketMatcher.Build("main(vec2[2]) { return value[0]; }");
        Assert.That(pairs[4], Is.EqualTo(12));
        Assert.That(pairs[9], Is.EqualTo(11));
        Assert.That(pairs[14], Is.EqualTo(33));
    }

    [Test]
    public void IgnoresBracketCharactersInsideCommentsAndStrings()
    {
        var pairs = ShaderBracketMatcher.Build("// (\n\"[\" /* { */ (x)");
        Assert.That(pairs, Has.Count.EqualTo(2));
        Assert.That(pairs, Does.Not.ContainKey(3));
        Assert.That(pairs, Does.Not.ContainKey(6));
        Assert.That(pairs, Does.Not.ContainKey(12));
        Assert.That(pairs[17], Is.EqualTo(19));
    }

}
