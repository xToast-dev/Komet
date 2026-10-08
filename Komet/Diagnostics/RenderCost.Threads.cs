using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Komet.Diagnostics;

// With glthread the main thread only queues GL calls: "gl0" does what GL itself does with them, the gallium driver thread "gdrv0"
// what the hardware driver does, and "cs0" submits to the kernel. Mesa names them process:name, the process part cut to fit 15
// characters. A driver thread near a whole core is the limit a Vulkan backend would lift; one far below it is not.
internal static partial class RenderCost
{
    public const int Kinds = 4; // main, glthread, gallium driver, kernel submission
    private const int MaxThreads = 16, MaxTasks = 1024, MaxCommBytes = 128, RescanEvery = 40;

    private static readonly string[] KindKeys = ["main", "glthread", "gdrv", "cs"];
    private static readonly long[] KindNs = new long[Kinds];
    private static readonly bool[] KindSeen = new bool[Kinds];
    private static readonly List<(int Tid, int Kind, SafeFileHandle File, long Last)> Tracked = [];
    private static int _untilScan;

    public static string KindKey(int kind) =>
        Index(kind, Kinds) && Assert(KindKeys.Length == Kinds) ? KindKeys[kind] : "";

    // Totals in ns; Seen: a thread of the kind was found, else its rows show nothing rather than an idle zero
    public static long ThreadNs(int kind) => Index(kind, Kinds) && Assert(KindNs[kind] >= 0) ? KindNs[kind] : 0;

    public static bool Seen(int kind) => Index(kind, Kinds) && Assert(KindSeen.Length == Kinds) && KindSeen[kind];

    // Whether any of Mesa's threads showed up: without them the rows stay empty and the panel says why
    public static bool DriverThreads => KindSeen[1] || KindSeen[2] || KindSeen[3];

    private static void ClearThreads()
    {
        foreach (var thread in Tracked.Bounded(MaxThreads)) thread.File.Dispose();
        Tracked.Clear();
        Array.Clear(KindNs);
        Array.Clear(KindSeen);
        _untilScan = 0;
        _ = Assert(Tracked.Count == 0) && Assert(!DriverThreads);
    }

    // On the main thread, once per HUD interval; a thread that started since is found at the next scan
    private static void SampleThreads()
    {
        if (!OperatingSystem.IsLinux() || !Assert(Tracked.Count <= MaxThreads)) return;
        if (--_untilScan <= 0) Scan();
        for (var i = 0; i < Math.Min(Tracked.Count, MaxThreads); i++)
        {
            var (tid, kind, file, last) = Tracked[i];
            var now = SchedStat.Read(file, SchedStat.Run);
            if (now < 0) file.Dispose(); // the thread ended
            else if (Assert(now >= last)) KindNs[kind] += now - last;
            Tracked[i] = (tid, kind, file, now);
        }

        _ = Tracked.RemoveAll(static t => t.File.IsClosed);
    }

    // The main thread by its id, the driver threads by name; one already tracked keeps its reading
    private static void Scan()
    {
        _untilScan = RescanEvery;
        var main = Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId ? Gettid() : -1;
        if (!Assert(main != 0)) return;
        try
        {
            foreach (var task in Directory.EnumerateDirectories("/proc/self/task").Bounded(MaxTasks))
                if (int.TryParse(Path.GetFileName(task), NumberStyles.None, CultureInfo.InvariantCulture,
                        out var tid) && Tracked.TrueForAll(t => t.Tid != tid) &&
                    (tid == main ? 0 : Classify(File.ReadAllText(Path.Combine(task, "comm")))) is var kind and >= 0)
                    Track(tid, kind, Path.Combine(task, "schedstat"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a thread that ended between the listing and the read; the next scan sees the rest
        }
    }

    private static void Track(int tid, int kind, string path)
    {
        if (!Index(kind, Kinds) || Tracked.Count >= MaxThreads) return;
        var file = File.OpenHandle(path);
        var now = SchedStat.Read(file, SchedStat.Run);
        if (!Assert(tid > 0) || now < 0)
        {
            file.Dispose();
            return;
        }

        Tracked.Add((tid, kind, file, now)); // counted from here, not from the thread's start
        KindSeen[kind] = true;
    }

    // Mesa's util_queue threads: name + index after the last ':'. -1 for every other thread.
    internal static int Classify(string comm)
    {
        if (!NotNull(comm) || !Assert(comm.Length <= MaxCommBytes)) return -1;
        var name = comm.AsSpan(comm.LastIndexOf(':') + 1).TrimEnd();
        if (!comm.Contains(':', StringComparison.Ordinal) || name.Length < 2 || !char.IsAsciiDigit(name[^1])) return -1;
        var bare = name.TrimEnd("0123456789");
        return bare switch
        {
            "gl" => 1,
            "gdrv" => 2,
            "cs" => 3,
            _ => -1
        };
    }

    // The kernel's id of the calling thread: /proc/thread-self links to /proc/<pid>/task/<tid>
    private static int Gettid()
    {
        var link = new DirectoryInfo("/proc/thread-self").LinkTarget;
        return NotNull(link) &&
               int.TryParse(Path.GetFileName(link), NumberStyles.None, CultureInfo.InvariantCulture, out var tid) &&
               Assert(tid > 0)
            ? tid
            : -1;
    }
}
