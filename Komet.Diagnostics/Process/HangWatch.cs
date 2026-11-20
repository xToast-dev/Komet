using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Komet.Diagnostics;

// After HangMs without a frame: log line, thread states, kernel waits and (with dotnet-stack) managed stacks, while
// hanging.
internal static class HangWatch
{
    private const int CheckMs = 1000, HangMs = 10_000, DumpMs = 60_000, StepCount = 16;

    private static readonly Lock Gate = new();
    private static readonly (long At, string Step)[] Last = new (long, string)[StepCount];

    private static Timer? _timer;
    private static ILogger? _logger;
    private static long _seen = -1, _since;
    private static int _next;
    private static bool _reported;

    public static void Start(ILogger logger)
    {
        if (_timer is not null) return;

        (_logger, _seen, _since, _reported) = (logger, -1, Environment.TickCount64, false);
        _timer = new Timer(static _ => Check(), null, CheckMs, CheckMs);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        (_timer, _logger) = (null, null);
    }

    // Last steps of a frame, from pacing through Vulkan and the terrain rebuild. A repeat only refreshes its time:
    // a hang needs the step where work stopped, not a ring filled with one name called every frame.
    public static void Mark(string step)
    {
        if (string.IsNullOrEmpty(step)) return;

        lock (Gate)
        {
            var at = Environment.TickCount64;
            var previous = (_next + StepCount - 1) % StepCount;
            if (Last[previous].Step == step)
            {
                Last[previous] = (at, step);
                return;
            }

            Last[_next % StepCount] = (at, step);
            _next++;
        }
    }

    internal static string Steps()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            return string.Join(Environment.NewLine, Enumerable.Range(0, StepCount)
                .Select(i => Last[(_next + StepCount - 1 - i) % StepCount])
                .Where(last => last.Step is not null)
                .Select(last => $"  {now - last.At} ms ago: {last.Step}"));
        }
    }

    private static void Check()
    {
        var (frames, now) = (FrameClock.Frames, Environment.TickCount64);
        if (frames == 0)
        {
            return; // FrameClock's frame patch is missing (or off): no frame count, so no hang to tell
        }

        if (frames != _seen)
        {
            (_seen, _since, _reported) = (frames, now, false);
            return;
        }

        if (_reported || now - _since < HangMs || _logger is not { } logger) return;

        _reported = true; // once a hang
        logger.Warning("Komet: the main thread has finished no frame for {0} s (frame {1})", (now - _since) / 1000,
            frames);
        _ = Dump(logger);
    }

    // Includes native threads (Mesa, Vulkan driver) with the kernel function each waits in
    private static string? Dump(ILogger logger)
    {
        if (!NotNull(GamePaths.Logs)) return null;

        var path = Path.Join(GamePaths.Logs,
            "komet-hang-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
        try
        {
            var report = Tool() is { } tool ? Stacks(tool) : "dotnet-stack is not installed: no managed stacks";
            File.WriteAllText(path,
                report + "\nLast Komet steps, newest first:\n" + Steps() +
                "\nEvery thread as the kernel sees it (id, name, state, waits in):\n" +
                Links.Threads.Threads());
            logger.Warning("Komet: the stacks of every thread are in {0}", path);
            return path;
        }
        catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or
                                      UnauthorizedAccessException
                                      or AggregateException) // on a timer thread: anything left would end the game
        {
            logger.Warning("Komet: no stacks of the hang: {0}", e.Message);
            return null;
        }
    }

    private static string Stacks(string tool)
    {
        using var stack = Process.Start(
            new ProcessStartInfo(tool, ["report", "-p", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)])
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true
            });
        if (!NotNull(stack)) return "";

        var (output, error) = (stack.StandardOutput.ReadToEndAsync(), stack.StandardError.ReadToEndAsync());
        if (!stack.WaitForExit(DumpMs)) stack.Kill();
        // empty report (no attach permission, as on macOS) says why
        var report = output.Wait(DumpMs) ? output.Result : "";
        var (exit, why) = (stack.HasExited ? stack.ExitCode : -1, error.Wait(DumpMs) ? error.Result : "");
        return report.Length > 0 ? report : $"dotnet-stack wrote no stacks (exit {exit}): {why}";
    }

    private static string? Tool()
    {
        var name = OperatingSystem.IsWindows() ? "dotnet-stack.exe" : "dotnet-stack";
        var home = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools",
            name);
        if (File.Exists(home)) return home;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Bounded(256))
        {
            if (dir.Length > 0 && File.Exists(Path.Join(dir, name))) return Path.Join(dir, name);
        }

        return null;
    }
}
