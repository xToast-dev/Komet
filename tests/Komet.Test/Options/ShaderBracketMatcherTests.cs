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

    [Test]
    public void FindsMultilineBraceBlocksForFolding()
    {
        var ranges = ShaderCodeFolding.Build("void main() {\n  if (true) {\n    color = vec4(1);\n  }\n}");
        Assert.That(ranges[0], Is.EqualTo(4));
        Assert.That(ranges[1], Is.EqualTo(3));
    }

    [Test]
    public void DoesNotFoldSingleLineOrCommentBraces()
    {
        var ranges = ShaderCodeFolding.Build("void main() { }\n// { ignored\nvec4 color;");
        Assert.That(ranges, Is.Empty);
    }

    [Test]
    public void FindsUniformAndFunctionDefinitionsInsteadOfUses()
    {
        const string source = "uniform float strength;\nfloat fog(vec3 p) {\n return strength;\n}\nvoid main() {\n fog(position);\n}";
        Assert.That(ShaderSymbolNavigator.TryFindDefinition(source, "strength", out var uniform), Is.True);
        Assert.That(source[uniform..], Does.StartWith("strength;"));
        Assert.That(ShaderSymbolNavigator.TryFindDefinition(source, "fog", out var function), Is.True);
        Assert.That(source[function..], Does.StartWith("fog("));
    }

    [Test]
    public void DoesNotTreatCallsOrCommentsAsDefinitions()
    {
        const string source = "// uniform float missing;\nvoid main() {\n missing();\n}";
        Assert.That(ShaderSymbolNavigator.TryFindDefinition(source, "missing", out _), Is.False);
    }
}
