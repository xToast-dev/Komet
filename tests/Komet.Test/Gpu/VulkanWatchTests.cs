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

    // At the game's exit the watch is woken, not waited for through its period
    [Test]
    public void StopEndsTheWatchAtOnce()
    {
        var (logs, folder) = (GamePaths.CustomLogPath, Directory.CreateTempSubdirectory("komet-watch").FullName);
        GamePaths.CustomLogPath = folder;
        try
        {
            VulkanWatch.Start();
            _ = SpinWait.SpinUntil(static () => false, 50); // inside its wait
            var stopping = Stopwatch.StartNew();
            VulkanWatch.Stop();
            var stopped = stopping.ElapsedMilliseconds;
            Assert.Multiple(() =>
            {
                Assert.That(stopped, Is.LessThan(250), "woken, not slept through a period");
                if (OperatingSystem.IsLinux()) // the kernel's name of the thread, cut to 15 characters
                {
                    Assert.That(VulkanWatch.Threads(), Does.Not.Contain(" komet-vulkan-wa "), "the watch thread ended");
                }
            });
        }
        finally
        {
            VulkanWatch.Stop();
            GamePaths.CustomLogPath = logs;
            Directory.Delete(folder, true);
        }
    }
}
