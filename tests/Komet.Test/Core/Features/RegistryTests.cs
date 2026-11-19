using Komet.Core;

namespace Komet.Test.Core;

[NonParallelizable]
public sealed class RegistryTests
{

    [Test]
    public void ADuplicateOrderIsRefused()
    {
        var table = BuiltIns.All();
        table[1] = table[1] with { Order = table[0].Order };
        _ = Assert.Throws<InvalidOperationException>(() => Features.Use([table]));
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
