using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// A program linked by the driver from vertex and fragment source, failing the test with the driver's log
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
}
