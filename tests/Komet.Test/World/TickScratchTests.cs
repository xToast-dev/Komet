using Komet.World;
using Newtonsoft.Json.Linq;

namespace Komet.Test.World;

// The soil's walk and the barrel recipes' pairing are rewritten; a child is handed out again while its token stays, and
// the kept empty list is replaced once a caller filled it
[NonParallelizable]
public sealed class TickScratchTests
{
    [Test]
    public void ChildrenAndEmptyAnswersAreKept()
    {
        using var harmony = new TestHarmony("komet-test-tickscratch");
        TickScratch.Install(harmony);
        var token = JObject.Parse("""{ "killPlantRadius": 3 }""");
        var attributes = new JsonObject(token);
        var child = TickScratch.Child(attributes, "killPlantRadius");
        var empty = TickScratch.Empty<int>();
        Assert.Multiple(() =>
        {
            Assert.That(TickScratch.Rewritten, Is.EqualTo(2));
            Assert.That(child.AsInt(), Is.EqualTo(3));
            Assert.That(TickScratch.Child(attributes, "killPlantRadius"), Is.SameAs(child));
            Assert.That(TickScratch.Child(attributes, "KILLPLANTRADIUS").AsInt(), Is.EqualTo(3), "the engine's case-blind lookup");
            Assert.That(TickScratch.Child(attributes, "absent").Exists, Is.False);
            Assert.That(TickScratch.Empty<int>(), Is.SameAs(empty));
        });
        token["killPlantRadius"] = 5;
        empty.Add(1);
        Assert.Multiple(() =>
        {
            Assert.That(TickScratch.Child(attributes, "killPlantRadius").AsInt(), Is.EqualTo(5), "a new token under the key");
            Assert.That(TickScratch.Empty<int>(), Is.Empty.And.Not.SameAs(empty));
        });
        TickScratch.Clear();
    }
}
