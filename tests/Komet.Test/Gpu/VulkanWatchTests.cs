using Komet.Vulkan;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class VulkanWatchTests
{
    [Test]
    public void ATimedOutWatchIsNotReplacedWhileItStillRuns()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var logs = GamePaths.CustomLogPath;
        var folder = Directory.CreateTempSubdirectory("komet-watch").FullName;
        var watching = VulkanWatch.Watching;
        GamePaths.CustomLogPath = folder;
        try
        {
            VulkanWatch.Watching = () =>
            {
                // ReSharper disable once AccessToDisposedClosure - the finally releases and stops the watch first
                entered.Set();
                // ReSharper disable once AccessToDisposedClosure - the finally releases and stops the watch first
                _ = release.Wait(TimeSpan.FromSeconds(5));
                return false;
            };
            VulkanWatch.Start();
            Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
            var field = AccessTools.Field(typeof(VulkanWatch), "_thread");
            var original = (Thread)field.GetValue(null)!;
            VulkanWatch.Stop();
            VulkanWatch.Start();
            Assert.That(field.GetValue(null), Is.SameAs(original));
            release.Set();
            Assert.That(original.Join(TimeSpan.FromSeconds(5)), Is.True);
            VulkanWatch.Stop();
            Assert.That(field.GetValue(null), Is.Null);
        }
        finally
        {
            release.Set();
            VulkanWatch.Stop();
            VulkanWatch.Watching = watching;
            GamePaths.CustomLogPath = logs;
            Directory.Delete(folder, true);
        }
    }

}
