using System.Globalization;
using Vintagestory.API.Common;

namespace Komet.Test.Shapes;

// The one tree walk behind the descriptor, the compile and the memo: the engine's pre-order, and a walk past its limits fails
// rather than comes back short.
public sealed class ElementWalkTests
{
    private static readonly string[] Visited = ["a@0<-1", "b@1<0", "null@1<0", "c@1<0", "d@2<3", "e@0<-1"];
    private static readonly string[] Skipped = ["a@0<-1", "b@1<0", "d@0<-1", "e@1<2"];

    private static ShapeElement E(string name, params ShapeElement[] children)
    {
        return new ShapeElement { Name = name, Children = children.Length == 0 ? null : children };
    }

    private static List<string> Walk(ShapeElement[] roots, string? skip = null)
    {
        var visits = new List<string>();
        var walk = new ElementWalk(roots);
        for (var n = 0; n <= ElementWalk.MaxElements; n++)
        {
            if (!walk.Next(out var element)) break;
            visits.Add($"{element?.Name ?? "null"}@{walk.Depth}<{walk.Parent}");
            if (element?.Name == skip) walk.Skip();
        }

        if (walk.Failed) visits.Add("failed");
        return visits;
    }

    private static string Name(int i)
    {
        return i.ToString(CultureInfo.InvariantCulture);
    }

    // One element per level, named by its depth
    private static ShapeElement[] Chain(int length)
    {
        var root = E(Name(0));
        var last = root;
        for (var i = 1; i < length; i++)
        {
            var next = E(Name(i));
            last.Children = [next];
            last = next;
        }

        return [root];
    }

    [Test]
    public void PreOrderWithDepthAndParentNullsIncluded()
    {
        ShapeElement[] roots = [E("a", E("b"), null!, E("c", E("d"))), E("e")];
        Assert.That(Walk(roots), Is.EqualTo(Visited));
    }

    [Test]
    public void SkippedChildrenAreNotVisited()
    {
        ShapeElement[] roots = [E("a", E("b", E("c"))), E("d", E("e"))];
        Assert.That(Walk(roots, "b"), Is.EqualTo(Skipped));
    }

    [Test]
    public void DeeperThanMaxDepthFailsUnlessSkipped()
    {
        var deepest = Walk(Chain(ElementWalk.MaxDepth));
        var tooDeep = Chain(ElementWalk.MaxDepth + 1);
        Assert.Multiple(() =>
        {
            Assert.That(deepest, Has.Count.EqualTo(ElementWalk.MaxDepth).And.No.Contains("failed"));
            Assert.That(Walk(tooDeep), Has.Count.EqualTo(ElementWalk.MaxDepth + 1).And.Contains("failed"));
            Assert.That(Walk(tooDeep, Name(ElementWalk.MaxDepth - 1)), Does.Not.Contain("failed"));
        });
    }

    [Test]
    public void MoreThanMaxElementsFails()
    {
        var most = Enumerable.Range(0, ElementWalk.MaxElements).Select(_ => E("x")).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(Walk(most), Has.Count.EqualTo(ElementWalk.MaxElements).And.No.Contains("failed"));
            Assert.That(Walk([.. most, E("y")]), Has.Count.EqualTo(ElementWalk.MaxElements + 1).And.Contains("failed"));
        });
    }
}
