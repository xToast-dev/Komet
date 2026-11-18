using System.IO.Compression;
using System.Runtime.InteropServices;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Portable;

// Code that behaves per system: separators, library names, files replaced in place
public sealed class PlatformTests
{
    private static readonly string[] Elsewhere = ["sheydermod:shaders/deferredlighting.fsh"], Mine = ["drtagx:agx"];
    private static readonly string[] Versions = ["agx:shaders/agx.fsh", "game:shaderincludes/fogandlight.fsh"];

    private static readonly string[] Replaced =
            ["shaders/final.fsh", "shaderincludes/fogandlight.ash"],
        Own = ["agx:agx"];

    private string _root = "";

    [SetUp]
    public void Folder() => _root = Directory.CreateTempSubdirectory("komet-portable").FullName;

    [TearDown]
    public void Clean() => Directory.Delete(_root, true);

    // A check's site reads the same from a Windows build (backslashes after PathMap's /_/) as from a Linux one
    [Test]
    public void AFailedCheckNamesItsSiteWithSlashes()
    {
        using var error = new StringWriter();
        var was = Console.Error;
        Contracts.Attach(null); // no logger: standard error
        Console.SetError(error);
        try
        {
            // ReSharper disable ExplicitCallerInfoArgument - the test names a caller of its own
            _ = Contracts.Assert(false, 24, @"/_/Komet.Rendering\Shaders\ShaderPacks.cs");
            // ReSharper restore ExplicitCallerInfoArgument
        }
        finally
        {
            Console.SetError(was);
        }

        Assert.That(error.ToString(), Does.Contain("Komet.Rendering/Shaders/ShaderPacks.cs:24"));
    }

    // shaderc is fetched for the system the game runs on
    [Test]
    public void ShadercIsThisSystemsLibrary()
    {
        var (rid, file) = ShadercFetch.Library()!.Value;
        var cpu = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        Assert.That((rid, file),
            Is.EqualTo(OperatingSystem.IsWindows()
                ? ($"win-{cpu}", "shaderc_shared.dll")
                : ($"linux-{cpu}", "libshaderc_shared.so")));
    }

    [Test]
    public void PhysicalMemoryIsAvailableOnBothPlatforms()
    {
        var (available, total) = ResourceStats.SystemMemory();
        Assert.Multiple(() =>
        {
            Assert.That(total, Is.GreaterThan(0UL));
            Assert.That(available, Is.LessThanOrEqualTo(total));
        });
    }

    // The fetched library replaces an older one whole; no .part stays behind
    [Test]
    public void AnUnpackedLibraryReplacesTheOldOne()
    {
        var target = Path.Join(_root, "lib", "shaderc.bin");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "old");
        ShadercFetch.Unpack(Zip("runtimes/x/native/shaderc.bin", "new"), "runtimes/x/native/shaderc.bin", target);
        Assert.That((File.ReadAllText(target), File.Exists(target + ".part")), Is.EqualTo(("new", false)));
    }

    [Test]
    public void BundledShadercCanBeReadFromTheModZip()
    {
        var (rid, file) = ShadercFetch.Library()!.Value;
        var zip = Path.Join(_root, "komet.zip");
        File.WriteAllBytes(zip, Zip($"native/{rid}/{file}", "bundled"));
        Assert.That(ShadercFetch.Bundled(zip, _root), Is.True);
        Assert.That(File.ReadAllText(Path.Join(_root, file)), Is.EqualTo("bundled"));
    }

    [Test]
    public void AFailedNativeReplacementLeavesNoPartialFile()
    {
        var target = Path.Join(_root, "occupied");
        _ = Directory.CreateDirectory(target);
        var failure = Assert.Catch(() => ShadercFetch.Unpack(Zip("native.bin", "new"), "native.bin", target));
        Assert.That(failure, Is.InstanceOf<IOException>().Or.InstanceOf<UnauthorizedAccessException>());
        Assert.That(File.Exists(target + ".part"), Is.False);
        Assert.That(Directory.Exists(target), Is.True);
    }

    [Test]
    public void WindowsCompilerProducesSpirvFromTheShippedLibrary()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Assert.Ignore("Windows x64 native compiler smoke test");
        }

        var (source, folder) = (Shaderc.SourceFolder, Shaderc.Folder);
        try
        {
            File.WriteAllText(Path.Join(_root, "shaderc_shared.dll"), "invalid native library");
            Shaderc.SourceFolder = _root;
            Shaderc.Folder = Path.Join(Path.GetDirectoryName(typeof(Shaderc).Assembly.Location), "native", "win-x64");
            Assert.That(Shaderc.Load(), Is.True, Shaderc.Failure);
            Assert.That(Shaderc.Location, Is.EqualTo(Path.Join(Shaderc.Folder, "shaderc_shared.dll")));
            var words = Shaderc.Compile("#version 450\nvoid main() { gl_Position = vec4(0.0); }", Shaderc.Vertex,
                "portable.vert", out var error, false);
            Assert.That(words, Is.Not.Null, error);
            Assert.That(words![0], Is.EqualTo(0x07230203U));
            Assert.That(Shaderc.Compile("#version 450\ninvalid shader", Shaderc.Vertex, "broken.vert", out error),
                Is.Null);
            Assert.That(error, Is.Not.Empty);
        }
        finally
        {
            (Shaderc.SourceFolder, Shaderc.Folder) = (source, folder);
        }
    }

    // A second example does not overwrite the first
    [Test]
    public void ExamplesTakeTheNextFreeName()
    {
        _ = Directory.CreateDirectory(Path.Join(_root, "example"));
        _ = Directory.CreateDirectory(Path.Join(_root, "example-2"));
        Assert.That(Path.GetFileName(KometExamples.Free(_root)), Is.EqualTo("example-3"));
    }

    // A mod's shader files count the same whether its zip was packed on Windows or elsewhere
    [Test]
    public void ShaderModsAreFoundWithEitherSeparator()
    {
        string[] paths =
        [
            @"assets\game\shaders\final.fsh", "assets/game/shaderincludes/fogandlight.ash",
            @"assets\agx\shaders\agx.fsh",
            "assets/agx/textures/x.png", "modinfo.json"
        ];
        var (replaced, own) = ShaderMods.Tally(paths);
        Assert.That((replaced, own), Is.EqualTo((Replaced, Own)));
    }

    // A mod declares itself with config/komet.json and brings Komet versions under config/komet/, from any packer
    [Test]
    public void DeclarationsAndKometVersionsAreFound()
    {
        string[] paths =
        [
            @"assets\agx\config\komet.json", @"assets\agx\config\komet\shaders\agx.fsh",
            "assets/game/config/komet/shaderincludes/fogandlight.fsh", "assets/game/config/komet/textures/x.png",
            "assets/agx/config/other.json"
        ];
        Assert.That((ShaderMods.Declares(paths), ShaderMods.KometVersions(paths)), Is.EqualTo((true, Versions)));
    }

    // A mod replacing files in another mod's domain (DRTAgX in sheydermod/) is told apart from its own files
    [Test]
    public void FilesInAnotherModsDomainAreTheirs()
    {
        string[] paths =
        [
            @"assets\sheydermod\shaders\deferredlighting.fsh", "assets/drtagx/shaders/agx.fsh",
            "assets/game/shaders/final.fsh"
        ];
        Assert.That((ShaderMods.Foreign(paths, "drtagx"), ShaderMods.Tally(paths, "drtagx").Own),
            Is.EqualTo((Elsewhere, Mine)));
    }

    // A mod with items or blocks (Primitive Survival) has content: its systems run even while its shaders are held back
    [Test]
    public void ContentModsAreToldApartFromShaderMods()
    {
        string[] content = [@"assets\primitivesurvival\itemtypes\fish.json", "assets/game/shaders/chunkliquid.fsh"];
        string[] shaders = ["assets/game/shaders/final.fsh", "assets/sheydermod/config/x.json"];
        Assert.That((ShaderMods.HasContent(content), ShaderMods.HasContent(shaders)), Is.EqualTo((true, false)));
    }

    private static byte[] Zip(string entry, string text)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
        {
            writer.Write(text);
        }

        return memory.ToArray();
    }
}
