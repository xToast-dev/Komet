using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Komet.Core;

// On a CPU with few threads the main thread shares the cores with the integrated server's threads, the tessellation
// workers, relight, chunk compression, pathfinding, async particles and the driver's shader compilers, all at the same
// priority: at 4 threads frames averaged 14.2 ms (1 % low 18 fps) where 4 cores with 8 threads made 11.5 ms. Those
// background threads, found by name, run one step lower (Linux nice 5, Windows below normal), so the frame wins a busy
// core: 4 threads then made 11.7 ms and 23 fps, tessellation throughput unchanged (bench, view distance 1536). Threads
// appear late (the server's at a world's start), so the process is scanned every 5 s; each thread is lowered once.
// nice 5, not more: a deeply lowered thread holding the CPU at a GC is slow to suspend (measured at nice 19).
// Linux lets a process lower its threads but not raise them again: switched off, threads made from then on stay normal.
internal static partial class BackgroundPriority
{
    private const int Niceness = 5, EveryMs = 5000, MaxThreads = 1024, NameLength = 64;
    private const int BelowNormal = -1, QueryLimited = 0x0800, SetLimited = 0x0400;

    private static readonly Regex Background = new(
        "^(SingleplayerSer|physicsManager|UdpSending|tesselateterrai|snowlayerScanne|ServerBlockTick|Relight|relight|" +
        "komet-worker|CompressChunks|compresschunks|collecttextures|clientPacketsPa|chunkvis|chunkdbthread|CheckLeafDecay|" +
        "blockticking|asyncparticles|ai-pathfinding|Vintage:disk|Vintag:sh_opt|Vintagesto:sh)", RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    private static readonly HashSet<long> Lowered = [];
    private static Timer? _timer;

    public static bool Enabled { get; set; } = true;

    public static void Start()
    {
        Stop();
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        _timer = new Timer(static _ => Scan(), null, 0, EveryMs);
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (Lowered)
            Lowered.Clear();
    }

    private static void Scan()
    {
        if (!Enabled) return;
        try
        {
            if (OperatingSystem.IsLinux()) ScanLinux();
            else if (OperatingSystem.IsWindows()) ScanWindows();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            Enabled = false; // the platform does not let us: everything stays at normal priority
        }
        catch (Exception e) when (e is RegexMatchTimeoutException or InvalidOperationException)
        {
            // a name match outlasted its timeout (a GC pause) or Windows listed no threads: the next scan tries again.
            // Thrown on, on the timer's thread, it would end the game
        }
    }

    private static void ScanLinux()
    {
        var tasks = Directory.GetDirectories("/proc/self/task");
        if (!Assert(tasks.Length <= MaxThreads * 4)) return;
        Span<char> buffer = stackalloc char[NameLength];
        foreach (var task in tasks.Bounded(MaxThreads))
        {
            if (!long.TryParse(Path.GetFileName(task), out var tid) || Seen(tid)) continue;
            bool background;
            try
            {
                background = Background.IsMatch(Comm(task, buffer));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // the thread ended meanwhile
            }

            if (background && ThreadNice.Lower(tid, Niceness)) Lower(tid);
        }
    }

    // The thread's name from its comm file, trimmed, read into name without a stream's buffers: every thread not lowered
    // is read again each scan (one may be named after it starts), 2 MB per 20 s through File.ReadAllText
    private static ReadOnlySpan<char> Comm(string task, Span<char> name)
    {
        Span<byte> bytes = stackalloc byte[NameLength];
        using var handle = File.OpenHandle(Path.Join(task, "comm"));
        var read = Math.Clamp(RandomAccess.Read(handle, bytes, 0), 0, Math.Min(NameLength, name.Length));
        var chars = System.Text.Encoding.UTF8.GetChars(bytes[..read], name);
        return name[..chars].Trim();
    }

    private static void ScanWindows()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var thread in process.Threads.Cast<System.Diagnostics.ProcessThread>().Bounded(MaxThreads))
        {
            var id = thread.Id;
            thread.Dispose();
            if (Seen(id)) continue;
            var handle = OpenThread(QueryLimited | SetLimited, false, (uint)id);
            if (handle == IntPtr.Zero) continue;
            try
            {
                if (GetThreadDescription(handle, out var text) >= 0 && text != IntPtr.Zero)
                {
                    var name = Marshal.PtrToStringUni(text, Math.Max(0, Math.Min(NameLength, Length(text))));
                    _ = LocalFree(text);
                    if (Background.IsMatch(name) && SetThreadPriority(handle, BelowNormal)) Lower(id);
                }
            }
            finally
            {
                _ = CloseHandle(handle);
            }
        }
    }

    // UTF-16 characters before the terminator, at most NameLength
    private static int Length(IntPtr text)
    {
        for (var n = 0; n < NameLength; n++)
            if (Marshal.ReadInt16(text, 2 * n) == 0)
                return n;
        return NameLength;
    }

    // A thread name of the background work lowered (tests)
    internal static bool IsBackground(string name) => NotNull(name) && Background.IsMatch(name);

    private static bool Seen(long id)
    {
        lock (Lowered) return Lowered.Contains(id);
    }

    private static void Lower(long id)
    {
        lock (Lowered)
            if (Lowered.Count < MaxThreads * 4)
                _ = Lowered.Add(id);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenThread", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr OpenThread(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadDescription")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetThreadDescription(IntPtr thread, out IntPtr description);

    [LibraryImport("kernel32.dll", EntryPoint = "SetThreadPriority", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadPriority(IntPtr thread, int priority);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr LocalFree(IntPtr memory);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

// Linux priority of one thread: Thread.Priority is a no-op there, setpriority on a thread id lowers just that thread.
// Lowering needs no rights, raising again does (CAP_SYS_NICE). Callers catch DllNotFound/EntryPointNotFound for a libc
// without the symbols.
internal static partial class ThreadNice
{
    private const int PrioProcess = 0, MaxNice = 19;

    // The calling thread's id
    public static int Current() => GetTid();

    // True when the thread runs at nice from now on
    public static bool Lower(long tid, int nice)
    {
        if (!Assert(tid is > 0 and <= uint.MaxValue) || !Assert(nice is > 0 and <= MaxNice)) return false;
        return SetPriority(PrioProcess, (uint)tid, nice) == 0;
    }

    [LibraryImport("libc", EntryPoint = "gettid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetTid();

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetPriority(int which, uint who, int value);
}
