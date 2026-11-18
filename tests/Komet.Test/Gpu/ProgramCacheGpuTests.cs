using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// ProgramCache's ground: a linked program's binary, given back to a new program, links without a compile
[NonParallelizable]
public sealed class ProgramCacheGpuTests
{
    [Test]
    public void BinaryLinksAgain()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here: " + GlContext.Why);

        var first = Linked();
        GL.GetProgram(first, (GetProgramParameterName)0x8741, out var length);
        Assume.That(length, Is.GreaterThan(0), "the driver keeps no binaries");

        var data = new byte[length];
        GL.GetProgramBinary(first, length, out _, out BinaryFormat format, data);
        var second = GL.CreateProgram();
        GL.ProgramBinary(second, format, data, data.Length);
        GL.GetProgram(second, GetProgramParameterName.LinkStatus, out var linked);
        var location = GL.GetUniformLocation(second, "tint");
        GL.DeleteProgram(first);
        GL.DeleteProgram(second);
        Assert.Multiple(() =>
        {
            Assert.That(linked, Is.EqualTo(1));
            Assert.That(location, Is.GreaterThanOrEqualTo(0));
        });
    }

    private static int Linked()
    {
        var (v, f) = (GL.CreateShader(ShaderType.VertexShader), GL.CreateShader(ShaderType.FragmentShader));
        GL.ShaderSource(v, "#version 330 core\nlayout(location = 0) in vec3 p;\nvoid main() { gl_Position = vec4(p, 1.0); }");
        GL.ShaderSource(f, "#version 330 core\nuniform vec4 tint;\nout vec4 c;\nvoid main() { c = tint; }");
        GL.CompileShader(v);
        GL.CompileShader(f);
        var program = GL.CreateProgram();
        GL.AttachShader(program, v);
        GL.AttachShader(program, f);
        GL.LinkProgram(program);
        GL.DeleteShader(v);
        GL.DeleteShader(f);
        return program;
    }
}
