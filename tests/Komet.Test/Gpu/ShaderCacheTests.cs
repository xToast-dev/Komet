using System.Text.Json;
using System.Text.Json.Serialization;
using Komet.Gpu;
using Komet.Vulkan;

namespace Komet.Test.Gpu;

// The device leaves its pipeline cache for the next device on the same GPU.
[NonParallelizable]
public sealed class ShaderCacheTests
{
    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec3 xyz;
        uniform mat4 mvp;
        uniform vec3 origin;
        uniform float fog = 0.5;
        void main() { gl_Position = mvp * vec4(xyz + origin, fog); }
        """;

    private const string Fragment = """
        #version 330 core
        uniform sampler2D tex;
        uniform vec4 tint = vec4(1, 0.5, 0.25, 1);
        out vec4 color;
        void main() { color = texture(tex, vec2(0)) * tint; }
        """;

    private static readonly JsonSerializerOptions Json =
        new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    private static readonly Dictionary<string, int> Attributes = new() { ["xyz"] = 0 };
    private static readonly HashSet<string> Hot = new(["origin"], StringComparer.Ordinal);
    private static readonly string[] HotNames = ["origin"];

    private string _folder = "";

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "_writes")]
    private static extern ref Task Writes([UnsafeAccessorType("Komet.Vulkan.ShaderCache, Komet")] object? cache);

    // The files ShaderCache has written so far are on disk
    private static void Flush() => Writes(null).Wait();

    [SetUp]
    public void Folder()
    {
        if (!Shaderc.Load()) Assert.Ignore("libshaderc is not installed");
        _folder = Path.Join(Path.GetTempPath(), "komet-shadercache-" + Guid.NewGuid().ToString("N"));
        ShaderCache.Open(_folder);
        ShaderCache.Enabled = true;
    }

    [TearDown]
    public void Gone()
    {
        Flush();
        ShaderCache.Open("");
        ShaderCache.Enabled = true;
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private static GlslPort.Ported Port(IReadOnlySet<string>? hot)
    {
        var ported = GlslPort.Build("test", Vertex, Fragment, Attributes, out var error, hot);
        Assert.That(ported, Is.Not.Null, error);
        return ported!;
    }

    private static string Sources() => ShaderCache.Sources(Vertex, Fragment, Attributes);

    [Test]
    public void APortReadBackIsThePortWrittenUnderTheNameAskedFor()
    {
        var ported = Port(Hot);
        ShaderCache.Keep(Sources(), Hot, ported);
        Flush();
        var read = ShaderCache.Port(Sources(), Hot, "chunkopaque");
        Assert.That(read, Is.Not.Null);
        Assert.That(read!.Name, Is.EqualTo("chunkopaque"));
        Assert.That(JsonSerializer.Serialize(read with { Name = "test" }, Json),
            Is.EqualTo(JsonSerializer.Serialize(ported, Json)));
        Assert.That(read.HotUniforms.Size, Is.GreaterThan(0));
        Assert.That(read.VertexUniforms.Members.Select(u => u.Name), Does.Not.Contain("origin"));
    }

    [Test]
    public void OtherSourcesOrOtherHotUniformsMiss()
    {
        ShaderCache.Keep(Sources(), Hot, Port(Hot));
        Flush();
        Assert.That(ShaderCache.Port(Sources(), null, "test"), Is.Null, "no hot uniforms");
        var other = ShaderCache.Sources(Vertex + "\n", Fragment, Attributes);
        Assert.That(other, Is.Not.EqualTo(Sources()));
        Assert.That(ShaderCache.Port(other, Hot, "test"), Is.Null, "another source");
        var bound = ShaderCache.Sources(Vertex, Fragment, new Dictionary<string, int> { ["xyz"] = 3 });
        Assert.That(ShaderCache.Port(bound, Hot, "test"), Is.Null, "another attribute location");
    }

    [Test]
    public void SettledHotUniformsComeBackAtTheNextStart()
    {
        Assert.That(ShaderCache.Settled(Sources()), Is.Null);
        ShaderCache.Settle(Sources(), Hot);
        Flush();
        ShaderCache.Open(_folder); // read anew, as at the next start
        Assert.That(ShaderCache.Settled(Sources()), Is.EquivalentTo(HotNames));
        Assert.That(ShaderCache.Settled(ShaderCache.Sources(Vertex, Fragment + " ", Attributes)), Is.Null);
    }

    [Test]
    public void ATornFileIsPortedAnew()
    {
        ShaderCache.Keep(Sources(), Hot, Port(Hot));
        Flush();
        foreach (var file in Directory.GetFiles(_folder, "*.json", SearchOption.AllDirectories))
            File.WriteAllText(file, "{\"Name\":\"test\",\"Vertex\":[1,2");
        Assert.That(ShaderCache.Port(Sources(), Hot, "test"), Is.Null);
    }

    [Test]
    public void SwitchedOffNothingIsReadOrWritten()
    {
        ShaderCache.Keep(Sources(), Hot, Port(Hot));
        Flush();
        ShaderCache.Enabled = false;
        Assert.That(ShaderCache.Port(Sources(), Hot, "test"), Is.Null);
        ShaderCache.Settle(Sources(), Hot);
        Flush();
        Assert.That(File.Exists(Path.Join(_folder, "hot.json")), Is.False);
    }

    [Test]
    public void TheDeviceLeavesItsPipelineCacheForTheNextOne()
    {
        using var window = OcclusionGpuTests.Context();
        if (window is null) Assert.Ignore("no OpenGL 4.6 context here");
        using (var rig = GpuRig.Start(GpuRig.Vulkan, out var why))
        {
            if (rig is null) Assert.Ignore(why);
            Assert.That(rig.Device!.PipelineCache, Is.Not.Zero);
            Assert.That(ShaderCache.PipelineBytes, Is.Zero, "nothing kept yet");
            Assert.That(rig.Backend!.Program(GpuShaders.HiZ, new QuietLogger()), Is.GreaterThan(0));
        }

        Assert.That(Directory.GetFiles(_folder, "pipelines-*.bin"), Has.Length.EqualTo(1));
        using var again = GpuRig.Start(GpuRig.Vulkan, out _);
        Assert.That(again?.Device?.PipelineCache, Is.Not.Zero);
        Assert.That(ShaderCache.PipelineBytes, Is.GreaterThan(32), "the header and the compute pipeline");
    }
}
