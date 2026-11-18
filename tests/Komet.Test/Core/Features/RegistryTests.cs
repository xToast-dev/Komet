using Komet.Core;
using Komet.Api;

namespace Komet.Test.Core;

[NonParallelizable]
public sealed class RegistryTests
{
    [Test]
    public void TheSameTableCanBeRegisteredAgain()
    {
        var table = BuiltIns.All();
        Features.Use([table]);
        Assert.That(BuiltIns.All(), Is.EqualTo(table));
    }

    [Test]
    public void DifferentFeaturesWithTheSameCountAreRefused()
    {
        var original = BuiltIns.All();
        var changed = original.ToArray();
        changed[0] = changed[0] with { Id = "DifferentFeature" };
        _ = Assert.Throws<InvalidOperationException>(() => Features.Use([changed]));
        Assert.That(BuiltIns.All(), Is.EqualTo(original));
    }

    [Test]
    public void ADuplicateOrderIsRefused()
    {
        var table = BuiltIns.All();
        table[1] = table[1] with { Order = table[0].Order };
        _ = Assert.Throws<InvalidOperationException>(() => Features.Use([table]));
    }

    [Test]
    public void AMissingReplacementMethodStandsDownTheBuiltInFeature()
    {
        var table = (Feature[])AccessTools.Field(typeof(FeatureTable), "_builtIn").GetValue(null)!;
        var index = Features.Find("MenuBlur");
        var original = table[index];
        try
        {
            table[index] = original with { Replaces = () => [null], Rivals = KapiPatchKinds.Replacing };
            using var rig = new FeatureRig("MenuBlur");
            Assert.That(rig.State, Is.EqualTo(KapiFeatureState.StoodDown));
        }
        finally
        {
            table[index] = original;
        }
    }

    [Test]
    public void AThrowingStopDoesNotPreventOtherFeaturesFromStopping()
    {
        var table = AccessTools.Field(typeof(FeatureTable), "_builtIn");
        var original = table.GetValue(null);
        var logger = new CapturingLogger();
        var previous = ModEvents.Logger;
        var stopped = false;
        try
        {
            ModEvents.Logger = logger;
            table.SetValue(null, new Feature[]
            {
                new("remaining") { Stop = _ => stopped = true },
                new("broken") { Stop = _ => throw new InvalidOperationException("stop failed") }
            });
            Features.Stop(null);
            Assert.That(stopped, Is.True);
            Assert.That(logger.Lines, Has.Some.Contains("broken: stop failed; cleanup continues"));
        }
        finally
        {
            table.SetValue(null, original);
            ModEvents.Logger = previous;
        }
    }
}
