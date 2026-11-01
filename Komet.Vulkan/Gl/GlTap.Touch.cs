using System.Runtime.InteropServices;
using unsafe DrawArraysFn = delegate* unmanaged<uint, int, int, void>;
using unsafe DrawElementsFn = delegate* unmanaged<uint, int, uint, nint, void>;
using unsafe DrawElementsInstancedFn = delegate* unmanaged<uint, int, uint, nint, int, void>;
using unsafe DrawArraysInstancedFn = delegate* unmanaged<uint, int, int, int, void>;
using unsafe DrawElementsBaseVertexFn = delegate* unmanaged<uint, int, uint, nint, int, void>;
using unsafe DrawElementsInstancedBaseVertexFn = delegate* unmanaged<uint, int, uint, nint, int, int, void>;
using unsafe DrawElementsBasesFn = delegate* unmanaged<uint, int, uint, nint, int, int, uint, void>;
using unsafe DrawArraysInstancedBaseInstanceFn = delegate* unmanaged<uint, int, int, int, uint, void>;
using unsafe DrawRangeElementsFn = delegate* unmanaged<uint, uint, uint, int, uint, nint, void>;
using unsafe MultiDrawArraysFn = delegate* unmanaged<uint, int*, int*, int, void>;
using unsafe MultiDrawElementsFn = delegate* unmanaged<uint, int*, uint, nint*, int, void>;
using unsafe MultiDrawElementsIndirectFn = delegate* unmanaged<uint, uint, nint, int, int, void>;
using unsafe MultiDrawElementsIndirectCountFn = delegate* unmanaged<uint, uint, nint, nint, int, int, void>;
using unsafe DrawElementsIndirectFn = delegate* unmanaged<uint, uint, nint, void>;
using unsafe DrawArraysIndirectFn = delegate* unmanaged<uint, nint, void>;
using unsafe MultiDrawArraysIndirectFn = delegate* unmanaged<uint, nint, int, int, void>;
using unsafe ClearFn = delegate* unmanaged<uint, void>;
using unsafe ClearBufferfvFn = delegate* unmanaged<uint, int, float*, void>;
using unsafe ClearBufferivFn = delegate* unmanaged<uint, int, int*, void>;
using unsafe ClearBufferuivFn = delegate* unmanaged<uint, int, uint*, void>;
using unsafe ClearBufferfiFn = delegate* unmanaged<uint, int, float, int, void>;
using unsafe ClearNamedFramebufferfvFn = delegate* unmanaged<uint, uint, int, float*, void>;
using unsafe ClearNamedFramebufferfiFn = delegate* unmanaged<uint, uint, int, float, int, void>;
using unsafe ClearTexImageFn = delegate* unmanaged<uint, int, uint, uint, nint, void>;
using unsafe BlitFramebufferFn = delegate* unmanaged<int, int, int, int, int, int, int, int, uint, uint, void>;
using unsafe BlitNamedFramebufferFn = delegate* unmanaged<uint, uint, int, int, int, int, int, int, int, int, uint,
    uint, void>;
using unsafe CopyImageSubDataFn = delegate* unmanaged<uint, uint, int, int, int, int, uint, uint, int, int, int, int,
    int, int, int, void>;
using unsafe CopyTexSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, void>;
using unsafe ReadPixelsFn = delegate* unmanaged<int, int, int, int, uint, uint, nint, void>;
using unsafe GetTexImageFn = delegate* unmanaged<uint, int, uint, uint, nint, void>;
using unsafe DispatchComputeFn = delegate* unmanaged<uint, uint, uint, void>;

namespace Komet.Vulkan;

// Draws and clears go to Drawing/Clearing first, which may take them over (GL skips them); anything GL runs goes to
// Touching first (its buffer, texture or named framebuffer, 0 for the bound one) so Vulkan's work is handed over first.
// While GL rests (Vulkan-only, Resting) it also skips untaken draws, clears, blits, copies and dispatches.
internal static unsafe partial class GlTap
{
    public const uint ClearColorMask = ClearColorBit, ClearDepthMask = ClearDepthBit, ClearStencilMask = ClearStencilBit;
    private const int TouchAt = 50, TouchCount = 53;
    private const int ArraysCommand = 16, ElementsCommand = 20, MaxIndirect = 1 << 12, MaxMultiDraws = 1 << 16;
    private const uint ClearColorBit = 0x4000, ClearDepthBit = 0x100, ClearStencilBit = 0x400;
    private const uint GlColor = 0x1800, GlDepth = 0x1801, GlStencilBuffer = 0x1802, GlDepthStencil = 0x84F9;
    private const uint GlRenderbuffer = 0x8D41;

    // A depth and stencil clear: the tap follows the stencil clear value, so both go to Clearing
    private const uint DepthStencilBits = ClearDepthBit | ClearStencilBit;

    public enum Touch
    {
        Draw,
        Clear, // the draw framebuffer's attachments
        ClearNamed, // a framebuffer's
        ClearTexture, Blit, Copy, Read, Compute,
        Upload, // into a buffer
        TextureUpload, Mipmap
    }

    // One draw (index type 0: glDrawArrays*); Indirect: Vulkan-only, parameters in a buffer (Count, Instances 1)
    public readonly record struct DrawCall(uint Mode, int Count, uint IndexType, nint Indices, int Instances, int First,
        int BaseVertex, uint BaseInstance, Indirect? Indirect = null);

    // Commands' buffer (GL_DRAW_INDIRECT_BUFFER), offset, count, stride; count buffer (GL_PARAMETER_BUFFER) and offset
    public readonly record struct Indirect(uint Buffer, nint Offset, int Draws, int Stride, uint CountBuffer = 0,
        nint CountOffset = 0);

    // One clear: framebuffer (-1: bound), GL_*_BUFFER_BIT mask, draw buffer (glClearBuffer*, -1: all), values
    public readonly record struct ClearCall(int Into, uint Mask, int Output, (float R, float G, float B, float A) Color,
        double Depth, int Stencil = 0);

    public static System.Func<DrawCall, bool>? Drawing { get; set; }

    // Clears of color or depth only
    public static System.Func<ClearCall, bool>? Clearing { get; set; }

    public static Action<Touch, uint>? Touching { get; set; }

    // For the report: the most ranges one multi-draw had, and the ranges taken one by one so far
    public static int LargestMultiDraw { get; private set; }
    public static long MultiDrawRanges { get; private set; }

    // A multi-draw range past the first is being judged: no Touched follows any verdict (the whole call is taken)
    public static bool LaterRange { get; private set; }

    // Texture 0 was specified, copied or mipmapped into: it may be complete, so empty units no longer read (0, 0, 0, 1)
    public static bool DefaultTextureTouched { get; private set; }

    private static bool Drawn(DrawCall call) =>
        _quiet == 0 && Drawing is { } drawing && Assert(call.Count >= 0) && Asked(drawing, call, false);

    private static bool Cleared(ClearCall call) =>
        _quiet == 0 && Clearing is { } clearing && Asked(clearing, call, false);

    private static void Touched(Touch kind, uint id)
    {
        if (id == 0 && kind is Touch.TextureUpload or Touch.Mipmap or Touch.Copy) DefaultTextureTouched = true;
        if (_quiet == 0 && Touching is { } touching) Heard(static s => s.touching(s.kind, s.id), (touching, kind, id));
    }

    private static ClearCall ClearOf(int into, uint buffer, int drawBuffer, float* values)
    {
        if (buffer == GlDepth) return new ClearCall(into, ClearDepthBit, -1, default, values[0]);
        var mask = buffer == GlColor ? ClearColorBit : 0;
        return new ClearCall(into, mask, drawBuffer, (values[0], values[1], values[2], values[3]), 1);
    }

    // Draws, clears, copies and reads from TouchAt on, in this order; the uploads (Uploads) follow
    private static Entry[] Drawings() =>
    [
        new("glDrawArrays", (IntPtr)(DrawArraysFn)(&DrawArrays)),
        new("glDrawElements", (IntPtr)(DrawElementsFn)(&DrawElements)),
        new("glDrawElementsInstanced", (IntPtr)(DrawElementsInstancedFn)(&DrawElementsInstanced)),
        new("glDrawArraysInstanced", (IntPtr)(DrawArraysInstancedFn)(&DrawArraysInstanced)),
        new("glDrawElementsBaseVertex", (IntPtr)(DrawElementsBaseVertexFn)(&DrawElementsBaseVertex)),
        new("glDrawElementsInstancedBaseVertex",
            (IntPtr)(DrawElementsInstancedBaseVertexFn)(&DrawElementsInstancedBaseVertex)),
        new("glDrawElementsInstancedBaseVertexBaseInstance", (IntPtr)(DrawElementsBasesFn)(&DrawElementsBases)),
        new("glDrawArraysInstancedBaseInstance",
            (IntPtr)(DrawArraysInstancedBaseInstanceFn)(&DrawArraysInstancedBaseInstance)),
        new("glDrawRangeElements", (IntPtr)(DrawRangeElementsFn)(&DrawRangeElements)),
        new("glMultiDrawArrays", (IntPtr)(MultiDrawArraysFn)(&MultiDrawArrays)),
        new("glMultiDrawElements", (IntPtr)(MultiDrawElementsFn)(&MultiDrawElements)),
        new("glMultiDrawElementsIndirect", (IntPtr)(MultiDrawElementsIndirectFn)(&MultiDrawElementsIndirect)),
        new("glMultiDrawElementsIndirectCount",
            (IntPtr)(MultiDrawElementsIndirectCountFn)(&MultiDrawElementsIndirectCount)),
        new("glDrawElementsIndirect", (IntPtr)(DrawElementsIndirectFn)(&DrawElementsIndirect)),
        new("glDrawArraysIndirect", (IntPtr)(DrawArraysIndirectFn)(&DrawArraysIndirect)),
        new("glMultiDrawArraysIndirect", (IntPtr)(MultiDrawArraysIndirectFn)(&MultiDrawArraysIndirect)),
        new("glClear", (IntPtr)(ClearFn)(&Clear)),
        new("glClearBufferfv", (IntPtr)(ClearBufferfvFn)(&ClearBufferfv)),
        new("glClearBufferiv", (IntPtr)(ClearBufferivFn)(&ClearBufferiv)),
        new("glClearBufferuiv", (IntPtr)(ClearBufferuivFn)(&ClearBufferuiv)),
        new("glClearBufferfi", (IntPtr)(ClearBufferfiFn)(&ClearBufferfi)),
        new("glClearNamedFramebufferfv", (IntPtr)(ClearNamedFramebufferfvFn)(&ClearNamedFramebufferfv)),
        new("glClearNamedFramebufferfi", (IntPtr)(ClearNamedFramebufferfiFn)(&ClearNamedFramebufferfi)),
        new("glClearTexImage", (IntPtr)(ClearTexImageFn)(&ClearTexImage)),
        new("glBlitFramebuffer", (IntPtr)(BlitFramebufferFn)(&BlitFramebuffer)),
        new("glBlitNamedFramebuffer", (IntPtr)(BlitNamedFramebufferFn)(&BlitNamedFramebuffer)),
        new("glCopyImageSubData", (IntPtr)(CopyImageSubDataFn)(&CopyImageSubData)),
        new("glCopyTexSubImage2D", (IntPtr)(CopyTexSubImage2DFn)(&CopyTexSubImage2D)),
        new("glReadPixels", (IntPtr)(ReadPixelsFn)(&ReadPixels)),
        new("glGetTexImage", (IntPtr)(GetTexImageFn)(&GetTexImage)),
        new("glDispatchCompute", (IntPtr)(DispatchComputeFn)(&DispatchCompute))
    ];

    [UnmanagedCallersOnly]
    private static void DrawArrays(uint a, int b, int c)
    {
        if (Drawn(new DrawCall(a, c, 0, 0, 1, b, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt] != IntPtr.Zero))
            ((DrawArraysFn)Original[TouchAt])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void DrawElements(uint a, int b, uint c, nint d)
    {
        if (Drawn(new DrawCall(a, b, c, d, 1, 0, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 1] != IntPtr.Zero))
            ((DrawElementsFn)Original[TouchAt + 1])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsInstanced(uint a, int b, uint c, nint d, int e)
    {
        if (Drawn(new DrawCall(a, b, c, d, e, 0, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 2] != IntPtr.Zero))
            ((DrawElementsInstancedFn)Original[TouchAt + 2])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DrawArraysInstanced(uint a, int b, int c, int d)
    {
        if (Drawn(new DrawCall(a, c, 0, 0, d, b, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 3] != IntPtr.Zero))
            ((DrawArraysInstancedFn)Original[TouchAt + 3])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsBaseVertex(uint a, int b, uint c, nint d, int e)
    {
        if (Drawn(new DrawCall(a, b, c, d, 1, 0, e, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 4] != IntPtr.Zero))
            ((DrawElementsBaseVertexFn)Original[TouchAt + 4])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsInstancedBaseVertex(uint a, int b, uint c, nint d, int e, int f)
    {
        if (Drawn(new DrawCall(a, b, c, d, e, 0, f, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 5] != IntPtr.Zero))
            ((DrawElementsInstancedBaseVertexFn)Original[TouchAt + 5])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsBases(uint a, int b, uint c, nint d, int e, int f, uint g)
    {
        if (Drawn(new DrawCall(a, b, c, d, e, 0, f, g))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 6] != IntPtr.Zero))
            ((DrawElementsBasesFn)Original[TouchAt + 6])(a, b, c, d, e, f, g);
    }

    [UnmanagedCallersOnly]
    private static void DrawArraysInstancedBaseInstance(uint a, int b, int c, int d, uint e)
    {
        if (Drawn(new DrawCall(a, c, 0, 0, d, b, 0, e))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 7] != IntPtr.Zero))
            ((DrawArraysInstancedBaseInstanceFn)Original[TouchAt + 7])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DrawRangeElements(uint a, uint b, uint c, int d, uint e, nint f)
    {
        if (Drawn(new DrawCall(a, d, e, f, 1, 0, 0, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 8] != IntPtr.Zero))
            ((DrawRangeElementsFn)Original[TouchAt + 8])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawArrays(uint a, int* b, int* c, int d)
    {
        if (MultiDrawn((a, 0), c, null, b, d)) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 9] != IntPtr.Zero))
            ((MultiDrawArraysFn)Original[TouchAt + 9])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawElements(uint a, int* b, uint c, nint* d, int e)
    {
        if (MultiDrawn((a, c), b, d, null, e)) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 10] != IntPtr.Zero))
            ((MultiDrawElementsFn)Original[TouchAt + 10])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawElementsIndirect(uint a, uint b, nint c, int d, int e)
    {
        if (IndirectDrawn((a, b), c, (d, e))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 11] != IntPtr.Zero))
            ((MultiDrawElementsIndirectFn)Original[TouchAt + 11])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawElementsIndirectCount(uint a, uint b, nint c, nint d, int e, int f)
    {
        if (_parameters != 0 && IndirectDrawn((a, b), c, (e, f), (_parameters, d))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 12] != IntPtr.Zero))
            ((MultiDrawElementsIndirectCountFn)Original[TouchAt + 12])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void DrawElementsIndirect(uint a, uint b, nint c)
    {
        if (IndirectDrawn((a, b), c, (1, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 13] != IntPtr.Zero))
            ((DrawElementsIndirectFn)Original[TouchAt + 13])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void DrawArraysIndirect(uint a, nint b)
    {
        if (IndirectDrawn((a, 0), b, (1, 0))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 14] != IntPtr.Zero))
            ((DrawArraysIndirectFn)Original[TouchAt + 14])(a, b);
    }

    [UnmanagedCallersOnly]
    private static void MultiDrawArraysIndirect(uint a, nint b, int c, int d)
    {
        if (IndirectDrawn((a, 0), b, (c, d))) return;
        Touched(Touch.Draw, 0);
        if (!Rests && Assert(Original[TouchAt + 15] != IntPtr.Zero))
            ((MultiDrawArraysIndirectFn)Original[TouchAt + 15])(a, b, c, d);
    }

    // Vulkan-only: an indirect draw for Drawing, from the commands' buffer bound now
    private static bool IndirectDrawn((uint Mode, uint IndexType) draw, nint offset, (int Draws, int Stride) commands,
        (uint Buffer, nint Offset) count = default)
    {
        if (_indirect == 0 || commands.Draws is <= 0 or > MaxIndirect || offset < 0 || !Assert(commands.Stride >= 0))
            return false;
        var tight = draw.IndexType == 0 ? ArraysCommand : ElementsCommand; // a stride of 0: the commands one after another
        var stride = commands.Stride > 0 ? commands.Stride : tight;
        var indirect = new Indirect(_indirect, offset, commands.Draws, stride, count.Buffer, count.Offset);
        return Drawn(new DrawCall(draw.Mode, 1, draw.IndexType, 0, 1, 0, 0, 0, indirect));
    }

    // Vulkan-only: each multi-draw range is a draw, taken with the first (same state; leftovers missing)
    private static bool MultiDrawn((uint Mode, uint IndexType) draw, int* counts, nint* offsets, int* firsts, int count)
    {
        if (count is <= 0 or > MaxMultiDraws || counts == null || (offsets == null && firsts == null)) return false;
        (LargestMultiDraw, MultiDrawRanges) = (Math.Max(LargestMultiDraw, count), MultiDrawRanges + count);
        try
        {
            for (var i = 0; i < Math.Min(count, MaxMultiDraws); i++)
            {
                var call = offsets != null
                    ? new DrawCall(draw.Mode, counts[i], draw.IndexType, offsets[i], 1, 0, 0, 0)
                    : new DrawCall(draw.Mode, counts[i], 0, 0, 1, firsts[i], 0, 0);
                LaterRange = i > 0;
                if (!Drawn(call) && i == 0) return false;
            }
        }
        finally
        {
            LaterRange = false;
        }

        return true;
    }

    [UnmanagedCallersOnly]
    private static void Clear(uint a)
    {
        if (Cleared(new ClearCall(-1, a, -1, _clearColor, _clearDepth, _clearStencil))) return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 16] != IntPtr.Zero))
            ((ClearFn)Original[TouchAt + 16])(a);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferfv(uint a, int b, float* c)
    {
        if (c != null && Cleared(ClearOf(-1, a, b, c))) return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 17] != IntPtr.Zero))
            ((ClearBufferfvFn)Original[TouchAt + 17])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferiv(uint a, int b, int* c)
    {
        if (a == GlStencilBuffer && c != null && Cleared(new ClearCall(-1, ClearStencilBit, -1, default, 1, c[0])))
            return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 18] != IntPtr.Zero))
            ((ClearBufferivFn)Original[TouchAt + 18])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferuiv(uint a, int b, uint* c)
    {
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 19] != IntPtr.Zero))
            ((ClearBufferuivFn)Original[TouchAt + 19])(a, b, c);
    }

    [UnmanagedCallersOnly]
    private static void ClearBufferfi(uint a, int b, float c, int d)
    {
        if (a == GlDepthStencil && Cleared(new ClearCall(-1, DepthStencilBits, -1, default, c, d))) return;
        Touched(Touch.Clear, 0);
        if (!Rests && Assert(Original[TouchAt + 20] != IntPtr.Zero))
            ((ClearBufferfiFn)Original[TouchAt + 20])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void ClearNamedFramebufferfv(uint a, uint b, int c, float* d)
    {
        a = Framed(a);
        if (a != 0 && d != null && Cleared(ClearOf((int)a, b, c, d))) return;
        Touched(Touch.ClearNamed, a);
        if (!Rests && Assert(Original[TouchAt + 21] != IntPtr.Zero))
            ((ClearNamedFramebufferfvFn)Original[TouchAt + 21])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void ClearNamedFramebufferfi(uint a, uint b, int c, float d, int e)
    {
        a = Framed(a);
        if (a != 0 && b == GlDepthStencil && Cleared(new ClearCall((int)a, DepthStencilBits, -1, default, d, e))) return;
        Touched(Touch.ClearNamed, a);
        if (!Rests && Assert(Original[TouchAt + 22] != IntPtr.Zero))
            ((ClearNamedFramebufferfiFn)Original[TouchAt + 22])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void ClearTexImage(uint a, int b, uint c, uint d, nint e)
    {
        if (TextureClearing is { } clearing && _quiet == 0 &&
            Asked(clearing, new TextureClear(a, b, (0, 0, 0, -1, -1, -1), (c, d), e), false)) return; // Vulkan's
        Touched(Touch.ClearTexture, a);
        if (!Rests) Changed(a); // resting, OpenGL clears it no more than Vulkan: missing, not stale
        if (!Rests && Assert(Original[TouchAt + 23] != IntPtr.Zero))
            ((ClearTexImageFn)Original[TouchAt + 23])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void BlitFramebuffer(int a, int b, int c, int d, int e, int f, int g, int h, uint i, uint j)
    {
        if (Blitted(_readFramebuffer, _framebuffer, [a, b, c, d, e, f, g, h], i, j)) return;
        Touched(Touch.Blit, 0);
        if (!Rests && Assert(Original[TouchAt + 24] != IntPtr.Zero))
            ((BlitFramebufferFn)Original[TouchAt + 24])(a, b, c, d, e, f, g, h, i, j);
    }

    [UnmanagedCallersOnly]
    private static void BlitNamedFramebuffer(uint a, uint b, int c, int d, int e, int f, int g, int h, int i, int j,
        uint k, uint l)
    {
        (a, b) = (Framed(a), Framed(b));
        if (Blitted(a, b, [c, d, e, f, g, h, i, j], k, l)) return;
        Touched(Touch.Blit, b);
        if (!Rests && Assert(Original[TouchAt + 25] != IntPtr.Zero))
            ((BlitNamedFramebufferFn)Original[TouchAt + 25])(a, b, c, d, e, f, g, h, i, j, k, l);
    }

    [UnmanagedCallersOnly]
    private static void CopyImageSubData(uint a, uint b, int c, int d, int e, int f, uint g, uint h, int i, int j,
        int k, int l, int m, int n, int o)
    {
        Touched(Touch.Copy, g);
        if (h != GlRenderbuffer && b != GlRenderbuffer) ImageCopied((a, c, d, e, f), (g, i, j, k, l), (m, n, o));
        else if (h != GlRenderbuffer && !Rests) Changed(g); // into a texture, not a renderbuffer
        if (!Rests && Assert(Original[TouchAt + 26] != IntPtr.Zero))
            ((CopyImageSubDataFn)Original[TouchAt + 26])(a, b, c, d, e, f, g, h, i, j, k, l, m, n, o);
    }

    [UnmanagedCallersOnly]
    private static void CopyTexSubImage2D(uint a, int b, int c, int d, int e, int f, int g, int h)
    {
        Touched(Touch.Copy, BoundName(a));
        if (Follows(Specifiable(a))) FramebufferRead(FollowedName(a), a, b, (c, d), (e, f, g, h));
        if (!Rests && Assert(Original[TouchAt + 27] != IntPtr.Zero))
            ((CopyTexSubImage2DFn)Original[TouchAt + 27])(a, b, c, d, e, f, g, h);
    }

    [UnmanagedCallersOnly]
    private static void ReadPixels(int a, int b, int c, int d, uint e, uint f, nint g)
    {
        var read = Framed(_readFramebuffer);
        if (Reading is not null && ReadAnswered(new ReadCall((int)read, ReadBufferOf(read), 0, 0, (a, b, c, d), (e, f), g)))
            return;
        Touched(Touch.Read, 0);
        if (Assert(Original[TouchAt + 28] != IntPtr.Zero))
            ((ReadPixelsFn)Original[TouchAt + 28])(a, b, c, d, e, f, g);
    }

    [UnmanagedCallersOnly]
    private static void GetTexImage(uint a, int b, uint c, uint d, nint e)
    {
        if (Reading is not null && a == 0x0DE1 && BoundName(a) != 0 && ReadAnswered(new ReadCall(0, 0, BoundName(a), b, (0, 0, -1, -1), (c, d), e)))
            return;
        Touched(Touch.Read, BoundName(a));
        if (Assert(Original[TouchAt + 29] != IntPtr.Zero))
            ((GetTexImageFn)Original[TouchAt + 29])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void DispatchCompute(uint a, uint b, uint c)
    {
        Touched(Touch.Compute, 0);
        if (!Rests && Assert(Original[TouchAt + 30] != IntPtr.Zero))
            ((DispatchComputeFn)Original[TouchAt + 30])(a, b, c);
    }
}
