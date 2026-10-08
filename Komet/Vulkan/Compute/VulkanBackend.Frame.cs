namespace Komet.Vulkan;

// On a frame the passes record into the frame's open segment, posted to its recording thread like the draws around them, so
// compute and the draws it feeds share one command buffer and no handoff. Deleting waits for the GPU, which only a world's end or
// a switch does.
internal sealed unsafe partial class VulkanBackend
{
    private const int MaxOutside = 16;

    private readonly VulkanFrame? _frame;
    private readonly System.Func<int, SharedImage?>? _shared;
    private readonly Dictionary<SharedImage, int> _outside = [];
    private bool _disposed;

    private void Record(Action<IntPtr> record)
    {
        if (!NotNull(record) || !Assert(_handle != IntPtr.Zero) || _disposed) return;
        if (_frame is { } frame)
        {
            if (!frame.Compute(record)) Refused++; // an op left unrecorded is not reused: the collector takes it
            return;
        }

        var commands = Recording();
        if (commands != IntPtr.Zero) record(commands);
    }

    // Work the frame had no segment for, or an upload no memory was left for: its result is missing (a pyramid level, a pass)
    public long Refused { get; private set; }

    // An engine texture as this backend names it: on a frame, the frame's shared image of it (0 without one); otherwise the
    // name is taken as this backend's own, as the tests hand theirs in
    public int Engine(int texture)
    {
        if (!Assert(texture >= 0) || _shared is null) return texture;
        if (texture == 0 || _shared(texture) is not { } image) return 0;
        if (_outside.TryGetValue(image, out var known) && _images[known]?.Shared == image) return known;
        if (_outside.Count >= MaxOutside) // the frame's framebuffers were made anew: the handles of the old ones go
        {
            foreach (var old in _outside.Values.ToArray().Bounded(MaxOutside)) _images[old] = null;
            _outside.Clear();
        }

        var at = Free(_images);
        if (at == 0) return 0;
        _images[at] = new ImageSlot(image.Image, 0, image.Sampled, [image.View]) { Shared = image };
        _outside[image] = at;
        return Assert(image.Sampled != 0) ? at : 0;
    }

    // IGpuBackend.Staged: on a frame, a region of its staging copied into the buffer among its commands
    public byte* Staged(int buffer, int offsetBytes, int bytes)
    {
        if (_frame is null || !Index(buffer, MaxHandles) || _buffers[buffer] is not { } b ||
            !Assert(offsetBytes >= 0 && bytes > 0) || (ulong)offsetBytes + (ulong)bytes > b.Size ||
            Staging(bytes, out var at) is not { } staging) return null;

        Copied(staging.Buffer, b.Handle,
            new Vk.BufferCopy { SourceOffset = (ulong)at, TargetOffset = (ulong)offsetBytes, Size = (ulong)bytes });
        b.View = (b.Handle, 0, b.Size);
        return staging.Pointer(at);
    }

    // Before a delete: the GPU done with everything recorded (on a frame, the open segment submitted first)
    private void Idle()
    {
        if (_disposed || !Assert(_handle != IntPtr.Zero)) return;
        if (_frame is null)
        {
            Flush();
            return;
        }

        _frame.Close("Komet's GPU passes let go of a resource");
        _ = Assert(_device.WaitIdle());
    }
}
