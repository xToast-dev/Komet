using System.Diagnostics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// How a segment's work orders against OpenGL's. Ordered: OpenGL signals as the segment opens, so what Vulkan records follows
// everything OpenGL did before. Deferred: OpenGL signals only as the segment closes - its calls in between run first on the
// GPU, which is right only for work that commutes with Vulkan's (depth-only draws into a depth both test and write with LESS or
// LEQUAL, calls touching none of the images the segment writes). Each signal drains Mesa's glthread and its driver thread, so
// a deferred segment spanning several passes costs one signal instead of one per pass.
internal enum SegmentSync
{
    Ordered,
    Deferred
}

// Each segment is one handoff: it takes every shared image and buffer over from OpenGL as it opens and hands them back as
// it closes; its submission waits for OpenGL's signal and OpenGL waits for Vulkan's. Segments open and close where the
// renderer's plan says, never because some OpenGL call happened to come by - except the guard: OpenGL binding an image a
// deferred segment writes, to read it, closes the segment first. A slot is reused once the fence submitted at its frame's
// end says the GPU is done with it. OpenGL never waits for a semaphore whose Vulkan submission failed: that is the freeze a
// lost device would otherwise bring. Every submission of the frame's is the recording thread's, in the order posted.
internal sealed partial class VulkanFrame : IDisposable
{
    public const int Slots = 3, MaxSegments = 32, MaxImages = TerrainTextures.MaxTextures + 128; // the targets' 80 too
    private const ulong GpuTimeoutNs = 5_000_000_000; // a GPU that takes longer is hung, or waits on OpenGL that waits on it
    private const ulong UploadBytes = 32UL << 20;
    private const int MaxClosers = 64;

    private readonly VulkanDevice _device;
    private readonly List<Slot> _slots = [];
    private Slot? _slot;
    private Segment? _open;
    private int _next;
    private bool _pending; // open, OpenGL not signalled yet (deferred)
    private bool _unflushed; // a segment closed and OpenGL queued work after it that was not flushed since
    private bool _signalled; // OpenGL signalled the open segment's ToVulkan: its submission waits for it
    private bool _late; // OpenGL wrote for the open segment after its signal: it signals Late as the segment closes

    private sealed class Segment(IntPtr taking, IntPtr commands, IntPtr setup,
        (SharedSemaphore ToVulkan, SharedSemaphore ToGl) semaphores)
    {
        // The recording thread's: the images taken over from OpenGL (Taken), then the draws
        public IntPtr Taking { get; } = taking;
        public IntPtr Commands { get; } = commands;
        public Handoff.Image[] Taken { get; set; } = [];
        public Handoff.Image[] Released { get; set; } = [];
        public IntPtr Setup { get; } = setup; // transfers that run before the segment's draws
        public SharedSemaphore ToVulkan { get; } = semaphores.ToVulkan;
        public SharedSemaphore ToGl { get; } = semaphores.ToGl;
        public SharedSemaphore? Late { get; set; } // made the first time OpenGL wrote for the segment after its signal
    }

    private sealed class Slot(HostBuffer uploads, HostBuffer staging, ulong fence)
    {
        public HostBuffer Uploads { get; } = uploads;
        public HostBuffer Staging { get; } = staging; // bulk: only copied from
        public ulong Fence { get; } = fence;
        public List<Segment> Segments { get; } = [];
        public int Used { get; set; }
        public bool InFlight { get; set; } // the recording thread's, once it reached Signalled
        public long Signalled { get; set; } // the ops posted up to the fence's submission
        public Action? Signal { get; set; }

        // Transfers outside any segment (Alone)
        public List<IntPtr> Loose { get; } = [];
        public int LooseUsed { get; set; }

        public int Index { get; init; }
        public List<Stamp> Stamps { get; } = [];

        // Pipeline statistics: the frame is counted, its queries were reset, how many it began
        public bool Sampled { get; set; }
        public bool Reset { get; set; }
        public int Queries { get; set; }
    }

    // An image handed over with every segment: at rest (between segments, and to OpenGL) in its resting layout
    public readonly record struct Shared(SharedImage Image, int Resting, uint GlLayout);

    private VulkanFrame(VulkanDevice device)
    {
        _ = Assert(device.Handle != IntPtr.Zero) && Assert(Slots > 1);
        _device = device;
    }

    public Func<IReadOnlyList<Shared>> Images { get; set; } = () => [];
    public Func<uint[]> Buffers { get; set; } = () => [];

    // Run as a segment opens, once its setup takes transfers, and as the frame ends, with no segment open. Filling: the
    // renderer's own, before Opened (what OpenGL packed for the copies goes into the setup)
    public Action? Opened { get; set; }
    public Action? Ending { get; set; }
    public Action? Filling { get; set; }

    // Run as an open segment begins to close, while it still takes commands (the renderer's depth written back)
    public Action? Closes { get; set; }

    public bool Open => _open is not null;
    public bool Pending => _pending;
    public IntPtr Setup => _open?.Setup ?? IntPtr.Zero;

    // The open segment's number, for what it reads
    public long Serial { get; private set; }

    public HostBuffer? Uploads => _slot?.Uploads;

    // The frame's host memory for data the GPU copies elsewhere before it reads it (HostBuffer's bulk kind)
    public HostBuffer? Staging => _slot?.Staging;

    // Bytes in an earlier frame's staging (not reused yet: it is begun again Slots frames on) moved into this frame's, whose
    // memory no segment of a later frame reads: the new place, or null without room
    public unsafe (ulong Buffer, long At)? Restaged((ulong Buffer, long At, int Bytes) from)
    {
        if (_slot?.Staging is not { } staging || !Assert(from.Bytes > 0) || from.Buffer == staging.Buffer)
            return _slot is null ? null : (from.Buffer, from.At);
        var slot = _slots.Find(s => s.Staging.Buffer == from.Buffer);
        var source = slot is null ? null : slot.Staging.Pointer(from.At);
        if (source == null) return null;
        var at = staging.Take(from.Bytes, out var into, 16);
        if (at < 0) return null;
        new ReadOnlySpan<byte>(source, from.Bytes).CopyTo(into);
        return (staging.Buffer, at);
    }

    public long Frames { get; private set; }
    public long Number { get; private set; } // frames begun

    // The segments the frame has opened so far
    public int Used => _slot?.Used ?? 0;

    // The last frame the GPU finished for sure: the one whose slot the frame begun last waited for
    public long Done => Number - Slots;
    public long Segments { get; private set; }
    public long SubmitTicks { get; private set; }
    public long OpenTicks { get; private set; } // the images taken over
    public long SignalTicks { get; private set; } // OpenGL's signal and flush
    public long WaitTicks { get; private set; } // OpenGL's wait and flush

    public Dictionary<string, long> Closers { get; } = new(StringComparer.Ordinal);

    public static VulkanFrame? Create(VulkanDevice device, out string why)
    {
        why = "";
        if (!NotNull(device)) return null;
        var frame = new VulkanFrame(device);
        _ = Assert(frame._slots.Count == 0);
        for (var i = 0; i < Slots; i++)
        {
            var (uploads, fence) = (HostBuffer.Create(device, UploadBytes), device.Fence(false));
            var staging = HostBuffer.Create(device, UploadBytes, bulk: true);
            if (uploads is not null && staging is not null && fence != 0)
            {
                frame._slots.Add(new Slot(uploads, staging, fence) { Index = i });
                continue;
            }

            uploads?.Dispose();
            staging?.Dispose();
            device.Destroy(fence);
            frame.Dispose();
            why = "no memory for a frame's uploads, or no fence";
            return null;
        }

        frame.StartWorker();
        device.CatchUp = frame.CaughtUp;
        return frame;
    }

    public void Next()
    {
        Close("the frame's end");
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
        var start = Hitches.Now;
        VulkanWatch.Mark("frame end: waiting for the GPU to finish the slot's last frame", Number);
        Reach(slot.Signalled); // frames ago: made long since
        var finished = !slot.InFlight || _device.Wait(slot.Fence, GpuTimeoutNs);
        if (slot.InFlight) Hitches.Since(Hitches.Kind.GpuWait, start);
        if (!finished && Fault.Length == 0) Fault = $"the GPU did not finish frame {Number - Slots} within 5 s";
        Timed(slot, finished);
        CountedFrame(slot, finished);
        VulkanWatch.Mark("frame begun", Number);
        (slot.InFlight, slot.Used, slot.LooseUsed) = (false, 0, 0);
        slot.Uploads.Reset();
        slot.Staging.Reset();
        _slot = slot;
        _ = Assert(_open is null);
    }

    // last: the segment that shows the frame, which may take the frame's last segment; nothing else may, so a frame
    // whose draws used up the others can still be shown
    public bool Begin(SegmentSync sync, out string why, bool last = false)
    {
        why = Fault;
        if (Fault.Length > 0) return false;
        if (_open is not null)
        {
            if (sync == SegmentSync.Ordered) Signal();
            return true;
        }

        if (_slot is null) Next();
        if (!NotNull(_slot)) return false;
        var slot = _slot;
        _ = Assert(slot.Used <= slot.Segments.Count);
        if (slot.Used >= (last ? MaxSegments : MaxSegments - 1))
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

        var start = Stopwatch.GetTimestamp();
        segment.Taken = Acquire();
        Enqueue(new Op { Kind = OpKind.Begin, Ref = segment }); // before anything recorded into it
        Transfers(segment.Setup, true);
        OpenTicks += Stopwatch.GetTimestamp() - start;
        (_open, _pending) = (segment, true);
        Serial++;
        TimingBegins(segment); // before anything Opened records
        CountingBegins(segment);
        _written.Clear();
        Filling?.Invoke();
        Opened?.Invoke();
        if (sync == SegmentSync.Ordered) Signal();
        else if (!GlTap.Scene) GlTap.Binding = Guarded;
        return true;
    }

    // GlTap.Binding while a deferred segment is open and nothing but the terrain is Vulkan's: OpenGL binding a texture the
    // segment draws into, to read it, closes the segment first
    private void Guarded(uint texture)
    {
        if (!_pending || !Assert(texture > 0) || !_written.ContainsKey((int)texture)) return;
        Close("OpenGL reads what the deferred segment drew");
    }

    // OpenGL signals: what Vulkan records from now on follows everything OpenGL did so far
    private void Signal()
    {
        if (!_pending || _open is not { } segment || !Assert(segment.ToVulkan.Gl > 0)) return;
        VulkanWatch.Mark("OpenGL signals Vulkan");
        GlTap.Binding = null;
        var start = Stopwatch.GetTimestamp();
        var dirty = _glDirty; // OpenGL touched shared memory since it last signalled
        if (dirty) DirtySignals++;
        else CleanSignals++;
        (_glDirty, _signalled) = (false, dirty || !Lazy);
        _ = Assert(CleanSignals >= 0) && Assert(DirtySignals >= 0);
        if (_signalled)
        {
            GlInterop.Signal(segment.ToVulkan.Gl, Buffers(), _names, _glLayouts);
            GL.Flush();
        }

        SignalTicks += Stopwatch.GetTimestamp() - start;
        _pending = false;
    }

    // OpenGL just wrote what a segment's setup copies from (GlStaging), so the segment's work must follow it. With none open or
    // one pending the signal to come covers it; signalled already, OpenGL signals once more as the segment closes, and the
    // submission waits for that too. False when there is no semaphore for it.
    public bool Late()
    {
        _glDirty = true; // a lazy handoff signals then
        if (_open is not { } segment) return Fault.Length == 0;
        if (Fault.Length > 0 || !Assert(segment.ToVulkan.Gl > 0)) return false;
        if (_pending) return true;
        segment.Late ??= SharedSemaphore.Create(_device);
        _late |= segment.Late is not null;
        return segment.Late is not null;
    }

    public long LateSignals { get; private set; }

    private void SignalLate(Segment segment)
    {
        if (!_late || !NotNull(segment.Late) || !Assert(!_pending)) return;
        var start = Stopwatch.GetTimestamp();
        GlInterop.Signal(segment.Late.Gl, Buffers(), [], []);
        GL.Flush();
        SignalTicks += Stopwatch.GetTimestamp() - start;
        LateSignals++;
    }

    private Segment? Made(Slot slot, out string why)
    {
        why = "";
        if (!NotNull(slot) || !Assert(slot.Segments.Count == slot.Used)) return null;
        Drain(); // the recording thread is done with its pool before a command buffer is made in it
        var (taking, commands, setup) = (AllocateRecorded(), AllocateRecorded(), _device.Allocate());
        var (toVulkan, toGl) = (SharedSemaphore.Create(_device), SharedSemaphore.Create(_device));
        if (taking != IntPtr.Zero && commands != IntPtr.Zero && setup != IntPtr.Zero && toVulkan is not null &&
            toGl is not null)
        {
            var segment = new Segment(taking, commands, setup, (toVulkan, toGl));
            slot.Segments.Add(segment);
            return segment;
        }

        toVulkan?.Dispose();
        toGl?.Dispose();
        why = "no command buffer or semaphores for a segment";
        return null;
    }

    // The submission goes to the recording thread as its last command for the segment: it ends the command buffer, submits
    // and then runs after once the submission went through, while the engine's thread goes on. Any queue operation of another
    // thread waits for it first (VulkanDevice.CatchUp), and so does OpenGL's wait for the segment's signal (Settle) - for the
    // submission only, not for after (a present that may wait for the display); a submission that failed faults the frame,
    // and OpenGL then waits for nothing.
    public void Close(string why, Action? after = null)
    {
        if (_open is null || _slot is null || !NotNull(why)) return;
        Closes?.Invoke();
        if (_open is not { } segment || _slot is not { } slot) return;
        GlTap.Binding = null;
        Signal();
        SignalLate(segment);
        _ = Assert(slot.Used < MaxSegments);
        if (Closers.Count < MaxClosers || Closers.ContainsKey(why)) Closers[why] = Closers.GetValueOrDefault(why) + 1;
        EndRendering();
        Release(segment);
        Transfers(segment.Setup, false);
        TimingEnds(segment);
        var setupEnded = VulkanDevice.End(segment.Setup); // from the engine's thread's pool: ended here
        var buffers = Buffers();
        var showing = _showing;
        _showing = 0;
        ulong[] signals = showing != 0 ? [segment.ToGl.Vulkan, showing] : [segment.ToGl.Vulkan];
        ulong[] waits = [.. Waits(segment)];
        _ = Assert(waits.Length <= 3);
        var (taking, setup, device) = (segment.Taking, segment.Setup, _device);
        Post(commands => Submitted(device, (taking, setup, commands), (waits, signals), setupEnded));
        var submission = _posted;
        if (after is not null) Post(_ => After(after));
        (_open, _counting) = (null, -1); // a query is ended before the rendering is, unless the frame faulted
        (_signalled, _owed, _late) = (false, null, false); // the owed signal, when there was one, is Vulkan's own wait now
        Shown = showing != 0 && Fault.Length == 0;
        slot.Used++; // the command buffers were begun: the slot's fence covers them either way
        if (Fault.Length > 0) return; // OpenGL must not wait for a signal that never comes
        _owed = new Owed(segment.ToGl, buffers, _names, _glLayouts, submission);
        if (!Lazy) Settle(); // OpenGL waits at once, as without a judge of what it touches
        _unflushed = true;
        Segments++;
    }

    // On the recording thread before a segment's submission: what the CPU still writes into memory it copies from is written
    // (FacePacking.Settle)
    public Action? Submitting { get; set; }

    // On the recording thread
    private void Submitted(VulkanDevice device, (IntPtr Taking, IntPtr Setup, IntPtr Commands) buffers,
        (ulong[] Waits, ulong[] Signals) semaphores, bool setupEnded)
    {
        _made = false;
        if (!Assert(buffers.Commands != IntPtr.Zero) || !NotNull(device)) return;
        Submitting?.Invoke();
        var start = Stopwatch.GetTimestamp();
        _made = setupEnded && Fault.Length == 0 && device.Submit([buffers.Taking, buffers.Setup, buffers.Commands],
            semaphores.Waits, semaphores.Signals, 0, ended: 2);
        SubmitTicks += Stopwatch.GetTimestamp() - start;
        if (!_made && Fault.Length == 0) Fault = "a segment's submission failed (the device is lost?)";
    }

    private bool _made; // the recording thread's: the last segment's submission went through

    private void After(Action after)
    {
        if (_made && NotNull(after) && Assert(Thread.CurrentThread == _worker)) after();
    }

    // Transfers need no segment: into memory OpenGL never sees (the buffer mirrors), outside any segment they run on their own,
    // in submission order after what the frame submitted so far; false when the frame has no command buffer for them
    public bool Alone(Action<IntPtr> record)
    {
        if (!NotNull(record) || !Assert(_open is null)) return false;
        if (_slot is not { } slot || slot.LooseUsed >= MaxSegments || Fault.Length > 0) return false;
        if (slot.LooseUsed == slot.Loose.Count) slot.Loose.Add(_device.Allocate());
        var commands = slot.Loose[slot.LooseUsed];
        if (commands == IntPtr.Zero || !VulkanDevice.Begin(commands)) return false;
        Transfers(commands, true);
        record(commands);
        Transfers(commands, false);
        slot.LooseUsed++;
        if (!VulkanDevice.End(commands)) return false;
        var device = _device;
        Enqueue(new Op { Kind = OpKind.Queue, Ref = () => Loose(device, commands) }); // the frame's fence covers it
        return Assert(slot.LooseUsed <= MaxSegments);
    }

    // On the recording thread
    private void Loose(VulkanDevice device, IntPtr commands)
    {
        if (Fault.Length > 0 || !Assert(commands != IntPtr.Zero) || !NotNull(device)) return;
        if (!device.Submit([commands], [], [], 0, ended: 1))
            Fault = "a transfer's submission failed (the device is lost?)";
    }

    private static unsafe void Transfers(IntPtr commands, bool before)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(VkApi.CmdPipelineBarrier != null)) return;
        var memory = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier,
            SrcAccess = before ? Vk.AccessMemoryRead | Vk.AccessMemoryWrite : Vk.AccessTransferWrite,
            DstAccess = before ? Vk.AccessTransferWrite | Vk.AccessTransferRead : Vk.AccessMemoryRead | Vk.AccessMemoryWrite
        };
        var (from, to) = before ? (Vk.StageAll, Vk.StageTransfer) : (Vk.StageTransfer, Vk.StageAll);
        VkApi.CmdPipelineBarrier(commands, from, to, 0, 1, &memory, 0, null, 0, null);
    }

    // At a stage's end after a segment closed: what OpenGL queued since goes to the GPU now, not when its buffer fills, so the
    // GPU finds OpenGL's work waiting when Vulkan's is done
    public void Flush()
    {
        if (!_unflushed || !Assert(_slot is not null)) return;
        GL.Flush();
        _unflushed = false;
        _ = Assert(!_unflushed);
    }

    // VulkanDevice.CatchUp: off the recording thread, it caught up (its submissions made)
    private void CaughtUp()
    {
        if (_worker is { } worker && Thread.CurrentThread != worker) Drain();
        _ = Assert(_worker is null || Thread.CurrentThread == _worker || Volatile.Read(ref _recorded) == _posted ||
                   Fault.Length > 0);
    }

    public unsafe void Dispose()
    {
        GlTap.Binding = null;
        Settle(); // OpenGL takes back what the last segment handed over before the frame goes
        PoolsFirst?.Invoke(); // and makes the copies into pools that still wait
        _ = Assert(_slots.Count <= Slots) && _device.WaitIdle();
        if (_device.CatchUp == CaughtUp) _device.CatchUp = null;
        StopWorker(); // after the GPU is done with the command buffers its pool frees
        if (_stamps != 0) VkApi.DestroyQueryPool(_device.Handle, _stamps, null);
        if (_statistics != 0) VkApi.DestroyQueryPool(_device.Handle, _statistics, null);
        (_stamps, _statistics) = (0, 0);
        (_open, _slot) = (null, null);
        foreach (var slot in _slots.Bounded(Slots))
        {
            foreach (var segment in slot.Segments.Bounded(MaxSegments))
            {
                segment.ToVulkan.Dispose();
                segment.ToGl.Dispose();
                segment.Late?.Dispose();
            }

            slot.Uploads.Dispose();
            slot.Staging.Dispose();
            _device.Destroy(slot.Fence);
        }

        _slots.Clear();
    }
}
