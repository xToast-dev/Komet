using Komet.Api;
using Komet.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Komet.Rendering;

namespace Komet.Test.Options;

// docs/schema gives editors completion for komet-shader.json and style.json: it names exactly what Komet reads
public sealed class SchemaTests
{
    private static JObject Schema(string name, [CallerFilePath] string here = "") =>
        JObject.Parse(File.ReadAllText(Path.GetFullPath(Path.Join(Path.GetDirectoryName(here), "../../../docs/schema", name))));

    private static string[] Fields(Type type) =>
        [.. type.GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName).OfType<string>().Order()];

    private static string[] Keys(JToken? properties) =>
        [.. ((JObject)properties!).Properties().Select(p => p.Name).Where(n => n is not ("$schema" or "_comment")).Order()];

    [Test]
    public void TheShaderSchemaNamesWhatPacksRead()
    {
        var schema = Schema("komet-shader.schema.json")["properties"]!;
        Assert.Multiple(() =>
        {
            Assert.That(Keys(schema), Is.EqualTo(Fields(typeof(ShaderPacks.Pack))));
            Assert.That(Keys(schema["settings"]!["items"]!["properties"]), Is.EqualTo(Fields(typeof(ShaderPacks.Setting))));
            Assert.That(Keys(schema["passes"]!["items"]!["properties"]), Is.EqualTo(Fields(typeof(ShaderPacks.PassSpec))));
        });
    }

    // komet.json: a pack's fields plus what adapters read
    [Test]
    public void TheDeclarationSchemaNamesWhatKometReads()
    {
        var schema = Schema("komet.schema.json")["properties"]!;
        var expected = Fields(typeof(ShaderPacks.Pack)).Union(Fields(typeof(ModAdapters.Manifest))).Order();
        Assert.Multiple(() =>
        {
            Assert.That(Keys(schema), Is.EqualTo(expected));
            Assert.That(Keys(schema["config"]!["properties"]), Is.EqualTo(Fields(typeof(ModAdapters.Config))));
        });
    }

    [Test]
    public void TheStyleSchemaNamesWhatStylesRead()
    {
        var schema = Schema("komet-style.schema.json")["properties"]!;
        Assert.Multiple(() =>
        {
            Assert.That(Keys(schema), Is.EqualTo(Fields(typeof(KometStyle))));
            Assert.That(Keys(schema["colors"]!["properties"]), Is.EqualTo(StyleSheet.ColorKeys.Order()));
            Assert.That(Keys(schema["sizes"]!["properties"]), Is.EqualTo(StyleSheet.SizeKeys.Order()));
            Assert.That(Keys(schema["fonts"]!["properties"]), Is.EqualTo(StyleSheet.FontKeys.Order()));
        });
    }
}
