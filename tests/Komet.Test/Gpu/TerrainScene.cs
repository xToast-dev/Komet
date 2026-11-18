using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// An engine chunk program, linked and ported as the game ports it: read back from the driver, the linker's attribute
// locations, origin pushed per pool
internal sealed class TerrainScene : IDisposable
{
    public TerrainScene(bool high, string program = "chunkopaque")
    {
        var (vertex, fragment) = EngineShaders.Program(program, high);
        Program = Parity.Linked(vertex, fragment);
        var source = ProgramPorts.Read(Program, program, out var why);
        var ported = source is null ? null
            : ProgramPorts.Made(source, out why, new HashSet<string>(["origin"], StringComparer.Ordinal));
        Ported = ported ?? throw new InvalidOperationException(why);
    }

    public int Program { get; }
    public GlslPort.Ported Ported { get; }

    public void Dispose() => GL.DeleteProgram(Program);
}
