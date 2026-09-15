using OpenTK.Graphics.OpenGL;

namespace Komet.Runtime.Diagnostics;

// Timer query around the whole frame, result one frame later. Time the GPU spends waiting for the CPU counts too.
internal sealed class GpuStats : IDisposable
{
    private readonly int[] _queries = new int[2];
    private int _frame;
    private bool _open, _vram = true;

    public double Ms { get; private set; }
    public double VramUsedMb { get; private set; } = double.NaN;
    public double VramTotalMb { get; private set; } = double.NaN;

    public void Begin()
    {
        if (!Assert(!_open)) GL.EndQuery(QueryTarget.TimeElapsed);   // End() of the previous frame was skipped
        if (_queries[0] == 0) GL.GenQueries(2, _queries);
        if (!Assert(_queries[0] != 0) || !Assert(_queries[1] != 0)) return;
        GL.BeginQuery(QueryTarget.TimeElapsed, _queries[_frame & 1]);
        _open = true;
    }

    public void End()
    {
        if (!_open) return;
        GL.EndQuery(QueryTarget.TimeElapsed);
        _open = false;
        _frame++;
        if (_frame == 1) return;
        var previous = _queries[_frame & 1];
        if (!Assert(previous != 0)) return;
        GL.GetQueryObject(previous, GetQueryObjectParam.QueryResultAvailable, out int ready);
        if (ready == 0) return;
        GL.GetQueryObject(previous, GetQueryObjectParam.QueryResult, out long nanoseconds);
        if (!Assert(nanoseconds is >= 0 and < 60_000_000_000)) return;   // a frame never takes a minute
        Ms = nanoseconds / 1e6;
    }

    public void SampleVram()
    {
        if (!_vram) return;
        GL.GetInteger((GetPName)0x9048, out int totalKb);   // GL_NVX_gpu_memory_info: NVIDIA and Mesa
        GL.GetInteger((GetPName)0x9049, out int freeKb);
        _vram = GL.GetError() == ErrorCode.NoError;
        if (!_vram || !Assert(totalKb > 0) || !Assert(freeKb >= 0) || !Assert(freeKb <= totalKb)) return;
        (VramTotalMb, VramUsedMb) = (totalKb / 1024.0, (totalKb - freeKb) / 1024.0);
    }

    public void Dispose()
    {
        if (_queries[0] != 0) GL.DeleteQueries(2, _queries);
        Array.Clear(_queries);
        _open = false;
    }
}
