using Vintagestory.Client;

namespace Komet.Runtime.Diagnostics;

internal sealed class FrameStats
{
    private const int MaxHistory = 1_000_000;
    private readonly float[] _history, _sorted;
    private int _next, _drawCallsAtLastSample;
    private float _elapsed, _worstDt;

    public FrameStats(int historyLength)
    {
        HistoryLength = Assert(historyLength >= 1000) && Assert(historyLength <= MaxHistory) ? historyLength : 1000;   // 0.1 % lows need a thousand frames
        _history = new float[HistoryLength];
        _sorted = new float[HistoryLength];
    }

    public int Frames { get; private set; }
    public int HistoryLength { get; }
    public float Fps       => _elapsed == 0 ? 0 : Frames / _elapsed;
    public float AverageMs => Frames == 0 ? 0 : _elapsed / Frames * 1000f;
    public float WorstMs   => _worstDt * 1000f;
    public float Low1Fps { get; private set; }
    public float Low01Fps { get; private set; }
    public int DrawCallsPerFrame { get; private set; }
    public int RenderedTriangles { get; private set; }    // the engine zeroes both every frame and sets them once per second
    public int AvailableTriangles { get; private set; }

    public void Record(float deltaTime)
    {
        if (!Assert(deltaTime is >= 0 and < 10) || !Index(_next, HistoryLength)) return;
        Frames++;
        _elapsed += deltaTime;
        _worstDt = Math.Max(_worstDt, deltaTime);
        _history[_next] = deltaTime * 1000f;
        _next = (_next + 1) % HistoryLength;
        if (RuntimeStats.renderedTriangles != 0)  RenderedTriangles  = RuntimeStats.renderedTriangles;
        if (RuntimeStats.availableTriangles != 0) AvailableTriangles = RuntimeStats.availableTriangles;
    }

    // 0 = oldest frame
    public float HistoryMs(int index) => Index(index, HistoryLength) ? _history[(_next + index) % HistoryLength] : 0;

    public void SampleWindow()
    {
        var calls = RuntimeStats.drawCallsCount;   // Alt+F3 resets it to 0
        if (!Assert(calls >= 0) || !Assert(_sorted.Length == _history.Length)) return;
        if (calls < _drawCallsAtLastSample) _drawCallsAtLastSample = 0;
        if (Frames > 0) DrawCallsPerFrame = (calls - _drawCallsAtLastSample) / Frames;
        _drawCallsAtLastSample = calls;

        _history.CopyTo(_sorted, 0);
        Array.Sort(_sorted);
        Low1Fps = LowFps(_sorted.AsSpan(^Math.Max(1, HistoryLength / 100)));
        Low01Fps = LowFps(_sorted.AsSpan(^Math.Max(1, HistoryLength / 1000)));
    }

    private static float LowFps(ReadOnlySpan<float> worstMs)
    {
        if (!Assert(!worstMs.IsEmpty)) return 0;
        float sum = 0;
        for (var i = 0; i < Math.Min(worstMs.Length, MaxHistory); i++) sum += worstMs[i];
        return Assert(sum >= 0) && Finite(sum) && sum > 0 ? 1000f * worstMs.Length / sum : 0;
    }

    public void Reset() => (Frames, _elapsed, _worstDt) = (0, 0f, 0f);
}
