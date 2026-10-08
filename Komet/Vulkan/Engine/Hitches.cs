using System.Diagnostics;

namespace Komet.Vulkan;

// The engine's thread only.
internal static class Hitches
{
    public enum Kind
    {
        Port,
        Pipeline,
        GpuWait,
        RecorderWait,
        BufferRead
    }

    private const int Kinds = 5;
    private static readonly Tally Tallied =
        new(["ports", "pipelines", "waits on the GPU", "waits on the recording thread", "buffers read back"]);

    public static long Now => Stopwatch.GetTimestamp();

    public static void Since(Kind kind, long start)
    {
        var i = (int)kind;
        if (!Index(i, Kinds) || !Assert(start > 0)) return;
        Tallied.Add(i, Stopwatch.GetTimestamp() - start);
    }

    public static string Report() => Tallied.Report("stutters");
}
