using Komet.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Komet.Test.World;

// Equal values in different trees become one object of their type; names, other types and the trees stay as they were
public sealed class JsonValuesTests
{
    [Test]
    public void EqualValuesShareOneObject()
    {
        var (a, b) = (JObject.Parse("""{ "s": "stone", "n": 3, "d": 0.5, "x": [ "stone", 4 ] }"""),
            JObject.Parse("""{ "s": "stone", "n": 3, "d": 0.5, "i": 3.0 }"""));
        var pass = new JsonValues.Pass();
        pass.Walk(a);
        pass.Walk(b);
        Assert.That(((JValue)b["s"]!).Value, Is.SameAs(((JValue)a["s"]!).Value));
        Assert.That(((JValue)a["x"]![0]!).Value, Is.SameAs(((JValue)a["s"]!).Value));
        Assert.That(((JValue)b["n"]!).Value, Is.SameAs(((JValue)a["n"]!).Value));
        Assert.That(((JValue)b["d"]!).Value, Is.SameAs(((JValue)a["d"]!).Value));
        Assert.That(((JValue)b["i"]!).Type, Is.EqualTo(JTokenType.Float), "3.0 stays a double");
        Assert.That(pass.Swapped, Is.Positive);
        Assert.That(b.ToString(Formatting.None), Is.EqualTo("""{"s":"stone","n":3,"d":0.5,"i":3.0}"""));
    }
}
