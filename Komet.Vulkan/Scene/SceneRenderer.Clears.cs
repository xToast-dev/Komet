using System.Numerics;

namespace Komet.Vulkan;

// Clears become vkCmdClearAttachments in the segment's rendering (all layers, like OpenGL); clears of the window itself
// are missing. Vulkan-only: a masked clear (glColorMask/glStencilMask dropping held bits) is drawn as one full-target
// triangle (ClearDrawn): color through each attachment's mask, depth always-pass where cleared, stencil always-replace
// through the write mask, scissored like OpenGL's clear. Komet's own program, ported once.
internal sealed unsafe partial class SceneRenderer
{
    private const uint FullMask = 0xF;
    private const int ClearProgram = -1;
    private const string ClearVertex = """
        #version 450 core
        uniform float komet_depth;
        void main()
        {
            vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(corner * 2.0 - 1.0, komet_depth * 2.0 - 1.0, 1.0);
        }
        """;

    private const string ClearFragment = """
        #version 450 core
        uniform vec4 komet_color;
        layout(location = 0) out vec4 c0;
        layout(location = 1) out vec4 c1;
        layout(location = 2) out vec4 c2;
        layout(location = 3) out vec4 c3;
        layout(location = 4) out vec4 c4;
        layout(location = 5) out vec4 c5;
        layout(location = 6) out vec4 c6;
        layout(location = 7) out vec4 c7;
        void main()
        {
            c0 = komet_color; c1 = komet_color; c2 = komet_color; c3 = komet_color;
            c4 = komet_color; c5 = komet_color; c6 = komet_color; c7 = komet_color;
        }
        """;

    private static readonly string[] ClearHot = ["komet_color", "komet_depth"];

    private readonly OwnProgram _clear = new("komet-clear", ClearVertex, ClearFragment, ClearHot);

    public System.Func<bool> Clearing { get; set; } = () => false;

    public bool Clear(GlTap.ClearCall call)
    {
        if (!Framing || !Clearing() || !Assert(call.Into >= -1)) return false;
        var fbo = call.Into >= 0 ? call.Into : GlTap.DrawFramebuffer;
        var state = GlTap.State;
        var depth = (call.Mask & GlTap.ClearDepthMask) != 0 && state.DepthWrite;
        var colors = (call.Mask & GlTap.ClearColorMask) != 0;
        // the stencil too, through its write mask
        var stencils = (call.Mask & GlTap.ClearStencilMask) != 0 && GlTap.StencilValues.Front.Write != 0;
        if (Unclearable(fbo, (depth || stencils, colors)) is { } unclearable) return unclearable;
        var target = _terrain.Target(state, out var why, fbo, !depth && !stencils);
        if (target is null) return Leave(why);
        Span<Vk.ClearAttachment> attachments = stackalloc Vk.ClearAttachment[GlTap.MaxBuffers + 1];
        var count = Attachments(attachments, target, call, (colors, depth, stencils), state);
        if (count < 0) return ClearDrawn(target, call, (colors, depth, stencils), state); // through masks
        if (count == 0) return true; // nothing attached where OpenGL would clear
        _sampled.Clear();
        var scissor = (state.Caps & (uint)GlTap.Caps.Scissor) != 0;
        if (!_terrain.Segment(target, _sampled, out why)) return Leave(why);
        Cleared(target, attachments[..count], scissor);
        return true;
    }

    // Stencil bit dropped (images have none), empty clears done, window clears without stand-in missing; null: cleared
    private bool? Unclearable(int fbo, (bool Depth, bool Colors) clears)
    {
        if (!Assert(fbo >= 0)) return null;
        if (!clears.Depth && !clears.Colors) return true; // nothing written, or the stencil alone
        return fbo == 0 ? Leave("a clear of the window itself (no stand-in for it)") : null;
    }

    // Masks dropping only absent channels are fine, fully masked attachments skipped; -1 if the mask drops held channels
    // or stencil bits (ClearDrawn's job); masks null: as the engine called it, everything written
    private static int Attachments(Span<Vk.ClearAttachment> into, VulkanFrame.Target target, GlTap.ClearCall call,
        (bool Colors, bool Depth, bool Stencil) clears, GlTap.Fixed? masks)
    {
        var count = 0;
        for (var i = 0; clears.Colors && i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
        {
            if (target.Colors[i] is not { } color || (call.Output >= 0 && call.Output != i)) continue;
            var mask = masks is { } state ? (state.Blend[i] >> 22) & FullMask : FullMask;
            if (mask == 0) continue;
            if (mask != FullMask && (mask & Components(color)) != Components(color)) return -1;
            into[count++] = new Vk.ClearAttachment
            {
                Aspect = Vk.AspectColor, Attachment = (uint)i, Red = call.Color.R, Green = call.Color.G,
                Blue = call.Color.B, Alpha = call.Color.A
            };
        }

        var stencil = clears.Stencil && target.Depth is { } held && (held.Format.Aspect & Vk.AspectStencil) != 0;
        if (stencil && (GlTap.StencilValues.Front.Write & 0xFF) != 0xFF) return -1;
        if ((clears.Depth || stencil) && target.Depth is not null)
            into[count++] = new Vk.ClearAttachment
            {
                Aspect = (clears.Depth ? Vk.AspectDepth : 0) | (stencil ? Vk.AspectStencil : 0),
                Red = (float)Math.Clamp(call.Depth, 0, 1), // VkClearDepthStencilValue's depth, then its stencil as a uint
                Green = stencil ? BitConverter.UInt32BitsToSingle((uint)call.Stencil & 0xFF) : 0
            };
        return count;
    }

    // R, G, B, A as bits 1, 2, 4, 8
    private static uint Components(SharedImage image)
    {
        if (image.OpaqueAlpha) return 0x7;
        return image.Format.Vulkan switch
        {
            Vk.FormatR8 or Vk.FormatR16F or Vk.FormatR32F or Vk.FormatR16 => 0x1,
            Vk.FormatRg8 or Vk.FormatRg16F => 0x3,
            Vk.FormatB10G11R11F => 0x7,
            _ => FullMask
        };
    }

    private void Cleared(VulkanFrame.Target target, ReadOnlySpan<Vk.ClearAttachment> attachments, bool scissor)
    {
        var frame = _terrain.Frame;
        (int X, int Y, int Width, int Height) box = scissor ? GlTap.Scissor : (0, 0, target.Width, target.Height);
        var (x, y) = (Math.Clamp(box.X, 0, target.Width), Math.Clamp(box.Y, 0, target.Height));
        var (right, top) = (Math.Clamp(box.X + box.Width, x, target.Width),
            Math.Clamp(box.Y + box.Height, y, target.Height));
        if (right > x && top > y)
        {
            var rect = new Vk.Rect { X = x, Y = y, Width = (uint)(right - x), Height = (uint)(top - y) };
            frame.PostClear(attachments, rect, (uint)target.Layers);
        }

        _terrain.Drawn(target);
        _terrain.Unprepared();
        Clears++;
    }

    private bool ClearDrawn(VulkanFrame.Target target, GlTap.ClearCall call, (bool Colors, bool Depth, bool Stencil) clears,
        GlTap.Fixed gl)
    {
        if (OwnPort(_clear, wait: true) is not { } port) return Leave($"a clear through a mask: no program to draw it ({_clear.Unported})");
        var state = ClearState(target, call, clears, gl);
        var into = _terrain.Formats(target, ref state);
        var key = new PipelineKey(ClearProgram, 0, "clear", into.Key, state);
        if (!_pipelines.TryMade(key, out var pipeline)) // a captured local would be a closure per clear, made or not
            pipeline = _pipelines.Get(key, ("Komet's clear", port), FullRecipe(state, into.Target), out _);
        if (pipeline is null) return Leave("a clear through a mask: no pipeline to draw it (see the log)");
        _sampled.Clear();
        if (!_terrain.Segment(target, _sampled, out var why)) return Leave(why);
        var frame = _terrain.Frame;
        frame.Viewport((0, 0, target.Width, target.Height));
        frame.Scissor((gl.Caps & (uint)GlTap.Caps.Scissor) != 0 ? GlTap.Scissor : null);
        var words = port.HotUniforms.Size / 4;
        var (_, _, push) = frame.Room(0, 0, words);
        Pushed(frame.PushWords(push, words), port.HotUniforms, call);
        if ((state.Caps & (uint)GlTap.Caps.Stencil) != 0) frame.Post(Stenciling(Replacing(call)));
        frame.Post(new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.Draw, Draw = VulkanFrame.DrawKind.Direct, Pipeline = pipeline.Pipeline,
            Layout = pipeline.Layout, Count = 3, Instances = 1, Push = push, PushBytes = words * 4
        });
        _terrain.Drawn(target);
        _terrain.Unprepared();
        Clears++;
        return Assert(words > 0);
    }

    // How to build the masked clear's pipeline for the target's formats and state; asked only while it is not made, since
    // the closure is garbage on every call
    private System.Func<GlslPort.Ported, Build<TerrainPipeline>> FullRecipe(GlTap.Fixed state, TerrainPipeline.Target into)
    {
        var device = _terrain.Device;
        return p => TerrainPipeline.Started(device, p, [], (state, into, Vk.TopologyTriangles));
    }

    // Color through each mask, no blending; depth always-pass, written where cleared; stencil replaced on both faces
    private static GlTap.Fixed ClearState(VulkanFrame.Target target, GlTap.ClearCall call,
        (bool Colors, bool Depth, bool Stencil) clears, GlTap.Fixed gl)
    {
        var stencil = clears.Stencil && target.Depth is { } depth && (depth.Format.Aspect & Vk.AspectStencil) != 0;
        const uint always = 7, replace = 2;
        var face = always | (replace << 3) | (replace << 6) | (replace << 9);
        var caps = (clears.Depth || stencil ? (uint)GlTap.Caps.DepthTest : 0) | (stencil ? (uint)GlTap.Caps.Stencil : 0);
        // the colour write masks alone: blending off
        return FullTarget(target, (clears.Colors, call.Output, gl.Blend), caps, always | (clears.Depth ? 8u : 0),
            stencil ? face | (face << 12) : 0);
    }

    private static void Pushed(Span<uint> into, GlslPort.Block hot, GlTap.ClearCall call)
    {
        ReadOnlySpan<Vector4> values =
        [
            new(call.Color.R, call.Color.G, call.Color.B, call.Color.A), new((float)Math.Clamp(call.Depth, 0, 1), 0, 0, 0)
        ];
        PushedValues(into, hot, ClearHot, values);
    }

    // Stencil clear value as both faces' reference, written through the front write mask (as OpenGL's clear)
    private static ((uint, uint, uint), (uint, uint, uint)) Replacing(GlTap.ClearCall call)
    {
        var write = GlTap.StencilValues.Front.Write;
        var reference = (uint)call.Stencil & 0xFF;
        return ((reference, 0xFF, write), (reference, 0xFF, write));
    }
}
