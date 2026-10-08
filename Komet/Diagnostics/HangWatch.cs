using System.Diagnostics;
using System.Globalization;

namespace Komet.Diagnostics;

// A main thread that finishes no frame for HangMs is written down while it still hangs: a log line, and where dotnet-stack is
// installed (dotnet tool install --global dotnet-stack, a developer's machine) the stacks of every thread into
// Logs/komet-hang-<time>.txt. A freeze the player ends with a kill otherwise leaves nothing behind but a log that stops.
internal static class HangWatch
{
    private const int CheckMs = 1000, HangMs = 10_000, DumpMs = 60_000;

    private static Timer? _timer;
    private static ILogger? _logger;
    private static long _seen = -1, _since;
    private static bool _reported;

    public static void Start(ILogger logger)
    {
        if (!NotNull(logger) || !Assert(FrameClock.Completed >= 0) || _timer is not null) return;
        (_logger, _seen, _since, _reported) = (logger, -1, Environment.TickCount64, false);
        _timer = new Timer(static _ => Check(), null, CheckMs, CheckMs);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        (_timer, _logger) = (null, null);
        _ = Assert(!_reported || HangMs > 0);
    }

    private static void Check()
    {
        var (frames, now) = (FrameClock.Completed, Environment.TickCount64);
        if (!Assert(frames >= 0) || !Assert(now >= _since)) return;
        if (frames != _seen)
        {
            (_seen, _since, _reported) = (frames, now, false);
            return;
        }

        if (_reported || now - _since < HangMs || _logger is not { } logger) return;
        _reported = true; // once a hang
        logger.Warning("Komet: the main thread has finished no frame for {0} s (frame {1})", (now - _since) / 1000, frames);
        _ = Dump(logger);
        _ = Assert(_reported);
    }

    private static string? Dump(ILogger logger)
    {
        var tool = Tool();
        if (tool is null || !NotNull(GamePaths.Logs) || !Assert(Environment.ProcessId > 0)) return null;
        var path = Path.Combine(GamePaths.Logs, "komet-hang-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        try
        {
            using var stack = Process.Start(new ProcessStartInfo(tool, ["report", "-p", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            });
            if (!NotNull(stack) || !Assert(stack.StartInfo.RedirectStandardOutput)) return null;
            var output = stack.StandardOutput.ReadToEndAsync();
            if (!stack.WaitForExit(DumpMs)) stack.Kill();
            File.WriteAllText(path, output.Wait(DumpMs) ? output.Result : "dotnet-stack did not answer");
            logger.Warning("Komet: the stacks of every thread are in {0}", path);
            return path;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or
                                       UnauthorizedAccessException)
        {
            logger.Warning("Komet: no stacks of the hang: {0}", e.Message);
            return null;
        }
    }

    // On PATH, or where dotnet tool install --global puts it
    private static string? Tool()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet-stack.exe" : "dotnet-stack";
        if (!Assert(name.Length > 0)) return null;
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", name);
        if (File.Exists(home)) return home;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Bounded(256))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        return null;
    }
}
