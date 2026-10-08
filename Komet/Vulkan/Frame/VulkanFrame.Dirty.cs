using System.Diagnostics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// The handoff as lazy as ordering allows, while GlTap hears of every OpenGL call that runs GPU work (Lazy: the scene's Touch group).
// OpenGL signals a segment only when it ran work on shared memory since it last signalled - an upload into a shared buffer (a
// terrain pool's), a texture call on a shared image, a clear onto one, any draw, blit, copy, read or compute it runs itself, any
// such call of Komet's own (Quietly): otherwise Vulkan's work has nothing of OpenGL's to follow, the signal (a drain of Mesa's
// glthread) and the submission's wait for it (RADV blocks the CPU there until OpenGL flushed) are left out. Vulkan still signals
// each segment's end, but OpenGL waits for it only before it next touches shared memory (Settle); when it does not, the next
// submission waits for that signal itself, on the same queue, so every binary semaphore is waited once before it is signalled
// again. Without the judge (the scene off) OpenGL signals and waits at every segment as before.
internal sealed partial class VulkanFrame
{
    private bool _glDirty = true;
    private bool _lazy;
    private Owed? _owed; // the closed segment's signal OpenGL has not waited for yet

    // At: the ops posted up to its submission
    private sealed record Owed(SharedSemaphore ToGl, uint[] Buffers, uint[] Names, uint[] Layouts, long At);

    // Whether every OpenGL call that runs GPU work is judged (GlTouched, GlQuiet): only then may the handoff be lazy
    public bool Lazy
    {
        get => _lazy;
        set
        {
            if (value == _lazy) return;
            if (!value) Settle(); // nothing tells any more when OpenGL touches shared memory
            if (!value) PoolsFirst?.Invoke(); // after the wait: OpenGL makes Vulkan's waiting copies into pools
            (_lazy, _glDirty) = (value, true);
        }
    }

    // Run before OpenGL runs a call of the engine's that may read or write a terrain pool's buffers (a draw, compute, copy, read
    // or upload): the renderer's copies into pools come first (TerrainRenderer.PoolsFirst)
    public Action? PoolsFirst { get; set; }

    public long LazyWaits { get; private set; }
    private (IReadOnlyList<Shared>? Of, HashSet<int> Set) _sharedTextures = (null, []);

    public long CleanSignals { get; private set; }
    public long DirtySignals { get; private set; }

    // Before the call runs
    public void GlTouched(GlTap.Touch kind, uint id)
    {
        if ((_glDirty && _owed is null) || !Assert(kind <= GlTap.Touch.Mipmap) || !NotNull(Images)) return;
        var touches = kind switch
        {
            GlTap.Touch.Upload => Shares(id),
            GlTap.Touch.TextureUpload or GlTap.Touch.Mipmap or GlTap.Touch.ClearTexture => SharedTexture((int)id),
            GlTap.Touch.Clear => SharedAttachment(GlTap.DrawFramebuffer),
            GlTap.Touch.ClearNamed => SharedAttachment((int)id),
            _ => true
        };
        if (!touches) return; // dirty or not, as it was
        if (kind is GlTap.Touch.Draw or GlTap.Touch.Compute or GlTap.Touch.Copy or GlTap.Touch.Read or GlTap.Touch.Upload)
            PoolsFirst?.Invoke();
        Settle();
        _glDirty = true;
    }

    public void GlQuiet()
    {
        Settle();
        _glDirty = true;
        _ = Assert(_glDirty);
    }

    // OpenGL waits for the last closed segment's signal, taking back what it handed over, before it touches shared memory
    private void Settle()
    {
        if (_owed is not { } owed || !Assert(owed.ToGl.Gl > 0)) return;
        _owed = null;
        // While this thread waits anyway, OpenGL's queued work goes to the kernel: the synchronous flush of the next signal
        // then has little left to submit
        if (Volatile.Read(ref _recorded) < owed.At) GL.Flush();
        Reach(owed.At); // the recording thread made the submission that signals it
        if (Fault.Length > 0) return; // a submission failed: the signal never comes
        var start = Stopwatch.GetTimestamp();
        VulkanWatch.Mark("OpenGL waits for Vulkan");
        GlInterop.Wait(owed.ToGl.Gl, owed.Buffers, owed.Names, owed.Layouts);
        GL.Flush();
        WaitTicks += Stopwatch.GetTimestamp() - start;
        LazyWaits++;
    }

    // What the open segment's submission waits for: OpenGL's signal when it gave one, the last segment's own signal when OpenGL
    // never waited for it, OpenGL's late signal
    private ReadOnlySpan<ulong> Waits(Segment segment)
    {
        if (!NotNull(segment)) return [];
        var count = 0;
        if (_signalled) _waits[count++] = segment.ToVulkan.Vulkan;
        if (_owed is { } owed) _waits[count++] = owed.ToGl.Vulkan;
        if (_late && segment.Late is { } late) _waits[count++] = late.Vulkan;
        _ = Assert(count <= _waits.Length) && Assert(!_signalled || segment.ToVulkan.Vulkan != 0);
        return _waits.AsSpan(0, count);
    }

    private readonly ulong[] _waits = new ulong[3];

    // The window's framebuffer (0) always counts: its stand-in is shared
    private bool SharedAttachment(int fbo)
    {
        if (fbo <= 0) return true;
        var (colors, depth) = Attached(fbo);
        if (!NotNull(colors) || SharedTexture(depth)) return true;
        foreach (var color in colors.Bounded(MaxColors))
            if (SharedTexture(color))
                return true;
        return false;
    }

    private bool SharedTexture(int texture)
    {
        if (!Assert(texture >= 0) || texture == 0) return false;
        var now = Images();
        if (!NotNull(now)) return true;
        if (!ReferenceEquals(now, _sharedTextures.Of)) _sharedTextures = (now, [.. now.Select(s => s.Image.Texture)]);
        return Assert(_sharedTextures.Set.Count <= MaxImages * 4) && _sharedTextures.Set.Contains(texture);
    }
}
