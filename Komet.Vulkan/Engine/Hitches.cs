using System.Diagnostics;
using Komet.Diagnostics;

namespace Komet.Vulkan;

// The engine's thread only. A frame's wait for the GPU counts as a stutter only past StutterMs; shorter ones are a
// GPU-bound frame's normal pace and go on their own report line.
internal static class Hitches
{
    public enum Kind
    {
        Port,
        Pipeline,
        GpuWait,
        RecorderWait,
        BufferRead,
        FrameWait
    }

    private const int Kinds = 5; // the stutters' kinds: FrameWait counts as GpuWait or a GPU-bound wait
    private const double StutterMs = 16;
    private static readonly long StutterTicks = (long)(StutterMs * Stopwatch.Frequency / 1000);

    private static readonly KindTimes Tallied =
        new(["ports", "pipelines", "waits on the GPU", "waits on the recording thread", "buffers read back"]);

    private static readonly KindTimes Bound = new(["waits"]);

    public static long Now => Stopwatch.GetTimestamp();

    public static void Since(Kind kind, long start)
    {
        var (i, ticks) = ((int)kind, Stopwatch.GetTimestamp() - start);
        if (!Index(i, Kinds + 1) || !Assert(start > 0)) return;
        if (kind == Kind.FrameWait && ticks < StutterTicks) Bound.Add(0, ticks);
        else Tallied.Add(kind == Kind.FrameWait ? (int)Kind.GpuWait : i, ticks);
    }

    public static string Report() => Tallied.Report("stutters") + Bound.Report("GPU-bound frame waits");
}

// Per kind: count, total, longest, for the log report (Hitches, Builds). Not thread-safe: owners lock.
internal sealed class KindTimes(string[] names)
{
    private const int MaxKinds = 8;
    private readonly long[] _counts = new long[names.Length], _ticks = new long[names.Length], _longest = new long[names.Length];

    public void Add(int kind, long ticks)
    {
        if (!Index(kind, Math.Min(_counts.Length, MaxKinds)) || !Assert(ticks >= 0)) return;
        (_counts[kind], _ticks[kind], _longest[kind]) = (_counts[kind] + 1, _ticks[kind] + ticks, Math.Max(_longest[kind], ticks));
    }

    // "; header: 3 ports 12.0 ms (longest 6.0), ...", empty while nothing came
    public string Report(string header)
    {
        var (ms, seen) = (FrameClock.TickMs, new List<string>(MaxKinds));
        for (var i = 0; i < Math.Min(names.Length, MaxKinds); i++)
            if (_counts[i] > 0)
                seen.Add($"{_counts[i]} {names[i]} {_ticks[i] * ms:0.0} ms (longest {_longest[i] * ms:0.0})");
        return seen.Count == 0 ? "" : $"; {header}: " + string.Join(", ", seen);
    }
}
