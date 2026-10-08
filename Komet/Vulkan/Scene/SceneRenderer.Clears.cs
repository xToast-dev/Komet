namespace Komet.Vulkan;

// Clears become vkCmdClearAttachments inside the segment's rendering; a clear through a partial color mask, or into the
// window, is left to OpenGL.
internal sealed unsafe partial class SceneRenderer
{
    private const uint FullMask = 0xF;

    public System.Func<bool> Clearing { get; set; } = () => false;

    public System.Func<bool> ClearDeferring { get; set; } = () => false;

    public bool Clear(GlTap.ClearCall call)
    {
        if (!Framing) return Kept(call);
        if (!Clearing() || !Assert(call.Into >= -1)) return false;
        var fbo = call.Into >= 0 ? call.Into : GlTap.DrawFramebuffer;
        var state = GlTap.State;
        var depth = (call.Mask & GlTap.ClearDepthMask) != 0 && state.DepthWrite;
        var colors = (call.Mask & GlTap.ClearColorMask) != 0;
        if (fbo == 0 || (!depth && !colors) || (call.Mask & ~(GlTap.ClearDepthMask | GlTap.ClearColorMask)) != 0)
            return false; // the window, nothing OpenGL would write, or the stencil
        var target = _terrain.Target(state, out var why, fbo, !depth, foreign: true);
        if (target is null) return Leave(why);
        Span<Vk.ClearAttachment> attachments = stackalloc Vk.ClearAttachment[GlTap.MaxBuffers + 1];
        var count = Attachments(attachments, target, call, (colors, depth), state);
        if (count < 0) return Leave("a clear through a color mask");
        if (count == 0) return true; // nothing attached where OpenGL would clear
        _sampled.Clear();
        var sync = ClearDeferring() ? SegmentSync.Deferred : SegmentSync.Ordered;
        var scissor = (state.Caps & (uint)GlTap.Caps.Scissor) != 0;
        if (!_terrain.Segment(target, sync, _sampled, out why, whole: Whole(target, depth, scissor))) return Leave(why);
        Cleared(target, attachments[..count], scissor);
        return true;
    }

    // The clear writes every texel of the depth: Komet's depth taking the engine's over needs no copy of it (TerrainRenderer)
    private static bool Whole(VulkanFrame.Target target, bool depth, bool scissor)
    {
        if (!depth || target.Depth is not { } image || (image.Width, image.Height) != (target.Width, target.Height))
            return false;
        var (x, y, width, height) = GlTap.Scissor;
        return Assert(target.Width > 0) &&
               (!scissor || (x <= 0 && y <= 0 && x + width >= target.Width && y + height >= target.Height));
    }

    private static int Attachments(Span<Vk.ClearAttachment> into, VulkanFrame.Target target, GlTap.ClearCall call,
        (bool Colors, bool Depth) clears, GlTap.Fixed? masks)
    {
        if (!NotNull(target) || !Assert(into.Length > GlTap.MaxBuffers)) return 0;
        var count = 0;
        for (var i = 0; clears.Colors && i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
        {
            if (target.Colors[i] is null || (call.Output >= 0 && call.Output != i)) continue;
            if (masks is { } state && ((state.Blend[i] >> 22) & FullMask) != FullMask) return -1;
            into[count++] = new Vk.ClearAttachment
            {
                Aspect = Vk.AspectColor, Attachment = (uint)i, Red = call.Color.R, Green = call.Color.G,
                Blue = call.Color.B, Alpha = call.Color.A
            };
        }

        if (clears.Depth && target.Depth is not null)
            into[count++] = new Vk.ClearAttachment
            {
                Aspect = Vk.AspectDepth, Red = (float)Math.Clamp(call.Depth, 0, 1) // VkClearDepthStencilValue.depth
            };
        return count;
    }

    private void Cleared(VulkanFrame.Target target, ReadOnlySpan<Vk.ClearAttachment> attachments, bool scissor)
    {
        if (!NotNull(target) || !Assert(attachments.Length is > 0 and <= GlTap.MaxBuffers + 1)) return;
        var frame = _terrain.Frame;
        (int X, int Y, int Width, int Height) box = scissor ? GlTap.Scissor : (0, 0, target.Width, target.Height);
        var (x, y) = (Math.Clamp(box.X, 0, target.Width), Math.Clamp(box.Y, 0, target.Height));
        var (right, top) = (Math.Clamp(box.X + box.Width, x, target.Width),
            Math.Clamp(box.Y + box.Height, y, target.Height));
        if (right > x && top > y)
        {
            var rect = new Vk.Rect { X = x, Y = y, Width = (uint)(right - x), Height = (uint)(top - y) };
            frame.Post(new VulkanFrame.Op
            {
                Kind = VulkanFrame.OpKind.Clear, Ref = new VulkanFrame.Clearing(attachments.ToArray(), rect)
            });
        }

        frame.Wrote(target, false);
        _terrain.Drawn(target);
        _terrain.Unprepared();
        Clears++;
    }
}
