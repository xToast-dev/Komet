using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;

namespace Komet.Diagnostics;

internal sealed partial class ResourceStats
{
    private const long Mb = 1024 * 1024;
    private const int MaxMeminfoLines = 128, MaxStatFields = 32;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private bool _failed;
    private long _lastAllocated;
    private ulong _lastBusy, _lastTotal;
    private TimeSpan _lastCpu;
    private int _lastGen0, _lastGen2;
    private TimeSpan _lastPause;

    public double CpuPercent { get; private set; }
    public long WorkingSetMb { get; private set; }
    public long ManagedUsedMb { get; private set; }
    public long ManagedCommittedMb { get; private set; }
    public long NativeMb { get; private set; }
    public double Gen0PerSec { get; private set; }
    public double Gen2PerSec { get; private set; }
    public double GcPausePercent { get; private set; } // wall time the collector stopped every thread
    public double AllocatedMbPerSec { get; private set; } // what drives that pause, all threads together

    public double SystemCpuPercent { get; private set; }
    public long TotalRamMb { get; private set; }
    public long AvailableRamMb { get; private set; }
    public long UsedRamMb => TotalRamMb - AvailableRamMb;

    public double PercentOfRam(long mb)
    {
        return Assert(mb >= 0) && TotalRamMb > 0 ? 100.0 * mb / TotalRamMb : 0;
    }

    // Rethrows what the process handle or /proc throw, once: the values then stay as they were and later calls return at once
    public void Sample()
    {
        if (_failed) return;
        try
        {
            Measure();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Win32Exception or NotSupportedException)
        {
            _failed = true;
            throw;
        }
    }

    private void Measure()
    {
        _process.Refresh();
        var seconds = _wall.Elapsed.TotalSeconds;
        _wall.Restart();
        if (!Assert(seconds > 0)) return;

        var cpu = _process.TotalProcessorTime;
        if (Assert(cpu >= _lastCpu))
            CpuPercent = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
        _lastCpu = cpu;

        Gen0PerSec = Rate(GC.CollectionCount(0), ref _lastGen0, seconds);
        Gen2PerSec = Rate(GC.CollectionCount(2), ref _lastGen2, seconds);
        var pause = GC.GetTotalPauseDuration();
        if (Assert(pause >= _lastPause)) GcPausePercent = (pause - _lastPause).TotalSeconds / seconds * 100;
        _lastPause = pause;
        var allocated = GC.GetTotalAllocatedBytes();
        if (Assert(allocated >= _lastAllocated))
            AllocatedMbPerSec = (double)(allocated - _lastAllocated) / Mb / seconds;
        _lastAllocated = allocated;

        WorkingSetMb = _process.WorkingSet64 / Mb;
        ManagedUsedMb = GC.GetTotalMemory(false) / Mb;
        ManagedCommittedMb = GC.GetGCMemoryInfo().TotalCommittedBytes / Mb;
        if (!Assert(WorkingSetMb > 0) || !Assert(ManagedCommittedMb > 0)) return;
        NativeMb = Math.Max(0, WorkingSetMb - ManagedCommittedMb);

        if (OperatingSystem.IsLinux()) SampleLinux();
        else if (OperatingSystem.IsWindows()) SampleWindows();
    }

    private static double Rate(int now, ref int last, double seconds)
    {
        var perSec = Assert(now >= last) && Assert(seconds > 0) ? (now - last) / seconds : 0;
        last = now;
        return perSec;
    }

    private void SampleLinux()
    {
        foreach (var line in File.ReadLines("/proc/meminfo").Bounded(MaxMeminfoLines))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) TotalRamMb = KbToMb(line);
            if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
            AvailableRamMb = KbToMb(line);
            break;
        }

        if (!Assert(TotalRamMb > 0) || !Assert(AvailableRamMb <= TotalRamMb)) return;

        using var stat = File.OpenText("/proc/stat");
        // cpu user nice system idle iowait …
        var fields = (stat.ReadLine() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!Assert(fields.Length >= 8) || !Assert(fields[0] == "cpu")) return;
        ulong total = 0, idle = 0;
        for (var i = 1; i < Math.Min(fields.Length, MaxStatFields); i++)
        {
            if (!Assert(ulong.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)))
                return;
            total += value;
            if (i is 4 or 5) idle += value; // idle and iowait
        }

        UpdateSystemCpu(total - idle, total);
    }

    private static long KbToMb(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!Assert(fields.Length >= 2)) return 0;
        return Assert(long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)) &&
               Assert(kb >= 0)
            ? kb / 1024
            : 0;
    }

    private void SampleWindows()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status) && Assert(status.ullTotalPhys > 0) &&
            Assert(status.ullAvailPhys <= status.ullTotalPhys))
            (TotalRamMb, AvailableRamMb) = ((long)(status.ullTotalPhys / Mb), (long)(status.ullAvailPhys / Mb));
        if (GetSystemTimes(out var idle, out var kernel, out var user) && Assert(kernel >= idle))
            UpdateSystemCpu(kernel + user - idle, kernel + user); // kernel includes idle
    }

    private void UpdateSystemCpu(ulong busy, ulong total)
    {
        if (!Assert(busy <= total)) return;
        if (Assert(total >= _lastTotal) && Assert(busy >= _lastBusy) && _lastTotal != 0 && total > _lastTotal)
            SystemCpuPercent = 100.0 * (busy - _lastBusy) / (total - _lastTotal);
        (_lastBusy, _lastTotal) = (busy, total);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;

        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual,
            ullAvailExtendedVirtual;
    }
}

// Timer query around the whole frame; time the GPU spends waiting for the CPU counts too. Begin/EndQuery run every frame and cost
// nothing to wait for, but every glGet* returns a value, which Mesa's glthread can only give after its driver thread has drained
// the queue: a sync of ~10 µs. So the result is read once per HUD interval, from the oldest of four queries: three frames old,
// flushed by the swaps since and long complete, so radeonsi answers it without flushing or waiting on the GPU.
internal sealed class GpuStats : IDisposable
{
    private const int Ring = 4, MaxExtensions = 4096;

    // GL_NVX_gpu_memory_info, KB. NVIDIA and Mesa both give the card's own VRAM as DEDICATED; TOTAL_AVAILABLE adds the GTT on Mesa,
    // which made "used" look like 21 GB on a 16 GB card. CURRENT_AVAILABLE is what the whole device leaves free, other processes included.
    private const GetPName Dedicated = (GetPName)0x9047, Available = (GetPName)0x9049, Evictions = (GetPName)0x904A;
    private readonly int[] _queries = new int[Ring];
    private int _frame, _lastEvictions = -1;
    private long _lastVramSample;
    private bool _open;
    private bool? _vram; // GL_NVX_gpu_memory_info is offered, looked up once

    public double Ms { get; private set; }
    public double VramUsedMb { get; private set; } = double.NaN;
    public double VramTotalMb { get; private set; } = double.NaN;

    // The driver moved buffers out of VRAM: a stall class of its own
    public double EvictionsPerSec { get; private set; } = double.NaN;

    public void Dispose()
    {
        if (_queries[0] != 0) GL.DeleteQueries(Ring, _queries);
        Array.Clear(_queries);
        _open = false;
    }

    public void Begin()
    {
        if (!Assert(!_open)) GL.EndQuery(QueryTarget.TimeElapsed); // End() of the previous frame was skipped
        if (_queries[0] == 0) GL.GenQueries(Ring, _queries);
        if (!Assert(_queries[0] != 0) || !Assert(_queries[Ring - 1] != 0)) return;
        GL.BeginQuery(QueryTarget.TimeElapsed, _queries[_frame % Ring]);
        _open = true;
    }

    public void End()
    {
        if (!_open) return;
        GL.EndQuery(QueryTarget.TimeElapsed);
        _open = false;
        _frame++;
        _ = Assert(_frame > 0);
    }

    // Once per interval. The frame in flight owns slot _frame % Ring; the oldest ended one is the slot the next Begin reuses.
    public void Sample()
    {
        if (_frame < Ring) return; // not three ended frames yet
        var oldest = _queries[(_frame + 1) % Ring];
        if (!Assert(oldest != 0)) return;
        GL.GetQueryObject(oldest, GetQueryObjectParam.QueryResultAvailable, out int ready);
        // After a stall the GPU is the slow side and the read would block the main thread
        if (!Assert(ready is 0 or 1) || ready == 0) return;
        GL.GetQueryObject(oldest, GetQueryObjectParam.QueryResult, out long nanoseconds);
        if (!Assert(nanoseconds is >= 0 and < 60_000_000_000)) return; // a frame never takes a minute
        Ms = nanoseconds / 1e6;
    }

    // Three more syncs, so only while the system panel shows the values (or a bench records them). Hidden, they are dropped: the
    // panel or a bench then starts from a fresh sample, not from stale values and an eviction rate across the gap.
    public void SampleVram(bool shown)
    {
        if (!shown)
        {
            (VramUsedMb, VramTotalMb, EvictionsPerSec, _lastEvictions) = (double.NaN, double.NaN, double.NaN, -1);
            return;
        }

        _vram ??= Offered("GL_NVX_gpu_memory_info");
        if (_vram == false) return;
        GL.GetInteger(Dedicated, out var totalKb);
        GL.GetInteger(Available, out var freeKb);
        GL.GetInteger(Evictions, out var evictions); // cumulative since the device came up
        if (!Assert(totalKb > 0) || !Assert(freeKb >= 0) || !Assert(freeKb <= totalKb)) return;
        (VramTotalMb, VramUsedMb) = (totalKb / 1024.0, (totalKb - freeKb) / 1024.0);
        var now = Stopwatch.GetTimestamp();
        var seconds = (now - _lastVramSample) / (double)Stopwatch.Frequency;
        if (_lastEvictions >= 0 && evictions >= _lastEvictions && seconds > 0)
            EvictionsPerSec = (evictions - _lastEvictions) / seconds;
        (_lastEvictions, _lastVramSample) = (Math.Max(0, evictions), now); // a wrap starts the count over
    }

    // The way ClientPlatformWindows tests its extensions; glGetError would take an error the engine left for its own CheckGlError
    internal static bool Offered(string extension)
    {
        var count = GL.GetInteger(GetPName.NumExtensions);
        if (!Assert(count > 0)) return false;
        for (var i = 0; i < Math.Min(count, MaxExtensions); i++)
            if (GL.GetString(StringNameIndexed.Extensions, i) == extension)
                return true;
        return false;
    }
}
