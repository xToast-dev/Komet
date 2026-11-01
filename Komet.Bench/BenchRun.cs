using System.Globalization;

namespace Komet.Bench;

internal readonly record struct BenchChat(double Seconds, string Type, string Text);

// What the driver saw of a segment once it ended (frames are in the recorder). NaN cpu: no resource sample.
internal readonly record struct BenchSegmentLog(
    bool Ran, double Seconds, double CpuPercent, double SystemCpuPercent, long WorkingSetMb, long ManagedMb);

internal sealed class BenchRun
{
    public const int MaxChat = 64, MaxChatText = 300, MaxInfo = 64;

    public BenchRun(BenchConfig config, BenchSegment[] segments)
    {
        if (!NotNull(config) || !Assert(segments.Length is > 0 and <= BenchScenario.MaxSegments))
            segments = [new BenchSegment(BenchKind.Setup, -1, 0, false, false, 1)];
        (Config, Segments, Logs) = (config, segments, new BenchSegmentLog[segments.Length]);
        Array.Fill(Logs, new BenchSegmentLog(false, 0, double.NaN, double.NaN, 0, 0));
    }

    public BenchConfig Config { get; }
    public BenchSegment[] Segments { get; }
    public BenchSegmentLog[] Logs { get; }
    public List<BenchChat> Chat { get; } = [];
    public List<KeyValuePair<string, string>> Info { get; } = [];
    public int[] Baseline { get; set; } = [];
    public int[][] ArmValues { get; set; } = [];
    public string? Error { get; private set; }
    public bool Complete { get; set; }
    public DateTime Started { get; } = DateTime.UtcNow;
    public DateTime Finished { get; set; }

    // The first error wins: later ones are usually its consequences
    public void Fail(string error)
    {
        if (!Assert(error.Length > 0) || !Assert(error.Length < 4096)) error = "unknown error";
        Error ??= error;
    }

    // The line the script's watchdog polls, written at phase changes outside measured frames
    public void Status(string phase)
    {
        if (!Assert(phase.Length > 0) || !Assert(Config.Status.Length > 0)) return;
        try
        {
            var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.WriteAllText(Config.Status, string.Create(CultureInfo.InvariantCulture, $"{phase} {seconds}\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail("status file not written: " + e.Message);
        }
    }

    public void AddChat(double seconds, string type, string text)
    {
        if (!Finite(seconds) || !NotNull(text) || Chat.Count >= MaxChat) return;
        Chat.Add(new BenchChat(seconds, type, text.Length > MaxChatText ? text[..MaxChatText] : text));
    }

    public void AddInfo(string key, string? value)
    {
        if (!Assert(key.Length > 0) || !Assert(Info.Count < MaxInfo)) return;
        Info.Add(new KeyValuePair<string, string>(key, value ?? ""));
    }
}
