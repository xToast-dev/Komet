namespace Komet.Vulkan;

// glBlitFramebuffer as vkCmdBlitImage in the open segment: the read buffer's attachment into every draw buffer's, depth into
// depth. Both keep OpenGL's rows (its bottom row is the image's first), so the corners carry over as they are, mirrored ones
// too, and a blit of the same size copies texels exactly; a nearest blit that scales maps pixel centres as OpenGL does. Left
// to OpenGL: the scissor test (OpenGL clips the blit to it), the stencil, a linear blit that scales (the filter weights are
// each driver's own), formats that differ (OpenGL converts), corners beyond either framebuffer (OpenGL clips the other one
// too), a layer or a mipmap level.
internal sealed partial class SceneRenderer
{
    private const uint ColorBlit = 0x4000, DepthBlit = 0x100, Nearest = 0x2600, Linear = 0x2601;

    private readonly List<SharedImage> _blitted = [];
    private readonly (SharedImage From, SharedImage To)[] _pairs = new (SharedImage, SharedImage)[GlTap.MaxBuffers + 1];

    public long Blits { get; private set; }

    public bool Blit(GlTap.BlitCall call)
    {
        if (!Framing || !Clearing() || !Assert(call.Read > 0 && call.Draw > 0)) return false;
        var why = Blitted(call);
        return why.Length == 0 || Leave("a blit " + why);
    }

    // "" when recorded
    private string Blitted(GlTap.BlitCall call)
    {
        var (color, depth) = ((call.Mask & ColorBlit) != 0, (call.Mask & DepthBlit) != 0);
        if ((call.Mask & ~(ColorBlit | DepthBlit)) != 0 || (!color && !depth)) return "of the stencil or of nothing";
        if ((GlTap.State.Caps & (uint)GlTap.Caps.Scissor) != 0) return "with the scissor test on";
        if (call.Filter is not (Nearest or Linear) || (depth && call.Filter != Nearest)) return "with that filter";
        var (from, to) = (Box(call.From), Box(call.To));
        if (from.Width == 0 || from.Height == 0 || to.Width == 0 || to.Height == 0) return "of no pixels";
        if (call.Filter == Linear && (from.Width != to.Width || from.Height != to.Height)) return "scaling linearly";
        if (color && call.ReadAttachment < 0) return "from no known read buffer";
        _blitted.Clear();
        string why;
        if (color && Source(call.Read, call.ReadAttachment, from, out why) is null) return why;
        if (depth && Source(call.Read, GlFramebuffer.Depth, from, out why) is null) return why;
        var target = _terrain.Target(GlTap.State, out why, call.Draw, !depth, foreign: true); // after the sources: those may share
        if (target is null) return why;
        if (to.X < 0 || to.Y < 0 || to.X + to.Width > target.Width || to.Y + to.Height > target.Height)
            return "beyond the framebuffer it draws into";
        var count = 0;
        if (color && Paired(_blitted[0], target.Colors, target.Views, ref count) is { Length: > 0 } c) return c;
        if (depth && Paired(_blitted[^1], [target.Depth], [], ref count) is { Length: > 0 } d) return d;
        if (count == 0) return ""; // nothing attached where OpenGL would write either
        if (!_terrain.Segment(target, SegmentSync.Ordered, _blitted, out why, render: false)) return why;
        if (!_terrain.Frame.Blit(_pairs.AsSpan(0, count), Region(call), call.Filter == Linear)) return "the segment lost an image";
        _terrain.Frame.Wrote(target, false);
        _terrain.Drawn(target);
        _terrain.Unprepared();
        Blits++;
        return "";
    }

    private static (int X, int Y, int Width, int Height) Box((int X0, int Y0, int X1, int Y1) corners) =>
        Assert(corners.X0 != int.MinValue && corners.X1 != int.MinValue)
            ? (Math.Min(corners.X0, corners.X1), Math.Min(corners.Y0, corners.Y1), Math.Abs(corners.X1 - corners.X0),
                Math.Abs(corners.Y1 - corners.Y0))
            : default;

    // The image the blit reads, added to _blitted, when the corners lie in it
    private SharedImage? Source(int fbo, int attachment, (int X, int Y, int Width, int Height) box, out string why)
    {
        var source = _terrain.Read(fbo, attachment, out why);
        if (source is null) return null;
        if (source.Layers > 1 || box.X < 0 || box.Y < 0 || box.X + box.Width > source.Width || box.Y + box.Height > source.Height)
        {
            why = "beyond the framebuffer it reads";
            return null;
        }

        _blitted.Add(source);
        return Assert(_blitted.Count <= 2) ? source : null;
    }

    // The source paired with each image the blit writes; "" when every one fits
    private string Paired(SharedImage source, ReadOnlySpan<SharedImage?> into, ReadOnlySpan<ulong> views, ref int count)
    {
        if (!NotNull(source)) return "from nothing";
        for (var i = 0; i < Math.Min(into.Length, GlTap.MaxBuffers); i++)
        {
            if (into[i] is not { } target) continue;
            if (target.Layers > 1 || (i < views.Length && views[i] != 0)) return "into a layer";
            if (target.Format != source.Format || target == source) return "between formats or within one image";
            if (source.OpaqueAlpha && !target.OpaqueAlpha) return "from RGB into RGBA";
            _pairs[count++] = (source, target);
        }

        return Assert(count <= _pairs.Length) ? "" : "into too many images";
    }

    private static Vk.ImageBlit Region(GlTap.BlitCall call)
    {
        var (from, to) = (call.From, call.To);
        _ = Assert(from.X0 != from.X1) && Assert(to.Y0 != to.Y1);
        return new Vk.ImageBlit
        {
            SourceX0 = from.X0, SourceY0 = from.Y0, SourceX1 = from.X1, SourceY1 = from.Y1, SourceZ1 = 1, TargetX0 = to.X0,
            TargetY0 = to.Y0, TargetX1 = to.X1, TargetY1 = to.Y1, TargetZ1 = 1
        };
    }
}
