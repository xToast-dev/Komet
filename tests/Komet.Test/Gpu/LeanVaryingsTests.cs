using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

// The terrain programs as the engine puts them together, Komet's distant cascade in: each loses outputs and still ports
public sealed class LeanVaryingsTests
{
    [OneTimeSetUp]
    public void Require()
    {
        GameInstall.RequireAssets();
        if (!Shaderc.Load()) Assert.Ignore("libshaderc is not installed");
    }

    [TestCaseSource(typeof(EngineShadersTests), nameof(EngineShadersTests.Settings))]
    [TestCase("chunktransparent", 1)]
    public void TheTerrainHandsOnLessAndStillPorts(string name, int high)
    {
        ArgumentNullException.ThrowIfNull(name);
        var (vertex, fragment) = EngineShaders.Program(name, high == 1);
        (vertex, fragment) = (DistantShadows.Vertex(vertex) ?? vertex,
            (DistantShadows.Fragment(fragment) ?? fragment).Replace("#define SSAOLEVEL", DistantShadows.Definition + "#define SSAOLEVEL",
                StringComparison.Ordinal));
        var program = new ShaderProgram
        {
            PassName = name, VertexShader = new Shader(EnumShaderType.VertexShader, vertex, name + ".vsh"),
            FragmentShader = new Shader(EnumShaderType.FragmentShader, fragment, name + ".fsh")
        };
        var before = Outputs(vertex);
        LeanVaryings.Edit(program);
        if (Environment.GetEnvironmentVariable("KOMET_SHADER_DUMP") is { Length: > 0 } dump)
        {
            File.WriteAllText(Path.Combine(dump, $"{name}-{high}.vert"), program.VertexShader.Code);
            File.WriteAllText(Path.Combine(dump, $"{name}-{high}.frag"), program.FragmentShader.Code);
        }

        Assert.That(before - Outputs(program.VertexShader.Code), Is.GreaterThanOrEqualTo(high == 1 ? 7 : 4));
        var ported = GlslPort.Build(name, program.VertexShader.Code, program.FragmentShader.Code, null, out var error);
        Assert.That(ported, Is.Not.Null, error);
    }

    private static int Outputs(string code) =>
        System.Text.RegularExpressions.Regex.Count(code, @"^\s*(flat\s+)?out\s+\w+\s+\w+\s*;",
            System.Text.RegularExpressions.RegexOptions.Multiline);
}
