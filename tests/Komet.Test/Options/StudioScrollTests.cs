using Komet.Sdk;

namespace Komet.Test.Options;

public sealed class StudioScrollTests
{
    [Test]
    public void WheelReachesTheScrollerUnderAButtonAndStopsAtAPopup()
    {
        var hits = new StudioHits();
        var column = new SwallowRegion();
        hits.Region(0, 0, 100, 100, column);
        hits.Add(10, 10, 50, 20, () => { });
        Assert.That(hits.WheelAt(20, 20), Is.SameAs(column));
        hits.Block(0, 0, 100, 100);
        hits.Add(0, 0, 100, 100, () => { });
        Assert.That(hits.WheelAt(20, 20), Is.Null);
    }

    [Test]
    public void SmallWheelStepsAddUp()
    {
        var steps = new WheelSteps();
        var lines = 0;
        for (var i = 0; i < 10; i++)
        {
            lines += steps.Take(0.1, 3, 1000);
        }

        Assert.That(lines, Is.EqualTo(3));
        Assert.That(steps.Take(-1, 3, 1000), Is.EqualTo(-3));
    }

    [Test]
    public void ScrollStepsOverAFoldedBlock()
    {
        var document = new ShaderDocument("a.fsh", "", "void main() {\n  a;\n  b;\n}\nx;\ny;\nz;", false);
        document.ToggleFold(0);
        Assert.That(document.Hidden(1), Is.True);
        Assert.That(CodeEditor.Scrolled(document, 1), Is.EqualTo(4));
        document.FirstLine = 5;
        Assert.That(CodeEditor.Scrolled(document, -2), Is.EqualTo(0));
    }
}
