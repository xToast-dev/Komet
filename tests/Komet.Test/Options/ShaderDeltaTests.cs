using Komet.Rendering;

namespace Komet.Test.Options;

public sealed class ShaderDeltaTests
{
    [Test]
    public void OnlyValuesThatDifferFromTheDefaultAreKeptAndUnknownKeysStay()
    {
        var pack = new ShaderPacks.Pack { Settings = [new() { Key = "a", Default = 1 }, new() { Key = "b", Default = 0 }] };
        var stored = new Dictionary<string, double> { ["a"] = 0.5, ["gone"] = 3 };
        var delta = ShaderPacks.Delta(stored, new Dictionary<string, double> { ["a"] = 1, ["b"] = 2 }, pack);
        Assert.That(delta, Is.EquivalentTo(new Dictionary<string, double> { ["b"] = 2, ["gone"] = 3 }));
    }

}
