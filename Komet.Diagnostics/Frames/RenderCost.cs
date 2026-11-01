using HarmonyLib;
using Komet.Core;
using Microsoft.Win32.SafeHandles;
using OpenTK.Graphics.OpenGL;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Vintagestory.Client.NoObf;
using Komet.Api;

namespace Komet.Diagnostics;

// Terrain draw cost (GPU time includes waiting for the call's commands); Vulkan terrain fills the GPU rows itself.
internal static partial class RenderCost
{
    public const int Groups = 3; // shadows, opaque, transparent (the liquid depth pre-pass OnRenderBefore, OIT and after OIT)
    private const int MaxCalls = 16, Ring = 4;

    private static readonly string[] GroupKeys = ["shadows", "opaque", "transparent"];
    private static readonly long[] CpuTicks = new long[Groups];
    private static readonly double[] GpuGroupMs = new double[Groups];

    private static readonly int[] Queries = new int[Ring * MaxCalls * 2];
    private static readonly int[] CallGroups = new int[Ring * MaxCalls];
    private static readonly int[] Calls = new int[Ring];

    private static Harmony? _harmony;
    private static ILogger? _logger;
    private static int _depth, _group, _frame;
    private static long _entered, _submitStart;
    private static bool _timing; // the call under way has its first timestamp
    private static bool _vulkan;

    // First true patches the engine, false leaves the patches inert; turned on, the threads' run time counts from then
    public static bool Enabled
    {
        get;
        set
        {
            if (value && !field) Rebase();
            field = value;
            if (value) Patch();
        }
    }

    public static bool Patched { get; private set; }

    public static long SubmitTicks { get; private set; }
    public static long Draws { get; private set; }
    public static long Ranges { get; private set; }
    public static long TotalTicks => CpuTicks[0] + CpuTicks[1] + CpuTicks[2];

    // One frame three frames back; NaN until read
    public static double GpuMs { get; private set; } = double.NaN;

    public static double FrameGpuMs(double gl) =>
        Links.VulkanFrameGpuMs() is var ms && !double.IsNaN(ms) && Assert(ms >= 0) ? ms : gl;

    public static string GroupKey(int group) =>
        Index(group, Groups) ? GroupKeys[group] : "";

    public static long GroupTicks(int group) =>
        Index(group, Groups) ? CpuTicks[group] : 0;

    public static double GroupGpuMs(int group) =>
        Index(group, Groups) ? GpuGroupMs[group] : double.NaN;

    public static void Install(Harmony harmony, ILogger logger)
    {
        Clear();
        (_harmony, _logger, Patched) = (null, null, false);
        if (!NotNull(harmony) || !NotNull(logger)) return;
        (_harmony, _logger) = (harmony, logger);
    }

    public static void Clear()
    {
        Enabled = false;
        if (Queries[0] != 0) GL.DeleteQueries(Queries.Length, Queries);
        Array.Clear(Queries);
        Array.Clear(Calls);
        Array.Clear(CpuTicks);
        Array.Fill(GpuGroupMs, double.NaN);
        (_depth, _frame, _timing, _vulkan, _submitStart, GpuMs) = (0, 0, false, false, 0, double.NaN);
        (SubmitTicks, Draws, Ranges) = (0, 0, 0);
        ClearThreads();
    }

    private static void Patch()
    {
        if (Patched || _harmony is null || !NotNull(_logger)) return;
        Patched = true;
        var type = typeof(ChunkRenderer);
        var render = AccessTools.Method(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.RenderMesh),
            [typeof(MeshRef), typeof(int[]), typeof(int[]), typeof(int), typeof(bool)]);
        string[] stages =
        [
            "OnRenderBefore", nameof(ChunkRenderer.RenderShadow), "OnBeforeRenderOpaque",
            nameof(ChunkRenderer.RenderOpaque), "RenderOIT", "RenderAfterOIT"
        ];
        try
        {
            foreach (var method in stages.Bounded(MaxCalls))
                Around(AccessTools.DeclaredMethod(type, method, [typeof(float)]), nameof(Enter), nameof(Leave));
            if (NotNull(render) && Assert(Il.Binds(render, AccessTools.Method(typeof(RenderCost), nameof(Submitted)))))
                Around(render, nameof(Submitting), nameof(Submitted));
        }
        catch (Exception e) when (e is HarmonyException or ArgumentException or InvalidOperationException
                                      or NotSupportedException)
        {
            _logger.Warning("Komet: the render cost could not patch the engine ({0})", e.Message);
        }
    }

    // First prefix and last postfix: covers every other patch
    private static void Around(MethodInfo? target, string prefix, string postfix)
    {
        if (!NotNull(target) || !NotNull(_harmony)) return;
        var self = typeof(RenderCost);
        _ = _harmony.Patch(target,
            new HarmonyMethod(AccessTools.Method(self, prefix)) { priority = Priority.First },
            new HarmonyMethod(AccessTools.Method(self, postfix)) { priority = Priority.Last });
    }

    private static void Enter(MethodBase __originalMethod)
    {
        if (!Enabled || _depth++ > 0 || !NotNull(__originalMethod)) return;
        var group = __originalMethod.Name switch
        {
            nameof(ChunkRenderer.RenderShadow) => 0,
            nameof(ChunkRenderer.RenderOpaque) or "OnBeforeRenderOpaque" => 1,
            _ => 2
        };
        (_group, _entered) = (group, Stopwatch.GetTimestamp());
        var slot = _frame % Ring;
        _timing = Calls[slot] < MaxCalls && !_vulkan;
        if (!_timing) return;
        if (Queries[0] == 0) GL.GenQueries(Queries.Length, Queries);
        var call = slot * MaxCalls + Calls[slot];
        CallGroups[call] = group;
        GL.QueryCounter(Queries[2 * call], QueryCounterTarget.Timestamp);
    }

    // Skipped on exception; EndFrame closes what was left open
    private static void Leave()
    {
        if (!Enabled || _depth == 0 || --_depth > 0) return;
        CpuTicks[_group] += Stopwatch.GetTimestamp() - _entered;
        if (!_timing) return;
        var slot = _frame % Ring;
        GL.QueryCounter(Queries[2 * (slot * MaxCalls + Calls[slot]) + 1], QueryCounterTarget.Timestamp);
        Calls[slot]++;
        _timing = false;
    }

    private static void Submitting()
    {
        if (!Enabled || _depth == 0) return;
        _submitStart = Stopwatch.GetTimestamp();
    }

    private static void Submitted(int groupCount)
    {
        if (_submitStart == 0) return;
        var now = Stopwatch.GetTimestamp();
        if (Assert(groupCount >= 0))
            (SubmitTicks, Draws, Ranges) = (SubmitTicks + now - _submitStart, Draws + 1, Ranges + groupCount);
        _submitStart = 0;
    }

    public static void EndFrame()
    {
        if (!Enabled) return;
        if (_depth != 0) (_depth, _timing, _submitStart) = (0, false, 0); // a call threw past its postfix
        _frame++;
        Calls[_frame % Ring] = 0;
        _vulkan = Links.VulkanTimesTerrain() || KometShaders.VulkanDraws(); // the next frame's calls
    }

    // Once per HUD interval from the oldest ended frame: one glthread sync, later reads find the queue empty
    public static void Sample()
    {
        if (!Enabled) return;
        SampleThreads();
        Span<double> ms = stackalloc double[Groups];
        if (Links.VulkanTerrainGpuMs(ms))
        {
            Took(ms);
            return;
        }

        if (_frame < Ring || Queries[0] == 0) return;
        var slot = (_frame + 1) % Ring;
        var calls = Math.Min(Calls[slot], MaxCalls);
        if (calls == 0) return;
        GL.GetQueryObject(Queries[2 * (slot * MaxCalls + calls - 1) + 1], GetQueryObjectParam.QueryResultAvailable,
            out int ready);
        if (!Assert(ready is 0 or 1) || ready == 0) return; // the GPU lags: the read would block
        for (var i = 0; i < Math.Min(calls, MaxCalls); i++)
        {
            var call = slot * MaxCalls + i;
            GL.GetQueryObject(Queries[2 * call], GetQueryObjectParam.QueryResult, out long begin);
            GL.GetQueryObject(Queries[2 * call + 1], GetQueryObjectParam.QueryResult, out long end);
            if (!Assert(end >= begin)) return;
            ms[CallGroups[call]] += (end - begin) / 1e6;
        }

        Took(ms);
    }

    private static void Took(ReadOnlySpan<double> ms)
    {
        ms.CopyTo(GpuGroupMs);
        GpuMs = ms[0] + ms[1] + ms[2];
    }
}

// glthread: "gl0" executes GL calls, "gdrv0" is the gallium driver, "cs0" submits (Mesa process:name, cut to 15 chars).
// A driver thread near a whole core is what Vulkan would lift.
internal static partial class RenderCost
{
    public const int Kinds = 4;
    private const int MaxThreads = 16, MaxTasks = 1024, MaxCommBytes = 128, RescanEvery = 40;
    private const int ForgetEvery = 25, MaxKindless = 4096;

    private static readonly string[] KindKeys = ["main", "glthread", "gdrv", "cs"];
    private static readonly long[] KindNs = new long[Kinds];
    private static readonly bool[] KindSeen = new bool[Kinds];
    private static readonly List<(int Tid, int Kind, SafeFileHandle File, long Last)> Tracked = [];

    // Threads of no kind, not read again until the set is dropped every ForgetEvery scans (a thread named late is found then)
    private static readonly HashSet<int> Kindless = [];
    private static int _untilScan, _scans, _main;

    public static string KindKey(int kind) =>
        Index(kind, Kinds) ? KindKeys[kind] : "";

    // Unseen kinds show nothing rather than an idle zero
    public static long ThreadNs(int kind) => Index(kind, Kinds) ? KindNs[kind] : 0;

    public static bool Seen(int kind) => Index(kind, Kinds) && KindSeen[kind];

    public static bool DriverThreads => KindSeen[1] || KindSeen[2] || KindSeen[3];

    private static void ClearThreads()
    {
        foreach (var thread in Tracked.Bounded(MaxThreads)) thread.File.Dispose();
        Tracked.Clear();
        Array.Clear(KindNs);
        Array.Clear(KindSeen);
        Kindless.Clear();
        (_untilScan, _scans, _main) = (0, 0, 0);
    }

    private static void SampleThreads()
    {
        if (!OperatingSystem.IsLinux()) return;
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

    // The tracked threads' run time counts from now: what they ran while nobody sampled belongs to no interval
    public static void Rebase()
    {
        if (!OperatingSystem.IsLinux()) return;
        for (var i = 0; i < Math.Min(Tracked.Count, MaxThreads); i++)
        {
            var (tid, kind, file, last) = Tracked[i];
            var now = SchedStat.Read(file, SchedStat.Run);
            Tracked[i] = (tid, kind, file, now >= 0 && Assert(now >= last) ? now : last); // an ended one goes at the next sample
        }
    }

    private static void Scan()
    {
        _untilScan = RescanEvery;
        if (++_scans % ForgetEvery == 0 || Kindless.Count >= MaxKindless) Kindless.Clear();
        var onMain = Environment.CurrentManagedThreadId == RuntimeEnv.MainThreadId;
        if (onMain && _main == 0) _main = Gettid(); // the main thread's id holds for the process
        var main = onMain ? _main : -1;
        try
        {
            foreach (var task in Directory.EnumerateDirectories("/proc/self/task").Bounded(MaxTasks))
            {
                if (!int.TryParse(Path.GetFileName(task), NumberStyles.None, CultureInfo.InvariantCulture, out var tid) ||
                    Tracking(tid) || Kindless.Contains(tid)) continue;
                var kind = tid == main ? 0 : Classify(File.ReadAllText(Path.Join(task, "comm")));
                if (kind >= 0) Track(tid, kind, Path.Join(task, "schedstat"));
                else _ = Kindless.Add(tid);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a thread that ended between the listing and the read
        }
    }

    private static bool Tracking(int tid)
    {
        foreach (var thread in Tracked.Bounded(MaxThreads))
            if (thread.Tid == tid)
                return true;
        return false;
    }

    private static void Track(int tid, int kind, string path)
    {
        if (Tracked.Count >= MaxThreads) return;
        var now = -1L;
        var file = Owned.Kept(File.OpenHandle(path), 0, (file, _) =>
        {
            now = SchedStat.Read(file, SchedStat.Run);
            return now < 0 ? "unreadable" : null;
        }, out _);
        if (file is null) return;

        Tracked.Add((tid, kind, file, now)); // counted from here, not from the thread's start
        KindSeen[kind] = true;
    }

    // Mesa util_queue threads: name + index after the last ':'; -1 otherwise.
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

    // /proc/thread-self links to /proc/<pid>/task/<tid>
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
