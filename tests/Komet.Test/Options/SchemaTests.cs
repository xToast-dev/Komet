using Komet.Rendering;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Options;

// docs/schema gives editors completion for komet-shader.json and style.json: it names exactly what Komet reads
public sealed class SchemaTests
{
    private static JObject Schema(string name, [CallerFilePath] string here = "") =>
        JObject.Parse(
            File.ReadAllText(Path.GetFullPath(Path.Join(Path.GetDirectoryName(here), "../../../docs/schema", name))));

    private static string[] Fields(Type type) =>
        [
            .. type.GetProperties().Select(p => p.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName)
                .OfType<string>().Order()
        ];

    private static string[] Keys(JToken? properties) =>
        [
            .. ((JObject)properties!).Properties().Select(p => p.Name).Where(n => n is not ("$schema" or "_comment"))
            .Order()
        ];

    [Test]
    public void TheShaderSchemaNamesWhatPacksRead()
    {
        var schema = Schema("komet-shader.schema.json")["properties"]!;
        Assert.Multiple(() =>
        {
            Assert.That(Keys(schema), Is.EqualTo(Fields(typeof(ShaderPacks.Pack)).Order()));
            Assert.That(Keys(schema["settings"]!["items"]!["properties"]),
                Is.EqualTo(Fields(typeof(ShaderPacks.Setting))));
            Assert.That(Keys(schema["passes"]!["items"]!["properties"]),
                Is.EqualTo(Fields(typeof(ShaderPacks.PassSpec))));
        });
    }

}
