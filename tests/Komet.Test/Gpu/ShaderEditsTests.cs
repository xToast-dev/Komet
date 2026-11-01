using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

// The post-processing programs' edits (SharpUpscale, LeanGodRays, LeanPost): each takes and still ports
public sealed class ShaderEditsTests
{
    [OneTimeSetUp]
    public void Require()
    {
        GameInstall.RequireAssets();
        if (!Shaderc.Load()) Assert.Ignore("libshaderc is not installed");
    }

    // The blit that stretches the scene over the window upscales edge-aware, and still ports
    [Test]
    public void TheBlitUpscalesAndStillPorts()
    {
        var (vertex, fragment) = EngineShaders.Program("blit", true);
        var program = new ShaderProgram
        {
            PassName = "blit", VertexShader = new Shader(EnumShaderType.VertexShader, vertex, "blit.vsh"),
            FragmentShader = new Shader(EnumShaderType.FragmentShader, fragment, "blit.fsh")
        };
        SharpUpscale.Edit(program);
        if (Environment.GetEnvironmentVariable("KOMET_SHADER_DUMP") is { Length: > 0 } dump)
            File.WriteAllText(Path.Combine(dump, "blit.frag"), program.FragmentShader.Code);
        Assert.That(program.FragmentShader.Code, Does.Contain("kometEasu(scene"));
        Assert.That(GlslPort.Build("blit", program.VertexShader.Code, program.FragmentShader.Code, null, out var error), Is.Not.Null,
            error);
    }

    // The god rays read the colour only where the glow lets it through, and still port
    [Test]
    public void TheGodRaysSkipTheTerrainAndStillPort()
    {
        var (vertex, fragment) = EngineShaders.Program("godrays", true);
        var program = new ShaderProgram
        {
            PassName = "godrays", VertexShader = new Shader(EnumShaderType.VertexShader, vertex, "godrays.vsh"),
            FragmentShader = new Shader(EnumShaderType.FragmentShader, fragment, "godrays.fsh")
        };
        LeanGodRays.Edit(program);
        Assert.That(program.FragmentShader.Code, Does.Contain("if (kometGlow != 0.0)"));
        Assert.That(GlslPort.Build("godrays", program.VertexShader.Code, program.FragmentShader.Code, null, out var error),
            Is.Not.Null, error);
    }

    // Half the god ray steps and the blur's seven taps: every line found, and both still port
    [TestCase("godrays", "int samples = int(90")]
    [TestCase("bilateralblur", "float w0 = 0.0;")]
    public void PostProcessingIsLeanerAndStillPorts(string pass, string edited)
    {
        var (vertex, fragment) = EngineShaders.Program(pass, true);
        var program = new ShaderProgram
        {
            PassName = pass, VertexShader = new Shader(EnumShaderType.VertexShader, vertex, pass + ".vsh"),
            FragmentShader = new Shader(EnumShaderType.FragmentShader, fragment, pass + ".fsh")
        };
        LeanGodRays.Edit(program);
        LeanPost.Edit(program);
        Assert.That(program.FragmentShader.Code, Does.Contain(edited));
        Assert.That(GlslPort.Build(pass, program.VertexShader.Code, program.FragmentShader.Code, null, out var error),
            Is.Not.Null, error);
    }
}
