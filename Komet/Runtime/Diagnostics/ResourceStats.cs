using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Komet.Runtime.Diagnostics;

internal sealed partial class ResourceStats
{
    private const long Mb = 1024 * 1024;
    private const int MaxMeminfoLines = 128, MaxStatFields = 32;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private TimeSpan _lastCpu;
    private ulong _lastBusy, _lastTotal;
    private int _lastGen0, _lastGen2;

    public double CpuPercent { get; private set; }
    public long WorkingSetMb { get; private set; }
    public long ManagedUsedMb { get; private set; }
    public long ManagedCommittedMb { get; private set; }
    public long NativeMb { get; private set; }
    public double Gen0PerSec { get; private set; }
    public double Gen2PerSec { get; private set; }

    public double SystemCpuPercent { get; private set; }
    public long TotalRamMb { get; private set; }
    public long AvailableRamMb { get; private set; }
    public long UsedRamMb => TotalRamMb - AvailableRamMb;
    public double PercentOfRam(long mb) => Assert(mb >= 0) && TotalRamMb > 0 ? 100.0 * mb / TotalRamMb : 0;

    public void Sample()
    {
        _process.Refresh();
        var seconds = _wall.Elapsed.TotalSeconds;
        _wall.Restart();
        if (!Assert(seconds > 0)) return;

        var cpu = _process.TotalProcessorTime;
        if (Assert(cpu >= _lastCpu)) CpuPercent = (cpu - _lastCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100;
        _lastCpu = cpu;

        Gen0PerSec = Rate(GC.CollectionCount(0), ref _lastGen0, seconds);
        Gen2PerSec = Rate(GC.CollectionCount(2), ref _lastGen2, seconds);

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
        var fields = (stat.ReadLine() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);   // cpu user nice system idle iowait …
        if (!Assert(fields.Length >= 8) || !Assert(fields[0] == "cpu")) return;
        ulong total = 0, idle = 0;
        for (var i = 1; i < Math.Min(fields.Length, MaxStatFields); i++)
        {
            if (!Assert(ulong.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))) return;
            total += value;
            if (i is 4 or 5) idle += value;   // idle and iowait
        }
        UpdateSystemCpu(total - idle, total);
    }

    private static long KbToMb(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!Assert(fields.Length >= 2)) return 0;
        return Assert(long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb)) && Assert(kb >= 0) ? kb / 1024 : 0;
    }

    private void SampleWindows()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref status) && Assert(status.ullTotalPhys > 0) && Assert(status.ullAvailPhys <= status.ullTotalPhys))
            (TotalRamMb, AvailableRamMb) = ((long)(status.ullTotalPhys / Mb), (long)(status.ullAvailPhys / Mb));
        if (GetSystemTimes(out var idle, out var kernel, out var user) && Assert(kernel >= idle))
            UpdateSystemCpu(kernel + user - idle, kernel + user);   // kernel includes idle
    }

    private void UpdateSystemCpu(ulong busy, ulong total)
    {
        if (!Assert(busy <= total)) return;
        if (!Assert(total >= _lastTotal) || !Assert(busy >= _lastBusy)) { (_lastBusy, _lastTotal) = (busy, total); return; }
        var dTotal = total - _lastTotal;
        if (_lastTotal != 0 && dTotal > 0) SystemCpuPercent = 100.0 * (busy - _lastBusy) / dTotal;
        (_lastBusy, _lastTotal) = (busy, total);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
