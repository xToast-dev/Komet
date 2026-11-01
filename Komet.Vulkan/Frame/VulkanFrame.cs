using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// A frame is a few segments of Vulkan's own work, each a submission of a setup (transfers) and its commands; they hold
// the images they use and put them back to rest as they close. Slots are reused once the frame-end fence passes. Only the
// recording thread submits, in post order.
// Nothing is shared with GL, so no semaphores either way and GL calls never close a segment. The frame is one segment
// from Vulkan's first work to present, split only by Vulkan's own needs (a query result, a resource released, a new
// image when full); each setup then starts with a full memory barrier instead of a semaphore.
internal sealed unsafe partial class VulkanFrame : IDisposable
{
    // MaxSegments only bounds a slot's command buffers, far past any frame's: a frame can always be shown. MaxLoose:
    // transfers outside any segment (Alone); MaxImages: handed with each segment (the targets' 80 and more); MaxHeld: the
    // images a segment holds, which is the frame's.
    public const int Slots = 3, MaxSegments = 1024, MaxLoose = 32, MaxImages = 1152, MaxHeld = 4096;
    private const ulong GpuTimeoutNs = 5_000_000_000; // a GPU that takes longer is hung, or waits on OpenGL that waits on it
    private const ulong UploadBytes = 32UL << 20;
    private const int MaxClosers = 64;

    private readonly VulkanDevice _device;
    private readonly List<Slot> _slots = [];
    private Slot? _slot;
    private Segment? _open;
    private int _next;
    private bool _made; // the recording thread's: the last segment's submission went through

    // Frame ended, next not begun: work in between (GUI, Before's first renderers) begins the next frame on its first
    // segment, so nothing records into a shown slot and Start finds it begun (Fresh)
    private bool _ended;
    private bool _beginning; // Next runs for Early: what it ends (Ending) must not begin a frame again

    private sealed class Segment(IntPtr commands, IntPtr setup)
    {
        public IntPtr Commands { get; } = commands; // the recording thread's: the draws
        public Handoff.Image[] Released { get; set; } = [];
        public IntPtr Setup { get; } = setup; // transfers that run before the segment's draws
    }

    private sealed class Slot(HostBuffer uploads, HostBuffer staging, ulong fence)
    {
        public HostBuffer Uploads { get; } = uploads;
        public HostBuffer Staging { get; } = staging; // bulk: only copied from
        public ulong Fence { get; } = fence;
        public List<Segment> Segments { get; } = [];
        public int Used { get; set; } // segments begun this frame
        public bool InFlight { get; set; } // the recording thread's, once it reached Signalled
        public long Signalled { get; set; } // the ops posted up to the fence's submission
        public Action? Signal { get; set; }

        // Transfers outside any segment (Alone)
        public List<IntPtr> Loose { get; } = [];
        public List<Action> LooseSubmits { get; } = []; // each loose buffer's submission, beside it
        public int LooseUsed { get; set; }

        public int Index { get; init; }
        public List<int> Stamps { get; } = []; // each timestamp's section, in query order
        public bool Timed { get; set; } // the frame begun in it writes timestamps (TimedFrame)

        // Pipeline statistics: the frame is counted, its queries were reset, how many it began
        public bool Sampled { get; set; }
        public bool Reset { get; set; }
        public int Queries { get; set; }
    }

    // An image held by a segment: at rest between segments in its resting layout
    public readonly record struct Shared(SharedImage Image, int Resting);

    private VulkanFrame(VulkanDevice device)
    {
        _device = device;
    }

    public Func<IReadOnlyList<Shared>> Images { get; set; } = () => [];

    // Run on segment open once its setup takes transfers, and at frame end with none open. Filling: the renderer's own,
    // before Opened
    public Action? Opened { get; set; }
    public Action? Ending { get; set; }
    public Action? Filling { get; set; }

    public bool Open => _open is not null;
    public IntPtr Setup => _open?.Setup ?? IntPtr.Zero;

    public long Serial { get; private set; }

    public HostBuffer? Uploads => _slot?.Uploads;

    // HostBuffer's bulk kind: the GPU copies it elsewhere before reading it
    public HostBuffer? Staging => _slot?.Staging;

    public long Frames { get; private set; }
    public long Number { get; private set; } // frames begun

    // The last frame the GPU finished for sure: the one whose slot the frame begun last waited for
    public long Done => Number - Slots;
    public long Segments { get; private set; }
    public long SubmitTicks { get; private set; }

    public Dictionary<string, long> Closers { get; } = new(StringComparer.Ordinal);

    public bool EarlyBegun { get; private set; }

    // Frames begun this way: from inside an OpenGL call (a draw between frames), the GPU wait for the slot included
    public long EarlyBegins { get; private set; }

    public static VulkanFrame? Create(VulkanDevice device, out string why)
    {
        why = "";
        if (!NotNull(device)) return null;
        var frame = Owned.Kept(new VulkanFrame(device), device, static (frame, device) => frame.Slotted(device), out why);
        if (frame is null) return null;
        frame.StartWorker();
        device.CatchUp = frame.CaughtUp;
        return frame;
    }

    // Each slot's upload buffers and fence; the reason when the device has none to give
    private string? Slotted(VulkanDevice device)
    {
        for (var i = 0; i < Slots; i++)
        {
            HostBuffer? uploads = null, staging = null;
            var fence = 0UL;
            try
            {
                (uploads, fence) = (HostBuffer.Create(device, UploadBytes), device.Fence(false));
                staging = HostBuffer.Create(device, UploadBytes, bulk: true);
            }
            catch
            {
                Owned.DropAll([uploads, staging]);
                device.DestroyFence(fence);
                throw;
            }

            if (uploads is not null && staging is not null && fence != 0)
            {
                _slots.Add(new Slot(uploads, staging, fence) { Index = i });
                continue;
            }

            Owned.DropAll([uploads, staging]);
            device.DestroyFence(fence);
            return "no memory for a frame's uploads, or no fence";
        }

        return null;
    }

    public void Next()
    {
        Close("the frame's end", endsFrame: true);
        if (_slot is not null) Ending?.Invoke(); // before the first frame there is nothing to end
        if (_slot is { } used && used.Used + used.LooseUsed > 0) // loose transfers too: their buffers are begun again
        {
            var (device, fence) = (_device, used.Fence);
            used.InFlight = false;
            Enqueue(new Op { Kind = OpKind.Queue, Ref = used.Signal ??= () => used.InFlight = device.Signal(fence) });
            used.Signalled = _posted;
            if (used.Used > 0) Frames++;
        }

        var slot = _slots[_next];
        _next = (_next + 1) % Slots;
        Number++;
        VulkanWatch.Mark("frame end: waiting for the GPU to finish the slot's last frame", Number);
        var finished = Fault.Length == 0 && Await(slot); // a hung GPU (Pace found it) is not waited for twice
        if (!finished) Hung(Number - Slots);
        Timed(slot, finished);
        CountedFrame(slot, finished);
        VulkanWatch.Mark("frame begun", Number);
        (slot.InFlight, slot.Used, slot.LooseUsed) = (false, 0, 0);
        slot.Timed = TimedFrame();
        slot.Uploads.Reset();
        slot.Staging.Reset();
        (_slot, _ended) = (slot, false);
    }

    // At frame start, before the engine reads its clock: wait for the GPU to finish the slot's previous frame (else
    // Next waits mid-frame). Here GPU-bound frames start as evenly as the GPU finishes; mid-frame the wait only absorbs
    // prior CPU work, so frame starts (engine dt, camera step) jitter by CPU variance while presents stay GPU-paced.
    public void Pace()
    {
        if (_slot is null || _open is not null || EarlyBegun || Fault.Length > 0) return;
        var slot = _slots[_next];
        if (Await(slot)) slot.InFlight = false;
        else Hung(Number + 1 - Slots);
    }

    // The slot's last frame done on the GPU (false: not within GpuTimeoutNs)
    private bool Await(Slot slot)
    {
        Reach(slot.Signalled); // frames ago: made long since
        if (!slot.InFlight) return true;
        var start = Hitches.Now;
        var finished = _device.Wait(slot.Fence, GpuTimeoutNs);
        Hitches.Since(Hitches.Kind.FrameWait, start);
        return finished;
    }

    private void Hung(long frame)
    {
        if (Fault.Length == 0) Fault = $"the GPU did not finish frame {frame} within 5 s";
    }

    public bool Begin(out string why)
    {
        why = Fault;
        if (Fault.Length > 0) return false;
        if (_open is not null) return true;

        if (_slot is null) Next();
        else Early();
        if (!NotNull(_slot)) return false;
        var slot = _slot;
        if (slot.Used >= MaxSegments)
        {
            why = "the frame has used all its segments";
            return false;
        }

        VulkanWatch.Mark("segment opening");
        var segment = slot.Used < slot.Segments.Count ? slot.Segments[slot.Used] : Made(slot, out why);
        if (segment is null || !VulkanDevice.Begin(segment.Setup))
        {
            if (why.Length == 0) why = "no command buffer";
            return false;
        }

        Acquire();
        Enqueue(new Op { Kind = OpKind.Begin, Ref = segment }); // before anything recorded into it
        Ordered(segment.Setup); // after everything before, transfers included
        _open = segment;
        Serial++;
        TimingBegins(segment); // before anything Opened records
        CountingBegins(segment);
        Filling?.Invoke();
        Opened?.Invoke();
        return true;
    }

    private Segment? Made(Slot slot, out string why)
    {
        why = "";
        Drain(); // the recording thread is done with its pool before a command buffer is made in it
        var (commands, setup) = (AllocateRecorded(), _device.Allocate());
        if (commands != IntPtr.Zero && setup != IntPtr.Zero)
        {
            var segment = new Segment(commands, setup);
            slot.Segments.Add(segment);
            return segment;
        }

        why = "no command buffer for a segment";
        return null;
    }

    // Submission is posted as the segment's last command: the recording thread ends the command buffer, submits, then
    // runs after, while the engine thread goes on. Other threads' queue operations wait for it (VulkanDevice.CatchUp),
    // for the submit only, not after (a present may wait for the display). A failed submission faults the frame.
    public void Close(string why, Action? after = null, bool endsFrame = false)
    {
        if (endsFrame && _slot is not null) _ended = true; // with no segment open too
        if (_open is not { } segment || _slot is not { } slot || !NotNull(why)) return;
        ref var closed = ref Closers.Count < MaxClosers // one lookup a segment; past MaxClosers only those already counted
            ? ref CollectionsMarshal.GetValueRefOrAddDefault(Closers, why, out _)
            : ref CollectionsMarshal.GetValueRefOrNullRef(Closers, why);
        if (!Unsafe.IsNullRef(ref closed)) closed++;
        EndRendering();
        Release(segment);
        Transfers(segment.Setup, false);
        TimingEnds(segment);
        var setupEnded = VulkanDevice.End(segment.Setup); // from the engine's thread's pool: ended here
        var showing = _showing;
        _showing = 0;
        Post(new Op { Kind = OpKind.Submit, Ref = segment, Indirect = showing, Count = setupEnded ? 1u : 0u });
        if (after is not null) Post(new Op { Kind = OpKind.After, Ref = after });
        (_open, _counting) = (null, -1); // a query is ended before the rendering is, unless the frame faulted
        Shown = showing != 0 && Fault.Length == 0;
        slot.Used++; // the command buffers were begun: the slot's fence covers them either way
        if (Fault.Length == 0) Segments++;
    }

    // On the recording thread
    private void Submitted(VulkanDevice device, (IntPtr Setup, IntPtr Commands) buffers, ulong signal, bool setupEnded)
    {
        _made = false;
        var start = Stopwatch.GetTimestamp();
        ReadOnlySpan<ulong> signals = signal == 0 ? [] : new ReadOnlySpan<ulong>(in signal);
        _made = setupEnded && Fault.Length == 0 && device.Submit([buffers.Setup, buffers.Commands], signals, 0,
            ended: 1);
        SubmitTicks += Stopwatch.GetTimestamp() - start;
        if (!_made && Fault.Length == 0) Fault = "a segment's submission failed (the device is lost?)";
    }

    private void After(Action after)
    {
        if (_made && Assert(Thread.CurrentThread == _worker)) after();
    }

    // Transfers need no segment, run alone in order; false without a buffer
    public bool Alone(Action<IntPtr> record)
    {
        if (!NotNull(record) || !Assert(_open is null)) return false;
        if (_slot is not { } slot || slot.LooseUsed >= MaxLoose || Fault.Length > 0) return false;
        if (slot.LooseUsed == slot.Loose.Count) Allocated(slot);
        var at = slot.LooseUsed;
        var commands = slot.Loose[at];
        if (commands == IntPtr.Zero || !VulkanDevice.Begin(commands)) return false;
        Transfers(commands, true);
        record(commands);
        Transfers(commands, false);
        slot.LooseUsed++;
        if (!VulkanDevice.End(commands)) return false;
        Enqueue(new Op { Kind = OpKind.Queue, Ref = slot.LooseSubmits[at] }); // the frame's fence covers it
        return true;
    }

    // A loose command buffer and its submission, made once: no closure per transfer
    private void Allocated(Slot slot)
    {
        var (device, commands) = (_device, _device.Allocate());
        slot.Loose.Add(commands);
        slot.LooseSubmits.Add(() => Loose(device, commands));
    }

    // On the recording thread
    private void Loose(VulkanDevice device, IntPtr commands)
    {
        if (Fault.Length > 0) return;
        if (!device.Submit([commands], [], 0, ended: 1))
            Fault = "a transfer's submission failed (the device is lost?)";
    }

    private static void Transfers(IntPtr commands, bool before)
    {
        if (!Assert(VkApi.CmdPipelineBarrier != null)) return;
        var memory = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier,
            SrcAccess = before ? Vk.AccessMemoryRead | Vk.AccessMemoryWrite : Vk.AccessTransferWrite,
            DstAccess = before ? Vk.AccessTransferWrite | Vk.AccessTransferRead : Vk.AccessMemoryRead | Vk.AccessMemoryWrite
        };
        var (from, to) = before ? (Vk.StageAll, Vk.StageTransfer) : (Vk.StageTransfer, Vk.StageAll);
        VkApi.CmdPipelineBarrier(commands, from, to, 0, 1, &memory, 0, null, 0, null);
    }

    // VulkanDevice.CatchUp: off the recording thread, it caught up (its submissions made)
    private void CaughtUp()
    {
        if (_worker is { } worker && Thread.CurrentThread != worker) Drain();
    }

    // The renderer's Start: the next frame, unless work before it began it already
    public void Fresh()
    {
        if (EarlyBegun) EarlyBegun = false;
        else Next();
    }

    private void Early()
    {
        if (!_ended || _beginning || Fault.Length > 0) return;
        _beginning = true;
        try
        {
            Next();
        }
        finally
        {
            _beginning = false;
        }

        EarlyBegun = _slot is not null;
        EarlyBegins++;
    }

    private static void Ordered(IntPtr commands)
    {
        if (!Assert(VkApi.CmdPipelineBarrier != null)) return;
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        var memory = new Vk.GlobalBarrier { SType = Vk.MemoryBarrier, SrcAccess = all, DstAccess = all };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 1, &memory, 0, null, 0, null);
    }

    public void Dispose()
    {
        _ = _device.WaitIdle();
        if (_device.CatchUp == CaughtUp) _device.CatchUp = null;
        StopWorker(); // after the GPU is done with the command buffers its pool frees
        if (_stamps != 0) VkApi.DestroyQueryPool(_device.Handle, _stamps, null);
        if (_statistics != 0) VkApi.DestroyQueryPool(_device.Handle, _statistics, null);
        (_stamps, _statistics) = (0, 0);
        (_open, _slot) = (null, null);
        foreach (var slot in _slots.Bounded(Slots))
        {
            slot.Uploads.Dispose();
            slot.Staging.Dispose();
            _device.DestroyFence(slot.Fence);
        }

        _slots.Clear();
    }
}

// Vulkan-only's exact copies: texels between same-format images at any level/layer (glCopyImageSubData, a draw's
// snapshot), a rectangle into host memory for an OpenGL read, and whole level/layer clears (glClearTexImage). In the
// open segment outside rendering; the next rendering moves the images back.
internal sealed unsafe partial class VulkanFrame
{
    // Regions (disjoint: a snapshot's levels) in one copy behind one barrier
    public bool CopyImage(SharedImage from, SharedImage to, Vk.ImageCopy[] regions)
    {
        if (!NotNull(regions) || regions.Length == 0) return regions is { Length: 0 };
        if (_open is null || !Holds(from) || !Holds(to) || from == to || !Assert(from.Format.Aspect == to.Format.Aspect))
            return false;
        ForTransfer((from, Vk.LayoutTransferSrc), (to, Vk.LayoutTransferDst));
        var (source, target) = (from.Image, to.Image);
        Post(commands => ImageCopied(commands, (source, target), regions));
        return true;
    }

    private static void ImageCopied(IntPtr commands, (ulong From, ulong To) images, Vk.ImageCopy[] regions)
    {
        if (!Assert(images.From != 0 && images.To != 0)) return;
        fixed (Vk.ImageCopy* at = regions)
            VkApi.CmdCopyImage(commands, images.From, Vk.LayoutTransferSrc, images.To, Vk.LayoutTransferDst, (uint)regions.Length, at);
    }

    // What drew into the image comes first; the host reads the buffer once the segment's submission is done
    public bool CopyOut(SharedImage image, Vk.BufferImageCopy region, ulong buffer)
    {
        if (_open is null || !Holds(image) || !Assert(buffer != 0)) return false;
        ForTransfer((image, Vk.LayoutTransferSrc));
        var source = image.Image;
        Post(commands => CopiedOut(commands, source, region, buffer));
        return true;
    }

    // Outside rendering, images in transfer layouts behind one barrier (even with no move): earlier writes come first
    private void ForTransfer((SharedImage Image, int Layout) first, (SharedImage Image, int Layout)? second = null)
    {
        EndRendering();
        _moves.Clear();
        Want(first.Image, first.Layout);
        if (second is { } other) Want(other.Image, other.Layout);
        Barrier();
    }

    private static void CopiedOut(IntPtr commands, ulong image, Vk.BufferImageCopy region, ulong buffer)
    {
        if (!Assert(image != 0 && buffer != 0)) return;
        VkApi.CmdCopyImageToBuffer(commands, image, Vk.LayoutTransferSrc, buffer, 1, &region);
        HostReadable(commands);
    }

    // Level 0 of from, whole, into an image of FrameBridge's (same size and format, outside the frame's layouts: it rests
    // in GENERAL before and after); posted in the open segment
    public bool CopyRaw(SharedImage from, ulong into)
    {
        if (_open is null || !Holds(from) || !Assert(into != 0) || !Assert(from.Image != into)) return false;
        ForTransfer((from, Vk.LayoutTransferSrc));
        Post(new Op
        {
            Kind = OpKind.RawCopy, Pipeline = from.Image, Layout = into, Count = from.Format.Aspect & ~Vk.AspectStencil,
            First = (uint)from.Width, Instances = (uint)from.Height
        });
        return true;
    }

    private static void RawCopied(IntPtr commands, (ulong From, ulong Into) images, uint aspect, (int Width, int Height) size)
    {
        if (!Assert(size.Width > 0 && size.Height > 0)) return;
        var barrier = Vk.ImageBarrier.Full(images.Into, (Vk.LayoutGeneral, Vk.LayoutTransferDst), aspect);
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
        var layers = new Vk.Layers { Aspect = aspect, Count = 1 };
        var region = new Vk.ImageCopy { Source = layers, Target = layers, Width = (uint)size.Width, Height = (uint)size.Height, Depth = 1 };
        VkApi.CmdCopyImage(commands, images.From, Vk.LayoutTransferSrc, images.Into, Vk.LayoutTransferDst, 1, &region);
        (barrier.OldLayout, barrier.NewLayout) = (Vk.LayoutTransferDst, Vk.LayoutGeneral);
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }

    // What the transfers before wrote, visible to the host once the fence is signalled
    internal static void HostReadable(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero)) return;
        var host = new Vk.GlobalBarrier { SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite, DstAccess = Vk.AccessHostRead };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageHost, 0, 1, &host, 0, null, 0, null);
    }

    // The levels and layers of range (its aspect the image's) set to the color, or to the depth and stencil
    public bool ClearImage(SharedImage image, Vk.ColorRange range, (float R, float G, float B, float A) color,
        Vk.DepthStencilValue depth)
    {
        if (_open is null || !Holds(image) || !Assert(range.Levels > 0 && range.Layers > 0)) return false;
        ForTransfer((image, Vk.LayoutTransferDst));
        var (handle, colored) = (image.Image, (image.Format.Aspect & Vk.AspectColor) != 0);
        Post(commands => ImageCleared(commands, (handle, colored), range, (color, depth)));
        return true;
    }

    private static void ImageCleared(IntPtr commands, (ulong Image, bool Color) image, Vk.ColorRange range,
        ((float R, float G, float B, float A) Color, Vk.DepthStencilValue Depth) value)
    {
        if (!Assert(image.Image != 0)) return;
        if (!image.Color)
        {
            var depth = value.Depth;
            VkApi.CmdClearDepthStencilImage(commands, image.Image, Vk.LayoutTransferDst, &depth, 1, &range);
            return;
        }

        var color = stackalloc float[] { value.Color.R, value.Color.G, value.Color.B, value.Color.A };
        VkApi.CmdClearColorImage(commands, image.Image, Vk.LayoutTransferDst, color, 1, &range);
    }
}
