using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using Komet.Core;
using Komet.Api;

namespace Komet.Diagnostics;

// Any glGet* forces a glthread drain (~10 µs), so the frame's timer query is read once per HUD interval from the oldest
// of four: long complete, radeonsi answers without flushing.
internal sealed class GpuStats : IDisposable
{
    private const int Ring = 4;

    // DEDICATED is the card's VRAM; TOTAL_AVAILABLE adds GTT on Mesa (21 GB "used" on 16 GB). CURRENT_AVAILABLE:
    // device-free.
    private const GetPName Dedicated = (GetPName)0x9047, Available = (GetPName)0x9049, Evictions = (GetPName)0x904A;
    private readonly int[] _queries = new int[Ring];
    private int _frame, _lastEvictions = -1;
    private long _lastVramSample;
    private bool _open;
    private bool? _vram;
    private static int _glVersion;

    public double Ms { get; private set; }
    public double VramUsedMb { get; private set; } = double.NaN;
    public double VramTotalMb { get; private set; } = double.NaN;
    public double EvictionsPerSec { get; private set; } = double.NaN;

    internal static int GlVersion
    {
        get
        {
            if (_glVersion == 0)
                _glVersion = 10 * GL.GetInteger(GetPName.MajorVersion) + GL.GetInteger(GetPName.MinorVersion);
            return _glVersion;
        }
    }

    public void Dispose()
    {
        if (_queries[0] != 0) GL.DeleteQueries(Ring, _queries);
        Array.Clear(_queries);
        _open = false;
    }

    public void Begin()
    {
        if (KometShaders.VulkanDraws()) return;
        if (_open) GL.EndQuery(QueryTarget.TimeElapsed); // the collectors went on mid-frame: the previous End() never ran
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
    }

    public void Sample()
    {
        if (_frame < Ring || KometShaders.VulkanDraws()) return; // not three ended frames yet
        var oldest = _queries[(_frame + 1) % Ring];
        if (!Assert(oldest != 0)) return;
        GL.GetQueryObject(oldest, GetQueryObjectParam.QueryResultAvailable, out int ready);
        // After a stall the read would block the main thread
        if (!Assert(ready is 0 or 1) || ready == 0) return;
        GL.GetQueryObject(oldest, GetQueryObjectParam.QueryResult, out long nanoseconds);
        if (!Assert(nanoseconds is >= 0 and < 60_000_000_000)) return; // a frame never takes a minute
        Ms = nanoseconds / 1e6;
    }

    // Three more syncs: only while shown or benched; hidden, they reset (no stale eviction rate).
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

    internal static bool Offered(string extension) => NotNull(extension) && GlExtensions.Offered(extension);
}
