using Komet.Diagnostics;

namespace Komet.Test.Diagnostics;

// The engine archives *.log per run; the consolidated file has to keep that separation without its own rotation.
[NonParallelizable]
public sealed class KometLogTests
{
    [Test]
    public void ACollectsEngineLinesAndExplicitTracesInOneFile()
    {
        var logs = GamePaths.CustomLogPath;
        var folder = Directory.CreateTempSubdirectory("komet-console").FullName;
        GamePaths.CustomLogPath = folder;
        try
        {
            File.WriteAllText(Path.Join(folder, "client-main.log"),
                "20.11.2026 10:00:00 [Notification] [komet] first engine line\n");
            File.WriteAllText(Path.Join(folder, "client-debug.log"), "debug line one\n");

            KometLog.Start();
            KometLog.Write("vulkan", "frame 42 step 'presenting'");
            File.AppendAllText(Path.Join(folder, "client-main.log"),
                "20.11.2026 10:00:01 [Warning] second engine line\n");
            KometLog.Stop();

            var text = File.ReadAllText(Path.Join(folder, "komet.log"));
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("Komet consolidated log"));
                Assert.That(text, Does.Contain("first engine line"));
                Assert.That(text, Does.Contain("second engine line"));
                Assert.That(text, Does.Contain("debug line one"));
                Assert.That(text, Does.Contain("[vulkan] frame 42"));
                Assert.That(text, Does.Contain("session ended"));
            });
        }
        finally
        {
            KometLog.Stop();
            GamePaths.CustomLogPath = logs;
            Directory.Delete(folder, true);
        }
    }

    [Test]
    public void BKeepsTheOffsetAcrossTwoWorldsInOneProcess()
    {
        var logs = GamePaths.CustomLogPath;
        var folder = Directory.CreateTempSubdirectory("komet-console").FullName;
        GamePaths.CustomLogPath = folder;
        try
        {
            var main = Path.Join(folder, "client-main.log");
            File.WriteAllText(main, "first world line\n");
            KometLog.Start();
            KometLog.Stop();

            File.AppendAllText(main, "second world line\n");
            KometLog.Start();
            KometLog.Stop();

            var text = File.ReadAllText(Path.Join(folder, "komet.log"));
            var starts = text.Split("session started").Length - 1;
            Assert.Multiple(() =>
            {
                Assert.That(text, Does.Contain("first world line"));
                Assert.That(text, Does.Contain("second world line"));
                Assert.That(starts, Is.EqualTo(2));
            });
        }
        finally
        {
            KometLog.Stop();
            GamePaths.CustomLogPath = logs;
            Directory.Delete(folder, true);
        }
    }
}
