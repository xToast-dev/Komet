using Komet.Rendering;

namespace Komet.Test.Options;

// The documented example pack is what pack authors copy: it parses whole, and setting values reach GLSL as numbers
public sealed class ShaderPackTests
{
    private static readonly string[] ExampleTypes = ["group", "switch", "slider"];

    private static string Example([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Join(Path.GetDirectoryName(here), "../../../Komet/assets/komet/config/komet-templates/shader-example"));

    [Test]
    public void TheExamplePackParsesWhole()
    {
        var pack = ShaderPacks.Parsed(null, File.ReadAllText(Path.Join(Example(), ShaderPacks.PackFile)), "example", "example")!;
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

    // A define's value is GLSL whatever the player's culture: whole numbers for switches and choices, a point for sliders
    [TestCase(1.0, "switch", "1")]
    [TestCase(2.0, "choice", "2")]
    [TestCase(0.25, "slider", "0.25")]
    [TestCase(1.0, "slider", "1.0")]
    public void DefinesAreGlslNumbers(double value, string type, string expected) =>
        Assert.That(ShaderPacks.Text(value, type), Is.EqualTo(expected));

    // The Vulkan template is a whole mod: its komet.json parses, each pass file exists and declares the uniforms the
    // settings feed, each pass gets the pack's defines, and every lang key it names is in both languages
    [Test]
    public void TheVulkanTemplateHangsTogether()
    {
        var mod = Path.Join(Example(), "../vulkan-example/myshaders/assets/myshaders");
        var pack = ShaderPacks.Parsed(null, File.ReadAllText(Path.Join(mod, "config/komet.json")), "myshaders", "myshaders")!;
        var sources = string.Concat(pack.Passes.Select(p => File.ReadAllText(Path.Join(mod, p.Fragment))));
        var keys = pack.Settings.SelectMany(s => s.Options.Append(s.Label).Append(s.Hint)).OfType<string>()
            .Where(k => k.StartsWith("myshaders:", StringComparison.Ordinal)).Select(k => k["myshaders:".Length..]).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(pack.Passes.Select(p => "pass:" + p.Id), Is.EquivalentTo(pack.Programs));
            Assert.That(pack.Settings.Where(s => s.Uniform is not null).Select(s => "uniform float " + s.Uniform + ";"),
                Has.All.Matches<string>(u => sources.Contains(u, StringComparison.Ordinal)));
            foreach (var lang in (string[])["en", "de"])
                Assert.That(keys, Is.SubsetOf(Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Join(mod, "lang", lang + ".json"))).Properties().Select(p => p.Name)), lang);
            Assert.That(Directory.GetFiles(Path.Join(Example(), "../../../.."), "*.cs", SearchOption.AllDirectories), Is.Empty,
                "the game refuses a zipped mod (Komet itself) with .cs files outside src/: templates keep them as .cs.txt");
        });
    }

    [Test]
    public void ABrokenFileGivesNoPack() => Assert.That(ShaderPacks.Parsed(null, "{ \"settings\": [", "x", "x"), Is.Null);
}
