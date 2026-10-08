using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

internal static class Parity
{
    public static int Linked(string vertex, string fragment)
    {
        var program = GL.CreateProgram();
        foreach (var (type, source) in (ReadOnlySpan<(ShaderType, string)>)
                 [(ShaderType.VertexShader, vertex), (ShaderType.FragmentShader, fragment)])
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            Assert.That(ok, Is.EqualTo(1), GL.GetShaderInfoLog(shader));
            GL.AttachShader(program, shader);
        }

        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        Assert.That(linked, Is.EqualTo(1), GL.GetProgramInfoLog(program));
        return program;
    }

    public static void Clear(FrameBufferRef framebuffer, float[] color)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        for (var i = 0; i < framebuffer.ColorTextureIds.Length; i++) GL.ClearBuffer(ClearBuffer.Color, i, color);
        var one = 1f;
        GL.DepthMask(true);
        GL.ClearBuffer(ClearBuffer.Depth, 0, ref one);
    }

    // Every attachment of a framebuffer of side by side pixels, as floats: the colors first, the depth last
    public static float[][] Read(FrameBufferRef framebuffer, int side)
    {
        var read = new List<float[]>();
        foreach (var color in framebuffer.ColorTextureIds)
        {
            var values = new float[side * side * 4];
            GL.GetTextureImage(color, 0, PixelFormat.Rgba, PixelType.Float, values.Length * 4, values);
            read.Add(values);
        }

        var depth = new float[side * side];
        GL.GetTextureImage(framebuffer.DepthTextureId, 0, PixelFormat.DepthComponent, PixelType.Float, depth.Length * 4,
            depth);
        read.Add(depth);
        return [.. read];
    }

    public static void Delete(FrameBufferRef framebuffer)
    {
        GL.DeleteFramebuffer(framebuffer.FboId);
        foreach (var texture in framebuffer.ColorTextureIds.Append(framebuffer.DepthTextureId)) GL.DeleteTexture(texture);
    }

    public static float Worst(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Max();

    public static string Differing(float[] drawn, float[] expected)
    {
        var differing = drawn.Zip(expected).Count(p => Math.Abs(p.First - p.Second) > 0.01f);
        var at = -1;
        for (var i = 0; i < drawn.Length && at < 0; i++)
            if (Math.Abs(drawn[i] - expected[i]) > 0.01f)
                at = i;
        return at < 0
            ? "none"
            : $"{differing} values, first at {at}: Vulkan {string.Join(' ', drawn.Skip(at / 4 * 4).Take(4))} OpenGL " +
              $"{string.Join(' ', expected.Skip(at / 4 * 4).Take(4))}";
    }
}
