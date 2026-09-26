using System.Diagnostics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Diagnostics;

// Timer query around the whole frame; time the GPU spends waiting for the CPU counts too. Begin/EndQuery run every frame and cost
// nothing to wait for, but every glGet* returns a value, which Mesa's glthread can only give after its driver thread has drained
// the queue: a sync of ~10 µs. So the result is read once per HUD interval, from the oldest of four queries: three frames old,
// flushed by the swaps since and long complete, so radeonsi answers it without flushing or waiting on the GPU.
internal sealed class GpuStats : IDisposable
{
    private const int Ring = 4, MaxExtensions = 1024;
    private const GetPName NumExtensions = (GetPName)0x821D;
    private const StringNameIndexed Extensions = (StringNameIndexed)0x1F03;

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

    public double EvictionsPerSec { get; private set; } =
        double.NaN; // the driver moved buffers out of VRAM: a stall class of its own

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
        if (!Assert(ready is 0 or 1) || ready == 0)
            return; // after a stall the GPU is the slow side and the read would block the main thread
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
    private static bool Offered(string extension)
    {
        var count = GL.GetInteger(NumExtensions);
        if (!Assert(count >= 0)) return false;
        for (var i = 0; i < Math.Min(count, MaxExtensions); i++)
            if (GL.GetString(Extensions, i) == extension)
                return true;
        return false;
    }
}
