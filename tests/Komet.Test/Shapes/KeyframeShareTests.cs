using Komet.Shapes;
using Newtonsoft.Json;

namespace Komet.Test.Shapes;

// Equal elements at the same frame become one object, across animations; another frame or value stays its own
[NonParallelizable]
public sealed class KeyframeShareTests
{
    private const string Json = """
                                [
                                  { "code": "a", "quantityframes": 10, "keyframes": [
                                    { "frame": 0, "elements": { "head": { "rotationX": 10 }, "tail": { "rotationX": 10 } } },
                                    { "frame": 5, "elements": { "head": { "rotationX": 10 } } } ] },
                                  { "code": "b", "quantityframes": 10, "keyframes": [
                                    { "frame": 0, "elements": { "leg": { "rotationX": 10 }, "arm": { "rotationX": 20 } } } ] } ]
                                """;

    [Test]
    public void EqualElementsAtOneFrameAreShared()
    {
        using var harmony = new TestHarmony("komet-test-keyframeshare");
        KeyframeShare.Install(harmony);
        try
        {
            Assert.That(KeyframeShare.Installed, Is.True, "Animation.OnDeserializedMethod changed");
            var animations = JsonConvert.DeserializeObject<Animation[]>(Json)!;
            var (a, b) = (animations[0].KeyFrames, animations[1].KeyFrames);
            var head = a[0].Elements["head"];
            Assert.That(a[0].Elements["tail"], Is.SameAs(head));
            Assert.That(b[0].Elements["leg"], Is.SameAs(head));
            Assert.That(a[1].Elements["head"], Is.Not.SameAs(head), "another frame");
            Assert.That(b[0].Elements["arm"], Is.Not.SameAs(head), "another value");
            Assert.That(KeyframeShare.Holds(head), Is.True);
        }
        finally
        {
            KeyframeShare.Stop();
        }
    }
}
