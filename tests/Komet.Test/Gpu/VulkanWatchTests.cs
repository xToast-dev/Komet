using Komet.Vulkan;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class VulkanWatchTests
{
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
                    Assert.That(VulkanWatch.Threads(), Does.Not.Contain(" komet-vulkan-wa "), "the watch thread ended");
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
