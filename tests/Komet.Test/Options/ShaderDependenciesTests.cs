using Komet.Options;

namespace Komet.Test.Options;

public sealed class ShaderDependenciesTests
{
    private static readonly string[] SharedOrder = ["shared", "a", "b", "root"];

    [Test]
    public void SharedDependenciesArePlannedOnceBeforeTheirUsers()
    {
        var graph = new Dictionary<string, string[]>
            { ["root"] = ["a", "b"], ["a"] = ["shared"], ["b"] = ["shared"], ["shared"] = [] };
        Assert.That(ShaderDependencies.Plan("root", id => graph[id], out var order, out var why), Is.True, why);
        Assert.That(order, Is.EqualTo(SharedOrder));
    }

    [Test]
    public void ACycleProducesNoActivationPlan()
    {
        Assert.That(ShaderDependencies.Plan("root", id => id == "root" ? ["child"] : ["root"],
            out var order, out var why), Is.False);
        Assert.That(order, Is.Empty);
        Assert.That(why, Does.Contain("cycle").And.Contain("root"));
    }

    [Test]
    public void AChainPastTheLimitProducesNoActivationPlan()
    {
        Assert.That(ShaderDependencies.Plan("0",
            id => [(int.Parse(id, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture)],
            out var order, out var why), Is.False);
        Assert.That(order, Is.Empty);
        Assert.That(why, Does.Contain("limit"));
    }

    [Test]
    public void TooManyDependenciesProduceNoActivationPlan()
    {
        Assert.That(ShaderDependencies.Plan("root", _ => new string[ShaderDependencies.MaxMods + 1],
            out var order, out var why), Is.False);
        Assert.That(order, Is.Empty);
        Assert.That(why, Does.Contain("too many"));
    }
}
