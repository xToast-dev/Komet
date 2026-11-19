using Komet.Rendering;

namespace Komet.Test.Options;

// The documented example pack is what pack authors copy: it parses whole, and setting values reach GLSL as numbers
public sealed class ShaderPackTests
{
    private static readonly string[] ExampleTypes = ["group", "switch", "slider"];

    private static string Example([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here),
            "../../../Komet.Sdk/assets/kometsdk/config/komet-templates/shader-example"));

    [Test]
    public void TheExamplePackParsesWhole()
    {
        var pack = ShaderPacks.Parsed(null, File.ReadAllText(Path.Join(Example(), ShaderPacks.PackFile)), "example",
            "example")!;
        Assert.Multiple(() =>
        {
            Assert.That(pack.Id, Is.EqualTo("example"));
            Assert.That(pack.Settings.Select(s => s.Type), Is.EqualTo(ExampleTypes));
            Assert.That(pack.Settings[1].Define, Is.EqualTo("EXAMPLE_TINT"));
            Assert.That(pack.Settings[2].Uniform, Is.EqualTo("exampleStrength"));
            Assert.That(pack.Passes.Single().When, Is.EqualTo("tint"));
            Assert.That(File.Exists(Path.Join(Example(), pack.Passes.Single().Fragment)));
        });
    }

    [Test]
    public void ABrokenFileGivesNoPack() =>
        Assert.That(ShaderPacks.Parsed(null, "{ \"settings\": [", "x", "x"), Is.Null);
}
