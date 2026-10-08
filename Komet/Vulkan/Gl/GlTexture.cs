using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// What OpenGL holds of a texture (target, size, format, sampled levels, sampling). Each question waits for Mesa's glthread:
// asked once where a texture is taken over (a framebuffer adopted, a copy made), never per draw.
internal static class GlTexture
{
    public const int Array2D = 0x8C1A;
    private const int MaxLevels = 16, TextureTarget = 0x1006, TextureWidth = 0x1000, TextureHeight = 0x1001;
    private const int TextureDepth = 0x8071, InternalFormat = 0x1003, MaxLevel = 0x813D, CompareMode = 0x884C;
    private const int BorderColor = 0x1004;

    public readonly record struct Description(int Kind, int Width, int Height, int Depth, int Format, int LevelCount);

    public static Description Of(int texture)
    {
        if (!Assert(texture > 0) || !Assert(texture < int.MaxValue)) return default;
        GL.GetTextureParameter(texture, (GetTextureParameter)TextureTarget, out int target);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureWidth, out int width);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureHeight, out int height);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureDepth, out int depth);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)InternalFormat, out int format);
        return new Description(target, width, height, depth, format, width > 0 ? Levels(texture) : 0);
    }

    // The levels that exist from level 0 on, up to GL_TEXTURE_MAX_LEVEL
    public static int Levels(int texture)
    {
        GL.GetTextureParameter(texture, (GetTextureParameter)MaxLevel, out int max);
        var levels = 1;
        for (var level = 1; level < Math.Min(MaxLevels, max + 1); level++)
        {
            GL.GetTextureLevelParameter(texture, level, (GetTextureParameter)TextureWidth, out int width);
            if (width <= 0) break;
            levels++;
        }

        _ = Assert(texture > 0) && Assert(levels <= MaxLevels);
        return levels;
    }

    public static Samplers.State Sampling(int texture)
    {
        if (!Assert(texture > 0) || !Assert(texture < int.MaxValue)) return default;
        GL.GetTextureParameter(texture, GetTextureParameter.TextureMinFilter, out int min);
        GL.GetTextureParameter(texture, GetTextureParameter.TextureMagFilter, out int mag);
        GL.GetTextureParameter(texture, GetTextureParameter.TextureWrapS, out int wrap);
        GL.GetTextureParameter(texture, (GetTextureParameter)CompareMode, out int compare);
        var border = new float[4];
        GL.GetTextureParameter(texture, (GetTextureParameter)BorderColor, border);
        return Samplers.FromGl(min, mag, wrap, compare != 0) with { WhiteBorder = border[0] > 0.5f };
    }

    // Copies one level between two textures through a pair of framebuffer objects (created, not generated: the DSA calls need
    // the object) that hold nothing between copies, so no texture counts as a render target
    public sealed class Copier : IDisposable
    {
        private int _read, _draw;

        // layer: a cube map's face (or an array's layer) in both, or -1 for a plain texture
        public void Copy(int from, int to, int level, (int Width, int Height) size, bool depth, int layer = -1)
        {
            if (!Assert(from > 0 && to > 0) || !Assert(level >= 0 && size.Width > 0 && size.Height > 0)) return;
            if (_read == 0)
            {
                GL.CreateFramebuffers(1, out _read);
                GL.CreateFramebuffers(1, out _draw);
            }

            var attachment = depth ? FramebufferAttachment.DepthAttachment : FramebufferAttachment.ColorAttachment0;
            if (layer < 0)
            {
                GL.NamedFramebufferTexture(_read, attachment, from, level);
                GL.NamedFramebufferTexture(_draw, attachment, to, level);
            }
            else
            {
                GL.NamedFramebufferTextureLayer(_read, attachment, from, level, layer);
                GL.NamedFramebufferTextureLayer(_draw, attachment, to, level, layer);
            }

            if (!depth)
            {
                GL.NamedFramebufferReadBuffer(_read, ReadBufferMode.ColorAttachment0);
                GL.NamedFramebufferDrawBuffer(_draw, DrawBufferMode.ColorAttachment0);
            }

            if (Assert(_read > 0 && _draw > 0)) // nearest and unscissored: the engine may have left the scissor test on
            {
                var scissored = GlTap.Tapped
                    ? (GlTap.State.Caps & (uint)GlTap.Caps.Scissor) != 0
                    : GL.IsEnabled(EnableCap.ScissorTest);
                if (scissored) GL.Disable(EnableCap.ScissorTest);
                GL.BlitNamedFramebuffer(_read, _draw, 0, 0, size.Width, size.Height, 0, 0, size.Width, size.Height,
                    depth ? ClearBufferMask.DepthBufferBit : ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
                if (scissored) GL.Enable(EnableCap.ScissorTest);
            }

            GL.NamedFramebufferTexture(_read, attachment, 0, 0);
            GL.NamedFramebufferTexture(_draw, attachment, 0, 0);
        }

        public void Dispose()
        {
            _ = Assert(_read >= 0) && Assert(_draw >= 0);
            if (_read != 0) GL.DeleteFramebuffers(2, [_read, _draw]);
            (_read, _draw) = (0, 0);
        }
    }
}
