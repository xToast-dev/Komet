using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// The engine clears the window and its primary framebuffer as each frame begins; left to OpenGL, that was OpenGL work on
// shared images every frame, so no handoff was ever free of OpenGL work to wait for. Such a clear is kept and recorded as
// Vulkan's first work once the frame's first segment opens; should OpenGL touch anything before that, the frame end without
// a segment, or the scene go off, OpenGL clears first after all (FlushEarly).
internal sealed partial class SceneRenderer
{
    private const int MaxEarly = 8;
    private const uint ColorMaskBits = 0xFu << 22;

    private readonly List<Early> _early = [];

    private sealed record Early(int Fbo, GlTap.ClearCall Call, bool Colors, bool Depth);

    // From the frame's end to the next frame's stages, while the scene runs: clears may be kept for the next segment
    public bool Between { get; set; }

    public long EarlyClears { get; private set; }

    public void Opened()
    {
        Mirrors.Opened();
        _terrain.PoolsOpened();
        if (_early.Count == 0 || !Assert(_early.Count <= MaxEarly)) return;
        Early[] kept = [.. _early];
        _early.Clear();
        var failed = 0;
        foreach (var early in kept.Bounded(MaxEarly))
            if (failed > 0 || !Recorded(early))
            {
                _early.Add(early); // and every one after it, in order
                failed++;
            }

        FlushEarly(); // OpenGL's, before the segment's own work: it signals after them
    }

    // True when OpenGL must not run the clear
    private bool Kept(GlTap.ClearCall call)
    {
        if (!Between || !Assert(call.Into >= -1)) return false;
        var fbo = call.Into >= 0 ? call.Into : GlTap.DrawFramebuffer;
        var state = GlTap.State;
        var depth = (call.Mask & GlTap.ClearDepthMask) != 0 && state.DepthWrite;
        var colors = (call.Mask & GlTap.ClearColorMask) != 0;
        var plain = fbo > 0 && _terrain.Ours(fbo) && (state.Caps & (uint)GlTap.Caps.Scissor) == 0 &&
                    (call.Mask & ~(GlTap.ClearDepthMask | GlTap.ClearColorMask)) == 0 &&
                    _early.Count < MaxEarly;
        for (var i = 0; plain && colors && i < GlTap.MaxBuffers; i++)
            plain = (state.Blend[i] & ColorMaskBits) == ColorMaskBits;
        if (!plain)
        {
            FlushEarly(); // OpenGL's clear comes after the ones kept before it
            return false;
        }

        if (depth || colors) _early.Add(new Early(fbo, call, colors, depth));
        _ = Assert(fbo > 0);
        return Assert(_early.Count <= MaxEarly); // a clear OpenGL would write nothing with is done too
    }

    // Into the framebuffer's images as they are now: Start may have moved them
    private bool Recorded(Early early)
    {
        if (!NotNull(early) || !Assert(early.Fbo > 0)) return false;
        var target = _terrain.Target(GlTap.State, out _, early.Fbo, !early.Depth, foreign: true);
        if (target is null) return false;
        Span<Vk.ClearAttachment> attachments = stackalloc Vk.ClearAttachment[GlTap.MaxBuffers + 1];
        var count = Attachments(attachments, target, early.Call, (early.Colors, early.Depth), null);
        if (count == 0) return true;
        _sampled.Clear();
        var sync = _terrain.Frame.Pending ? SegmentSync.Deferred : SegmentSync.Ordered;
        if (!_terrain.Segment(target, sync, _sampled, out _)) return false;
        Cleared(target, attachments[..count], false);
        EarlyClears++;
        return true;
    }

    // A draw or compute may sample anything; a clear, blit, copy, read or texture call touches the kept clears' images only
    // when it names one; a buffer upload never does
    public void Touching(GlTap.Touch kind, uint id)
    {
        if (_early.Count == 0 || !Assert(kind <= GlTap.Touch.Mipmap)) return;
        var touches = kind switch
        {
            GlTap.Touch.Upload => false,
            GlTap.Touch.Clear => Overlaps(GlTap.DrawFramebuffer),
            GlTap.Touch.ClearNamed => Overlaps((int)id),
            GlTap.Touch.TextureUpload or GlTap.Touch.Mipmap or GlTap.Touch.ClearTexture => id == 0 || Attaches((int)id),
            _ => true
        };
        if (touches) FlushEarly();
        _ = Assert(!touches || _early.Count == 0);
    }

    private bool Overlaps(int fbo)
    {
        if (fbo <= 0) return true;
        var (colors, depth) = _terrain.Frame.Attached(fbo);
        _ = Assert(depth >= 0);
        return Attaches(depth) || colors.Any(Attaches);
    }

    private bool Attaches(int texture)
    {
        if (texture <= 0 || !Assert(_early.Count <= MaxEarly)) return false;
        foreach (var early in _early.Bounded(MaxEarly))
        {
            var (colors, depth) = _terrain.Frame.Attached(early.Fbo);
            if (depth == texture || colors.Contains(texture)) return true;
        }

        return false;
    }

    // As the engine called them: no scissor, every color component and the depth written
    public void FlushEarly()
    {
        if (_early.Count == 0 || !Assert(_early.Count <= MaxEarly)) return;
        var state = GlTap.State;
        using (GlTap.Quietly())
        {
            if ((state.Caps & (uint)GlTap.Caps.Scissor) != 0) GL.Disable(EnableCap.ScissorTest);
            GL.ColorMask(true, true, true, true);
            GL.DepthMask(true);
            foreach (var early in _early.Bounded(MaxEarly)) Cleared(early);
            if ((state.Caps & (uint)GlTap.Caps.Scissor) != 0) GL.Enable(EnableCap.ScissorTest);
            for (var i = 0; i < GlTap.MaxBuffers; i++)
            {
                var mask = (state.Blend[i] & ColorMaskBits) >> 22;
                GL.ColorMask(i, (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, (mask & 8) != 0);
            }

            GL.DepthMask(state.DepthWrite);
        }

        _early.Clear();
    }

    private static void Cleared(Early early)
    {
        if (!NotNull(early) || !Assert(early.Fbo > 0)) return;
        var call = early.Call;
        var buffers = GlTap.DrawBuffersOf(early.Fbo);
        float[] color = [call.Color.R, call.Color.G, call.Color.B, call.Color.A];
        for (var i = 0; early.Colors && i < Math.Min(buffers.Length, GlTap.MaxBuffers); i++)
            if (buffers[i] >= 0 && (call.Output < 0 || call.Output == i))
                GL.ClearNamedFramebuffer(early.Fbo, ClearBuffer.Color, i, color);
        var depth = (float)call.Depth;
        if (early.Depth) GL.ClearNamedFramebuffer(early.Fbo, ClearBuffer.Depth, 0, ref depth);
    }
}
