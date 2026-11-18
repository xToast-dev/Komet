using Komet.Core;
using Komet.Options;

namespace Komet.Test.Options;

[NonParallelizable]
public sealed class ModAdaptersTests
{
    [Test]
    public void RepeatedAdapterStartsHoldOnceAndStopReleasesTheHold()
    {
        using var rig = new FeatureRig("MenuBlur");
        var adapter = new ModAdapters.Adapter
        {
            Mod = "othermod",
            Holds = [new ModAdapters.Hold { Feature = "MenuBlur", Reason = "custom backdrop" }]
        };
        var hold = AccessTools.Method(typeof(ModAdapters), "HoldFor");
        var main = RuntimeEnv.MainThreadId;
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        try
        {
            _ = hold.Invoke(null, [adapter]);
            _ = hold.Invoke(null, [adapter]);
            Assert.That(Features.HoldsText(), Does.Contain("MenuBlur (1)"));
            ModAdapters.Stop();
            Assert.That(Features.HoldsText(), Is.EqualTo("none"));
        }
        finally
        {
            ModAdapters.Stop();
            RuntimeEnv.MainThreadId = main;
        }
    }
}
