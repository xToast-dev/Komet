using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;

namespace Komet.Options;

// Blurred at quarter size (1/16 of the pixels per pass), so it runs every frame; the last pass flips rows upright. The
// targets exist only while the screen is open: made at its first Draw, released when it closes.
internal sealed class Backdrop(ICoreClientAPI capi) : IDisposable
{
    private const int Shrink = 4, Rounds = 2, Buffers = 3, MaxSize = 16384;

    private const string Vertex = """
                                  #version 330 core
                                  out vec2 uv;
                                  void main()
                                  {
                                      vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                                      uv = corner;
                                      gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
                                  }
                                  """;

    // shrink: four bilinear fetches average a 4x4 block; else nine Gaussian taps (sigma 2) as five bilinear fetches
    private const string Fragment = """
                                    #version 330 core
                                    in vec2 uv;
                                    out vec4 color;
                                    uniform sampler2D source;
                                    uniform vec2 stride;
                                    uniform int shrink;
                                    uniform int flip;
                                    void main()
                                    {
                                        vec2 at = flip == 1 ? vec2(uv.x, 1.0 - uv.y) : uv;
                                        vec3 sum;
                                        if (shrink == 1)
                                        {
                                            sum = (texture(source, at + stride * vec2(-1.0, -1.0)).rgb + texture(source, at + stride * vec2(1.0, -1.0)).rgb
                                                + texture(source, at + stride * vec2(-1.0, 1.0)).rgb + texture(source, at + stride * vec2(1.0, 1.0)).rgb) * 0.25;
                                        }
                                        else
                                        {
                                            sum = texture(source, at).rgb * 0.2270270270;
                                            sum += (texture(source, at + stride * 1.3846153846).rgb + texture(source, at - stride * 1.3846153846).rgb) * 0.3162162162;
                                            sum += (texture(source, at + stride * 3.2307692308).rgb + texture(source, at - stride * 3.2307692308).rgb) * 0.0702702703;
                                        }
                                        color = vec4(sum, 1.0);
                                    }
                                    """;

    private readonly int[]
        _small = new int[Buffers],
        _smallFb = new int[Buffers]; // the quarter picture, ping-ponged by the passes; [2] sharp

    private readonly Vec4f _tint = new(1, 1, 1, 1); // Render2DTexture reads it into a uniform during the call
    private readonly int[] _viewport = new int[4];
    private bool _failed;
    private int _program, _vao, _full, _fullFb, _source, _step, _shrink, _flip;
    private (int Width, int Height) _size;

    public static bool Enabled { get; set; } = true;

    public void Dispose()
    {
        Release();
        if (_program != 0) GL.DeleteProgram(_program);

        if (_vao != 0) GL.DeleteVertexArray(_vao);

        (_program, _vao) = (0, 0);
    }

    // The program, ahead of the first opening, whose frame would otherwise compile it
    public void Prepare()
    {
        if (!Enabled || _failed) return;

        if (_program == 0) _ = Build();
    }

    // amount 0..1: blur spread and opacity grow together; false when none. Under Vulkan-only GlTap routes it to the
    // scene.
    public bool Draw(float z, double amount)
    {
        if (!Finite(z) || Blurred(amount) is not (> 0 and var blurred)) return false;

        _tint.W = (float)Math.Min(1, amount);
        capi.Render.Render2DTexture(blurred, 0, 0, capi.Render.FrameWidth, capi.Render.FrameHeight, z, _tint);
        return true;
    }

    // The frame so far, blurred into a texture of a quarter size, rows upright; 0 when none. A style's background
    // shader reads it.
    public int Blurred(double amount)
    {
        if (!Enabled || !Finite(amount) || amount <= 0) return 0;

        return Capture(false, (float)Math.Min(1, amount)) ? _small[0] : 0;
    }

    // The frame so far at a quarter size, unblurred and upright, in its own target for the Shader Studio's live
    // preview. Zero when there is none.
    public int Sharp() => Capture(true, 0) ? _small[2] : 0;

    private bool Capture(bool sharp, float amount)
    {
        var viewport = _viewport;
        GL.GetInteger(GetPName.Viewport, viewport);
        var (width, height) = (viewport[2], viewport[3]);
        if (_failed || width < Shrink || height < Shrink || !Assert(width <= MaxSize && height <= MaxSize) ||
            (_program == 0 && !Build()) || (_size != (width, height) && !Targets(width, height)))
        {
            return false;
        }

        GL.GetInteger(GetPName.DrawFramebufferBinding, out var drawFb);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out var readFb);
        GL.GetInteger(GetPName.CurrentProgram, out var program);
        GL.GetInteger(GetPName.VertexArrayBinding, out var vao);
        GL.GetInteger(GetPName.ActiveTexture, out var unit);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out var texture);
        var (blend, scissor) = (GL.IsEnabled(EnableCap.Blend), GL.IsEnabled(EnableCap.ScissorTest));
        try
        {
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, drawFb);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fullFb);
            GL.BlitFramebuffer(viewport[0], viewport[1], viewport[0] + width, viewport[1] + height, 0, 0, width, height,
                ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            Passes(width, height, sharp ? -1 : amount);
        }
        finally
        {
            Restore((drawFb, readFb, program, vao, unit, texture), viewport, blend, scissor);
        }

        return true;
    }

    private static void Restore((int Draw, int Read, int Program, int Vao, int Unit, int Texture) bound, int[] viewport,
        bool blend, bool scissor)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, bound.Draw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, bound.Read);
        GL.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        GL.UseProgram(bound.Program);
        GL.BindVertexArray(bound.Vao);
        GL.BindTexture(TextureTarget.Texture2D, bound.Texture);
        GL.ActiveTexture((TextureUnit)bound.Unit);
        if (blend) GL.Enable(EnableCap.Blend);

        if (scissor) GL.Enable(EnableCap.ScissorTest);
    }

    private void Passes(int width, int height, float amount)
    {
        var (w, h) = (width / Shrink, height / Shrink);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        GL.Uniform1(_source, 0);
        GL.Viewport(0, 0, w, h);
        if (amount < 0) // sharp: one shrink, rows upright, into its own target
        {
            Pass(_full, _smallFb[2], 1f / width, 1f / height, true, true);
            return;
        }

        Pass(_full, _smallFb[0], 1f / width, 1f / height, true, false);
        for (var round = 0; round < Rounds; round++)
        {
            Pass(_small[0], _smallFb[1], amount / w, 0, false, false);
            Pass(_small[1], _smallFb[0], 0, amount / h, false, round == Rounds - 1);
        }
    }

    private void Pass(int source, int target, float x, float y, bool shrink, bool flip)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, target);
        GL.BindTexture(TextureTarget.Texture2D, source);
        GL.Uniform2(_step, x, y);
        GL.Uniform1(_shrink, shrink ? 1 : 0);
        GL.Uniform1(_flip, flip ? 1 : 0);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }

    private bool Build()
    {
        var (vertex, fragment) = (Stage(ShaderType.VertexShader, Vertex), Stage(ShaderType.FragmentShader, Fragment));
        if (vertex == 0 || fragment == 0) return Fail("a shader did not compile");

        _program = GL.CreateProgram();
        GL.AttachShader(_program, vertex);
        GL.AttachShader(_program, fragment);
        GL.LinkProgram(_program);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out var linked);
        if (linked == 0) return Fail("the program did not link: " + GL.GetProgramInfoLog(_program));

        (_source, _step) = (GL.GetUniformLocation(_program, "source"), GL.GetUniformLocation(_program, "stride"));
        (_shrink, _flip) = (GL.GetUniformLocation(_program, "shrink"), GL.GetUniformLocation(_program, "flip"));
        _vao = GL.GenVertexArray();
        return Assert(_source >= 0 && _step >= 0) || Fail("the uniforms are missing");
    }

    private int Stage(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled != 0) return shader;

        capi.Logger?.Warning("Komet options: the backdrop's {0} shader: {1}", type, GL.GetShaderInfoLog(shader));
        GL.DeleteShader(shader);
        return 0;
    }

    private bool Targets(int width, int height)
    {
        Release();
        GL.GetInteger(GetPName.DrawFramebufferBinding, out var drawFb);
        GL.GetInteger(GetPName.TextureBinding2D, out var texture);
        try
        {
            (_full, _fullFb) = Target(width, height);
            for (var i = 0; i < Buffers; i++)
            {
                (_small[i], _smallFb[i]) = Target(width / Shrink, height / Shrink);
            }
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, drawFb);
            GL.BindTexture(TextureTarget.Texture2D, texture);
        }

        if (_fullFb == 0 || Array.IndexOf(_smallFb, 0) >= 0) return Fail("a framebuffer is incomplete");

        _size = (width, height);
        return true;
    }

    private static (int Texture, int Framebuffer) Target(int width, int height)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        var framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D,
            texture, 0);
        if (GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) == FramebufferErrorCode.FramebufferComplete)
        {
            return (texture, framebuffer);
        }

        GL.DeleteFramebuffer(framebuffer);
        GL.DeleteTexture(texture);
        return (0, 0);
    }

    // The screen closed: the full-size copy and the blur targets go until it opens again
    public void Release()
    {
        if (_fullFb != 0) GL.DeleteFramebuffer(_fullFb);

        if (_full != 0) GL.DeleteTexture(_full);

        for (var i = 0; i < Buffers; i++)
        {
            if (_smallFb[i] != 0) GL.DeleteFramebuffer(_smallFb[i]);

            if (_small[i] != 0) GL.DeleteTexture(_small[i]);

            (_small[i], _smallFb[i]) = (0, 0);
        }

        (_full, _fullFb, _size) = (0, 0, default);
    }

    private bool Fail(string why)
    {
        _failed = true;
        capi.Logger?.Warning("Komet options: no blurred backdrop, {0}", why);
        return false;
    }
}
