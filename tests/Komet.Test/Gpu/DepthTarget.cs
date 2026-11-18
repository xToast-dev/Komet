using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// A depth texture of its own framebuffer, drawn into with the depth test as the engine's passes have it, read back as floats
internal sealed class DepthTarget : IDisposable
{
    private readonly int _texture;

    public DepthTarget(int width, int height, int colors = 0)
    {
        (Width, HeightPixels) = (width, height);
        Framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        _texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _texture);
        GL.TexStorage2D(TextureTarget2d.Texture2D, 1, SizedInternalFormat.DepthComponent32f, width, height);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D,
            _texture, 0);
        Colors = new int[colors];
        for (var i = 0; i < colors; i++)
        {
            Colors[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, Colors[i]);
            GL.TexStorage2D(TextureTarget2d.Texture2D, 1, SizedInternalFormat.Rgba16f, width, height);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i,
                TextureTarget.Texture2D, Colors[i], 0);
        }

        if (colors == 0)
        {
            GL.DrawBuffer(DrawBufferMode.None);
        }
        else
        {
            GL.DrawBuffers(colors, [.. Enumerable.Range(0, colors).Select(i => DrawBuffersEnum.ColorAttachment0 + i)]);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    private int Width { get; }
    private int HeightPixels { get; }
    public int Framebuffer { get; }

    private int[] Colors { get; }

    public void Dispose()
    {
        GL.DeleteFramebuffer(Framebuffer);
        GL.DeleteTexture(_texture);
        foreach (var color in Colors)
        {
            GL.DeleteTexture(color);
        }
    }

    public void Begin()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        GL.Viewport(0, 0, Width, HeightPixels);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.DepthMask(true);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Blend);
        GL.ClearDepth(1);
        GL.ClearColor(0, 0, 0, 0);
        GL.Clear(ClearBufferMask.DepthBufferBit | (Colors.Length > 0 ? ClearBufferMask.ColorBufferBit : 0));
    }

    public float[] Read()
    {
        var depth = new float[Width * HeightPixels];
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, Framebuffer);
        GL.ReadPixels(0, 0, Width, HeightPixels, PixelFormat.DepthComponent, PixelType.Float, depth);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return depth;
    }
}
