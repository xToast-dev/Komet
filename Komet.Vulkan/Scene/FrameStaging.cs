namespace Komet.Vulkan;

// Uploads past the frame's staging ring go into overflow buffers of at least ChunkBytes, shared by the frame's further
// overflow, each kept until the GPU finishes the last frame that took from it. One buffer per large upload used to run
// out at 64 in a burst (a handbook or creative page drawing hundreds of new items at once), and an upload into a lean
// texture or pool then was lost for good: OpenGL holds no copy to read back (the empty item icons, missing faces).
internal sealed class FrameStaging(VulkanDevice device, VulkanFrame frame) : IDisposable
{
    private const int MaxOwn = 64;
    private const ulong ChunkBytes = 16UL << 20;

    private readonly List<(HostBuffer Buffer, long Frame)> _own = [];

    // Takes that found no room even in a new overflow buffer (should stay 0)
    public long Refused { get; private set; }

    public HostBuffer? Take(int bytes, out long at, int align = 16)
    {
        at = -1;
        if (!Assert(bytes > 0)) return null;
        if (frame.Staging is { } uploads && (at = uploads.Take(bytes, out _, align)) >= 0) return uploads;
        // the newest overflow buffer first: its room is this frame's, it then lives until this frame is done
        if (_own.Count > 0 && _own[^1] is var (last, _) && (at = last.Take(bytes, out _, align)) >= 0)
        {
            _own[^1] = (last, frame.Number);
            return last;
        }

        if (_own.Count >= MaxOwn ||
            HostBuffer.Create(device, Math.Max((ulong)bytes + (ulong)align, ChunkBytes), bulk: true) is not { } own)
        {
            Refused++;
            return null;
        }

        _own.Add((own, frame.Number));
        at = own.Take(bytes, out _, align);
        return at >= 0 ? own : null;
    }

    public void Collect()
    {
        var kept = 0;
        for (var i = 0; i < Math.Min(_own.Count, MaxOwn); i++)
        {
            if (_own[i].Frame <= frame.Done) _own[i].Buffer.Dispose();
            else _own[kept++] = _own[i];
        }

        _own.RemoveRange(kept, _own.Count - kept);
    }

    public void Dispose()
    {
        _ = device.WaitIdle();
        foreach (var (buffer, _) in _own.Bounded(MaxOwn)) buffer.Dispose();
        _own.Clear();
    }
}
