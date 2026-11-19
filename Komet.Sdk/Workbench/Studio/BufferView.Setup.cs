using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// Building and releasing the view's GL objects, the saved state around its draw, and the pixel inspector
internal sealed partial class BufferView
{
    // The workbench closed: every GL object goes
    public void Dispose()
    {
        ReleaseTargets();
        if (_program != 0)
        {
            GL.DeleteProgram(_program);
            GL.DeleteVertexArray(_vao);
            GL.DeleteSampler(_sampler);
        }

        (_program, _vao, _sampler, _failed, Snapshot) = (0, 0, 0, false, "");
    }

    private Saved Save()
    {
        GL.GetInteger(GetPName.Viewport, _viewport);
        GL.GetInteger(GetPName.DrawFramebufferBinding, out var draw);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out var read);
        GL.GetInteger(GetPName.CurrentProgram, out var program);
        GL.GetInteger(GetPName.VertexArrayBinding, out var vao);
        GL.GetInteger(GetPName.ActiveTexture, out var unit);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out var tex0);
        GL.GetInteger(GetPName.SamplerBinding, out var sampler0);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.GetInteger(GetPName.TextureBinding2D, out var tex1);
        GL.GetInteger(GetPName.SamplerBinding, out var sampler1);
        return new Saved(draw, read, program, vao, unit, tex0, tex1, sampler0, sampler1, GL.IsEnabled(EnableCap.Blend),
            GL.IsEnabled(EnableCap.ScissorTest), GL.IsEnabled(EnableCap.DepthTest));
    }

    private void Restore(Saved s)
    {
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, s.Draw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, s.Read);
        GL.Viewport(_viewport[0], _viewport[1], _viewport[2], _viewport[3]);
        GL.UseProgram(s.Program);
        GL.BindVertexArray(s.Vao);
        GL.ActiveTexture(TextureUnit.Texture1);
        GL.BindTexture(TextureTarget.Texture2D, s.Tex1);
        GL.BindSampler(1, s.Sampler1);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, s.Tex0);
        GL.BindSampler(0, s.Sampler0);
        GL.ActiveTexture((TextureUnit)s.Unit);
        Toggle(EnableCap.Blend, s.Blend);
        Toggle(EnableCap.ScissorTest, s.Scissor);
        Toggle(EnableCap.DepthTest, s.Depth);
    }

    private static void Toggle(EnableCap cap, bool on)
    {
        if (on)
        {
            GL.Enable(cap);
        }
        else
        {
            GL.Disable(cap);
        }
    }

    private bool Build()
    {
        var (vertex, fragment) = (Stage(ShaderType.VertexShader, Vertex), Stage(ShaderType.FragmentShader, Fragment));
        if (vertex == 0 || fragment == 0) return false;

        _program = GL.CreateProgram();
        GL.AttachShader(_program, vertex);
        GL.AttachShader(_program, fragment);
        GL.LinkProgram(_program);
        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        GL.GetProgram(_program, GetProgramParameterName.LinkStatus, out var linked);
        _failed = linked == 0;
        (_uSource, _uOther) = (GL.GetUniformLocation(_program, "source"), GL.GetUniformLocation(_program, "other"));
        (_uMode, _uChannel) = (GL.GetUniformLocation(_program, "mode"), GL.GetUniformLocation(_program, "channel"));
        (_uContrast, _uSplit) = (GL.GetUniformLocation(_program, "contrast"), GL.GetUniformLocation(_program, "split"));
        (_uFlip, _uRange) = (GL.GetUniformLocation(_program, "flip"), GL.GetUniformLocation(_program, "range"));
        _uMark = GL.GetUniformLocation(_program, "mark");
        _vao = GL.GenVertexArray();
        _sampler = GL.GenSampler();
        GL.SamplerParameter(_sampler, SamplerParameterName.TextureCompareMode, (int)TextureCompareMode.None);
        GL.SamplerParameter(_sampler, SamplerParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.SamplerParameter(_sampler, SamplerParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.SamplerParameter(_sampler, SamplerParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.SamplerParameter(_sampler, SamplerParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        return !_failed;
    }

    private int Stage(ShaderType type, string source)
    {
        var shader = GL.CreateShader(type);
        GL.ShaderSource(shader, source);
        GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out var compiled);
        if (compiled != 0) return shader;

        _failed = true;
        GL.DeleteShader(shader);
        return 0;
    }

    private bool Targets2(int height)
    {
        ReleaseTargets();
        GL.GetInteger(GetPName.DrawFramebufferBinding, out var draw);
        GL.GetInteger(GetPName.TextureBinding2D, out var texture);
        (_target, _fb) = Target(height);
        (_snap, _snapFb) = Target(height);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        _height = height;
        Snapshot = "";
        return _fb != 0 && _snapFb != 0;
    }

    private static (int Texture, int Framebuffer) Target(int height)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Width, height, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        var framebuffer = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
        GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, texture, 0);
        return GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) == FramebufferErrorCode.FramebufferComplete
            ? (texture, framebuffer)
            : (0, 0);
    }

    private void ReleaseTargets()
    {
        int[] objects = [_fb, _snapFb];
        for (var i = 0; i < 2; i++)
        {
            if (objects[i] != 0) GL.DeleteFramebuffer(objects[i]);
        }

        int[] textures = [_target, _snap];
        for (var i = 0; i < 2; i++)
        {
            if (textures[i] != 0) GL.DeleteTexture(textures[i]);
        }

        (_fb, _snapFb, _target, _snap, _height) = (0, 0, 0, 0, 0);
    }

    private readonly record struct Saved(
        int Draw,
        int Read,
        int Program,
        int Vao,
        int Unit,
        int Tex0,
        int Tex1,
        int Sampler0,
        int Sampler1,
        bool Blend,
        bool Scissor,
        bool Depth);
}
