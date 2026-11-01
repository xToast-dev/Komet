using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// The state taps: each records what it sets, then calls OpenGL. The window counts as an FBO Vulkan stands for (Window,
// 0: none): framebuffer 0 and GL_BACK/GL_FRONT name it, so the engine's last passes and GUI draw into Vulkan's images,
// which Vulkan's swapchain shows.
internal static unsafe partial class GlTap
{
    private const uint GlReadFramebuffer = 0x8CA8, GlBackLeft = 0x402, GlFront = 0x404, GlFrontLeft = 0x400;
    private const uint GlFrontAndBack = 0x408, GlBackBuffer = 0x405;

    private static uint _readFramebuffer;

    // The stand-in framebuffer object; set and cleared by its owner (VulkanRenderer's terrain renderer)
    public static uint Window { get; set; }

    public static int ReadFramebuffer => (int)_readFramebuffer;

    // Framebuffer 0 as the stand-in while there is one
    private static uint Framed(uint framebuffer) => framebuffer == 0 && Window != 0 ? Window : framebuffer;

    // The window's colour buffers as the stand-in's first attachment
    private static uint Windowed(uint buffer) =>
        buffer is GlBackLeft or GlFront or GlBackBuffer or GlFrontLeft or GlFrontAndBack ? GlColor0 : buffer;

    [UnmanagedCallersOnly]
    private static void Enable(uint cap)
    {
        Toggle(cap, true);
        if (Assert(Original[EnableAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[EnableAt])(cap);
    }

    [UnmanagedCallersOnly]
    private static void Disable(uint cap)
    {
        Toggle(cap, false);
        if (Assert(Original[DisableAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[DisableAt])(cap);
    }

    [UnmanagedCallersOnly]
    private static void Enablei(uint cap, uint index)
    {
        if (cap == GlBlend) SetBlending((int)index, true);
        if (Assert(Original[EnableiAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[EnableiAt])(cap, index);
    }

    [UnmanagedCallersOnly]
    private static void Disablei(uint cap, uint index)
    {
        if (cap == GlBlend) SetBlending((int)index, false);
        if (Assert(Original[DisableiAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[DisableiAt])(cap, index);
    }

    [UnmanagedCallersOnly]
    private static void BlendFunc(uint source, uint target)
    {
        SetFunc(AllBuffers, (source, target, source, target));
        if (Assert(Original[FuncAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[FuncAt])(source, target);
    }

    [UnmanagedCallersOnly]
    private static void BlendFuncSeparate(uint sc, uint dc, uint sa, uint da)
    {
        SetFunc(AllBuffers, (sc, dc, sa, da));
        if (Assert(Original[FuncSeparateAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, uint, uint, void>)Original[FuncSeparateAt])(sc, dc, sa, da);
    }

    [UnmanagedCallersOnly]
    private static void BlendFunci(uint buffer, uint source, uint target)
    {
        SetFunc((int)buffer, (source, target, source, target));
        if (Assert(Original[FunciAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, uint, void>)Original[FunciAt])(buffer, source, target);
    }

    [UnmanagedCallersOnly]
    private static void BlendFuncSeparatei(uint buffer, uint sc, uint dc, uint sa, uint da)
    {
        SetFunc((int)buffer, (sc, dc, sa, da));
        if (Assert(Original[FuncSeparateiAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, uint, uint, uint, void>)Original[FuncSeparateiAt])(buffer, sc, dc, sa,
                da);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquation(uint mode)
    {
        SetEquation(AllBuffers, mode, mode);
        if (Assert(Original[EquationAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[EquationAt])(mode);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquationSeparate(uint color, uint alpha)
    {
        SetEquation(AllBuffers, color, alpha);
        if (Assert(Original[EquationSeparateAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[EquationSeparateAt])(color, alpha);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquationi(uint buffer, uint mode)
    {
        SetEquation((int)buffer, mode, mode);
        if (Assert(Original[EquationiAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[EquationiAt])(buffer, mode);
    }

    [UnmanagedCallersOnly]
    private static void BlendEquationSeparatei(uint buffer, uint color, uint alpha)
    {
        SetEquation((int)buffer, color, alpha);
        if (Assert(Original[EquationSeparateiAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, uint, void>)Original[EquationSeparateiAt])(buffer, color, alpha);
    }

    [UnmanagedCallersOnly]
    private static void DepthFunc(uint function)
    {
        if (function is >= 0x200 and <= 0x207 && function != _depthFunc) (_depthFunc, Version) = (function, Version + 1);
        if (Assert(Original[DepthFuncAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[DepthFuncAt])(function);
    }

    [UnmanagedCallersOnly]
    private static void DepthMask(byte on)
    {
        if (_depthMask != (on != 0)) (_depthMask, Version) = (on != 0, Version + 1);
        if (Assert(Original[DepthMaskAt] != IntPtr.Zero))
            ((delegate* unmanaged<byte, void>)Original[DepthMaskAt])(on);
    }

    [UnmanagedCallersOnly]
    private static void ColorMask(byte r, byte g, byte b, byte a)
    {
        SetMask(AllBuffers, r != 0, g != 0, b != 0, a != 0);
        if (Assert(Original[ColorMaskAt] != IntPtr.Zero))
            ((delegate* unmanaged<byte, byte, byte, byte, void>)Original[ColorMaskAt])(r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static void ColorMaski(uint buffer, byte r, byte g, byte b, byte a)
    {
        SetMask((int)buffer, r != 0, g != 0, b != 0, a != 0);
        if (Assert(Original[ColorMaskiAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, byte, byte, byte, byte, void>)Original[ColorMaskiAt])(buffer, r, g, b, a);
    }

    [UnmanagedCallersOnly]
    private static void CullFace(uint face)
    {
        if (_cullFace != face) (_cullFace, Version) = (face, Version + 1);
        if (Assert(Original[CullFaceAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[CullFaceAt])(face);
    }

    [UnmanagedCallersOnly]
    private static void FrontFace(uint face)
    {
        if (_frontFace != face) (_frontFace, Version) = (face, Version + 1);
        if (Assert(Original[FrontFaceAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[FrontFaceAt])(face);
    }

    [UnmanagedCallersOnly]
    private static void SetViewport(int x, int y, int width, int height)
    {
        if (_viewport != (x, y, width, height)) (_viewport, Version) = ((x, y, width, height), Version + 1);
        if (Assert(Original[ViewportAt] != IntPtr.Zero))
            ((delegate* unmanaged<int, int, int, int, void>)Original[ViewportAt])(x, y, width, height);
    }

    [UnmanagedCallersOnly]
    private static void BindFramebuffer(uint target, uint framebuffer)
    {
        Version++;
        framebuffer = Framed(framebuffer); // the window's stand-in for the window
        if (target is GlFramebuffer or GlDrawFramebuffer) _framebuffer = framebuffer;
        if (target is GlFramebuffer or GlReadFramebuffer) _readFramebuffer = framebuffer;
        if (Assert(Original[BindFramebufferAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[BindFramebufferAt])(target, framebuffer);
    }

    [UnmanagedCallersOnly]
    private static void PolygonOffset(float factor, float units)
    {
        if ((_offsetFactor, _offsetUnits) != (factor, units))
            (_offsetFactor, _offsetUnits, Version) = (factor, units, Version + 1);
        if (Assert(Original[OffsetAt] != IntPtr.Zero))
            ((delegate* unmanaged<float, float, void>)Original[OffsetAt])(factor, units);
    }

    [UnmanagedCallersOnly]
    private static void SetDrawBuffers(int count, uint* buffers)
    {
        var windowed = stackalloc uint[16];
        if (_framebuffer != 0 && _framebuffer == Window && buffers != null && count is > 0 and <= 16)
        {
            for (var i = 0; i < Math.Min(count, 16); i++) windowed[i] = Windowed(buffers[i]);
            buffers = windowed;
        }

        if (_framebuffer > 0 && buffers != null && count is > 0 and <= 16)
            SetBuffers(_framebuffer, new ReadOnlySpan<uint>(buffers, count));
        if (Assert(Original[DrawBuffersAt] != IntPtr.Zero))
            ((delegate* unmanaged<int, uint*, void>)Original[DrawBuffersAt])(count, buffers);
    }

    [UnmanagedCallersOnly]
    private static void DrawBuffer(uint buffer)
    {
        if (_framebuffer != 0 && _framebuffer == Window) buffer = Windowed(buffer);
        if (_framebuffer > 0) SetBuffers(_framebuffer, [buffer]);
        if (Assert(Original[DrawBufferAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[DrawBufferAt])(buffer);
    }

    [UnmanagedCallersOnly]
    private static void NamedDrawBuffers(uint framebuffer, int count, uint* buffers)
    {
        var windowed = stackalloc uint[16];
        if (framebuffer == 0 && Window != 0 && buffers != null && count is > 0 and <= 16)
        {
            for (var i = 0; i < Math.Min(count, 16); i++) windowed[i] = Windowed(buffers[i]);
            framebuffer = Window;
            buffers = windowed;
        }

        if (framebuffer > 0 && buffers != null && count is > 0 and <= 16)
            SetBuffers(framebuffer, new ReadOnlySpan<uint>(buffers, count));
        if (Assert(Original[NamedDrawBuffersAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, int, uint*, void>)Original[NamedDrawBuffersAt])(framebuffer, count, buffers);
    }

    [UnmanagedCallersOnly]
    private static void NamedDrawBuffer(uint framebuffer, uint buffer)
    {
        if (framebuffer == 0 && Window != 0) (framebuffer, buffer) = (Window, Windowed(buffer));
        if (framebuffer > 0) SetBuffers(framebuffer, [buffer]);
        if (Assert(Original[NamedDrawBufferAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, uint, void>)Original[NamedDrawBufferAt])(framebuffer, buffer);
    }

    [UnmanagedCallersOnly]
    private static void DeleteProgram(uint program)
    {
        Version++;
        if (program != 0 && ProgramGone is { } gone) Heard(gone, program);
        _ = ProgramRecords.Remove(program);
        _ = Programs.Remove(program);
        if (program == _current) _using = null;
        if (Assert(Original[DeleteProgramAt] != IntPtr.Zero))
            ((delegate* unmanaged<uint, void>)Original[DeleteProgramAt])(program);
    }

    [UnmanagedCallersOnly]
    private static void DeleteFramebuffers(int count, uint* framebuffers)
    {
        Version++;
        var (draw, read, window) = (false, false, false);
        for (var i = 0; framebuffers != null && i < Math.Min(count, MaxDeleted); i++)
        {
            _ = DrawBuffers.Remove(framebuffers[i]);
            _ = DrawOutputs.Remove(framebuffers[i]);
            _ = Reads.Remove(framebuffers[i]);
            _ = FramebufferRecords.Remove(framebuffers[i]);
            // deleting a bound framebuffer binds 0 in its place
            if (framebuffers[i] == _framebuffer) (_framebuffer, draw) = (0, true);
            if (framebuffers[i] == _readFramebuffer) (_readFramebuffer, read) = (0, true);
            window |= framebuffers[i] == Window && Window != 0;
            if (framebuffers[i] != 0 && FramebufferGone is { } gone) Heard(gone, framebuffers[i]);
        }

        if (Assert(Original[DeleteFramebuffersAt] != IntPtr.Zero))
            ((delegate* unmanaged<int, uint*, void>)Original[DeleteFramebuffersAt])(count, framebuffers);
        if (!window) Restand(draw, read);
    }

    // The engine's 0 is the window's stand-in: a deleted bound framebuffer leaves the stand-in bound, not the real
    // window. Bound past the tap, so no listener hears it
    private static void Restand(bool draw, bool read)
    {
        if (Window == 0 || !(draw || read) || !Assert(Original[BindFramebufferAt] != IntPtr.Zero)) return;
        var target = draw ? GlDrawFramebuffer : GlReadFramebuffer;
        if (draw && read) target = GlFramebuffer;
        ((delegate* unmanaged<uint, uint, void>)Original[BindFramebufferAt])(target, Window);
        if (draw) _framebuffer = Framed(0);
        if (read) _readFramebuffer = Framed(0);
    }

    // Whoever shares a texture about to go lets go first (the name may come back for another texture at once)
    [UnmanagedCallersOnly]
    private static void DeleteTextures(int count, uint* textures)
    {
        (Version, TextureVersion, DeleteVersion) = (Version + 1, TextureVersion + 1, DeleteVersion + 1);
        var deleting = Deleting;
        for (var i = 0; textures != null && i < Math.Min(count, MaxDeleted); i++)
        {
            var texture = textures[i];
            if (texture != 0 && deleting is not null) Heard(deleting, texture);
            Unbound(texture);
            if (Leaned > 0) LeanGone(texture, true);
            if (GlBehind.Count > 0) _ = GlBehind.Remove(texture);
        }

        if (Assert(Original[DeleteTexturesAt] != IntPtr.Zero))
            ((delegate* unmanaged<int, uint*, void>)Original[DeleteTexturesAt])(count, textures);
    }

    // Every unit that held a deleted texture holds none
    private static void Unbound(uint texture)
    {
        if (texture == 0) return;
        _ = TextureTargets.Remove(texture);
        _ = TextureRecords.Remove(texture);
        for (var unit = 0; unit < MaxUnits; unit++)
        {
            if (UnitTextures[unit] == texture) UnitTextures[unit] = 0;
            if (UnitArrays[unit] == texture) UnitArrays[unit] = 0;
            if (UnitCubes[unit] == texture) UnitCubes[unit] = 0;
        }
    }
}
