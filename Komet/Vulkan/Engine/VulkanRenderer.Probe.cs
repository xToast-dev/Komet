using System.Globalization;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Every readback waits for the GPU, which is why the probe runs only at the frames in ProbeAt.
internal static partial class VulkanRenderer
{
    private const int Grid = 5;
    private static readonly long[] ProbeAt = [60, 300, 3000];

    private static void Probe(TerrainRenderer renderer, List<FrameBufferRef> framebuffers)
    {
        if (!NotNull(renderer) || !NotNull(framebuffers) || Array.IndexOf(ProbeAt, renderer.Frame.Number) < 0) return;
        var lines = new List<string>
        {
            // index:GL name, ! when not on shared images
            "framebuffers " + string.Join(" ", framebuffers.Select((f, i) => Named(renderer, f, i)))
        };
        foreach (var at in Shared().Take(8).ToArray().Bounded(8))
            if (at < framebuffers.Count && framebuffers[at] is { } framebuffer)
                lines.Add(Probed(at, framebuffer, renderer));
        foreach (var (texture, image) in renderer.Copies.Copied.Where(c => c.Image.Exported).Take(8).ToArray().Bounded(8))
            lines.Add($"copy of {texture} ({image.Width}x{image.Height}): {Texel(texture)} vs {Texel(image.Texture)}");
        var state = GlTap.State;
        lines.Add($"GL: caps 0x{state.Caps:X}, depth word 0x{state.Depth:X}, viewport {GlTap.Viewport}, " +
                  $"framebuffer {GlTap.DrawFramebuffer}");
        _ = Assert(lines.Count <= 32);
        _logger?.Notification("Komet: Vulkan probe at frame {0}: {1}", renderer.Frame.Number, string.Join(" | ", lines));
    }

    private static string Named(TerrainRenderer renderer, FrameBufferRef? framebuffer, int at)
    {
        if (framebuffer is null || !Assert(at >= 0)) return $"{at}:-";
        var mark = renderer.Targets.Swapped(framebuffer) ? "" : "!";
        return $"{at}:{framebuffer.FboId}{mark}";
    }

    private static string Probed(int at, FrameBufferRef framebuffer, TerrainRenderer renderer)
    {
        if (!NotNull(framebuffer) || !Assert(at >= 0)) return "";
        var shared = renderer.Targets.Swapped(framebuffer) ? "shared" : "NOT shared";
        var status = GL.CheckNamedFramebufferStatus(framebuffer.FboId, FramebufferTarget.DrawFramebuffer);
        var attached = GlFramebuffer.Attachments(framebuffer.FboId); // what is drawn into, not what the engine names
        var colors = new List<string>();
        for (var i = 0; i < GlFramebuffer.Colors; i++)
            if (attached[i] is { Texture: > 0, Layered: false } a)
                colors.Add($" color{i} (texture {a.Texture}{(a.Layer > 0 ? $" layer {a.Layer}" : "")}" +
                           $"{(renderer.Targets.Find(a.Texture) is null ? ", not shared" : "")}) " +
                           Colors(a.Texture, framebuffer, a.Layer));
        var depthTexture = attached[GlFramebuffer.Depth];
        var depth = depthTexture.Texture > 0 ? " depth " + Depths(depthTexture.Texture, framebuffer) : "";
        _ = Assert(colors.Count <= GlFramebuffer.Colors);
        return $"fb {at} ({framebuffer.Width}x{framebuffer.Height}, {shared}, {status}):{string.Concat(colors)}{depth}";
    }

    private static string Colors(int texture, FrameBufferRef framebuffer, int layer)
    {
        var (sum, black) = (new float[4], 0);
        var pixel = new float[4];
        for (var i = 0; i < Grid * Grid; i++)
        {
            var (x, y) = Spot(i, framebuffer);
            GL.GetTextureSubImage(texture, 0, x, y, layer, 1, 1, 1, PixelFormat.Rgba, PixelType.Float, 16, pixel);
            for (var c = 0; c < 4; c++) sum[c] += pixel[c] / (Grid * Grid);
            if (pixel[0] + pixel[1] + pixel[2] < 0.01f) black++;
        }

        _ = Assert(black <= Grid * Grid);
        return string.Create(CultureInfo.InvariantCulture,
            $"mean ({sum[0]:0.00} {sum[1]:0.00} {sum[2]:0.00} {sum[3]:0.00}), {black}/{Grid * Grid} black");
    }

    private static string Depths(int texture, FrameBufferRef framebuffer)
    {
        var (min, max) = (float.MaxValue, float.MinValue);
        var depth = new float[1];
        for (var i = 0; i < Grid * Grid; i++)
        {
            var (x, y) = Spot(i, framebuffer);
            GL.GetTextureSubImage(texture, 0, x, y, 0, 1, 1, 1, PixelFormat.DepthComponent, PixelType.Float, 4, depth);
            (min, max) = (Math.Min(min, depth[0]), Math.Max(max, depth[0]));
        }

        _ = Assert(min <= max) && Assert(Finite(max));
        return string.Create(CultureInfo.InvariantCulture, $"{min:0.00000}..{max:0.00000}");
    }

    private static (int X, int Y) Spot(int i, FrameBufferRef framebuffer)
    {
        _ = Assert(i is >= 0 and < Grid * Grid) && Assert(framebuffer.Width > 0);
        return ((i % Grid * 2 + 1) * framebuffer.Width / (2 * Grid), (i / Grid * 2 + 1) * framebuffer.Height / (2 * Grid));
    }

    // A depth texture is read as depth: OpenGL refuses to read it as colors
    private static string Texel(int texture)
    {
        if (!Assert(texture > 0)) return "";
        var gl = GlTexture.Of(texture);
        _ = Assert(gl.Width >= 0);
        if (SharedFormat.Of(gl.Format)?.Aspect == Vk.AspectDepth)
        {
            var depth = new float[1];
            GL.GetTextureSubImage(texture, 0, gl.Width / 2, gl.Height / 2, 0, 1, 1, 1, PixelFormat.DepthComponent,
                PixelType.Float, 4, depth);
            return string.Create(CultureInfo.InvariantCulture, $"depth {depth[0]:0.00000}");
        }

        var pixel = new byte[4];
        GL.GetTextureSubImage(texture, 0, gl.Width / 2, gl.Height / 2, 0, 1, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte,
            4, pixel);
        return $"{pixel[0]},{pixel[1]},{pixel[2]},{pixel[3]}";
    }
}
