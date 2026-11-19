using Komet.Core;
using Komet.Options;
using Newtonsoft.Json.Linq;

namespace Komet.Test.Options;

// ConfigLib's settings as Komet rows, on stand-ins shaped like ConfigLib's types (reflection finds them by name)
public sealed class ConfigLibPagesTests
{
    [OneTimeSetUp]
    public void LoadTheGamesLanguage() => GameLang.LoadEnglish();

    [Test]
    public void AChangeGoesThroughConfigLibAndApplyWritesTheFile()
    {
        var setting = new FakeSetting("distance", ConfigSettingType.Float, 1.5)
            { Validation = new FakeValidation { Minimum = Json(0), Maximum = Json(4) } };
        var config = new FakeConfig(setting);
        var page = new OptionPage("test", "Test", "Mods");
        _ = ConfigLibPages.Fill(page, config, allowed: true);

        Row(page, "distance").Set(2.5);
        ConfigLibPages.Save(null, EventArgs.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(setting.Value.AsDouble(), Is.EqualTo(2.5));
            Assert.That(setting.ChangedCalls, Is.EqualTo(1));
            Assert.That(config.Writes, Is.EqualTo(1));
        });
    }

    private static OptionRow Row(OptionPage page, string label) =>
        Enumerable.Range(0, page.Count).Select(i => page[i]).First(r => r.Label == label);

    private static JsonObject Json(object value) => new(JToken.FromObject(value));

    internal enum ConfigSettingType
    {
        Boolean,
        Float,
        Integer,
        String
    }

    internal sealed class FakeValidation
    {
        public JsonObject? Minimum { get; init; }
        public JsonObject? Maximum { get; init; }
        public JsonObject? Step { get; init; }
        public List<JsonObject>? Values { get; init; }
        public Dictionary<string, JsonObject>? Mapping { get; init; }
    }

    internal sealed class FakeSetting(string code, ConfigSettingType type, object value)
    {
        public string YamlCode { get; } = code;
        public string? InGui { get; init; }
        public string? Comment { get; init; }
        public ConfigSettingType SettingType { get; } = type;
        public JsonObject Value { get; set; } = Json(value);
        public JsonObject DefaultValue { get; } = Json(value);
        public FakeValidation? Validation { get; init; }
        public string? MappingKey { get; set; }
        public bool ClientSide { get; init; }
        public bool Hide { get; init; }
        public float SortingWeight { get; init; }
        public int ChangedCalls { get; private set; }

        internal void Changed() => ChangedCalls++;
    }

    internal sealed class FakeConfig(params FakeSetting[] settings)
    {
        internal Dictionary<string, FakeSetting> Settings { get; } = settings.ToDictionary(s => s.YamlCode);
        internal string ModName { get; } = "Fake mod";
        public int Writes { get; private set; }

        public void WriteToFile() => Writes++;
    }
}
