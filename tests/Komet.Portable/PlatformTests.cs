using System.IO.Compression;
using System.Runtime.InteropServices;
using Komet.Options;
using Komet.Rendering;
using Komet.Vulkan;

namespace Komet.Portable;

// Code that behaves per system: separators, library names, files replaced in place
public sealed class PlatformTests
{
    private static readonly string[] Elsewhere = ["sheydermod:shaders/deferredlighting.fsh"], Mine = ["drtagx:agx"];
    private static readonly string[] Versions = ["agx:shaders/agx.fsh", "game:shaderincludes/fogandlight.fsh"];
    private static readonly string[] Replaced = ["shaders/final.fsh", "shaderincludes/fogandlight.ash"], Own = ["agx:agx"];
    private string _root = "";

    [SetUp]
    public void Folder() => _root = Directory.CreateTempSubdirectory("komet-portable").FullName;

    [TearDown]
    public void Clean() => Directory.Delete(_root, recursive: true);

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
            _ = Contracts.Assert(false, 24, @"/_/Komet.Rendering\Shaders\ShaderPacks.cs");
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
        Assert.That((rid, file), Is.EqualTo(OperatingSystem.IsWindows() ? ($"win-{cpu}", "shaderc_shared.dll") : ($"linux-{cpu}", "libshaderc_shared.so")));
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
        string[] paths = [@"assets\game\shaders\final.fsh", "assets/game/shaderincludes/fogandlight.ash", @"assets\agx\shaders\agx.fsh",
            "assets/agx/textures/x.png", "modinfo.json"];
        var (replaced, own) = ShaderMods.Tally(paths);
        Assert.That((replaced, own), Is.EqualTo((Replaced, Own)));
    }

    // A mod declares itself with config/komet.json and brings Komet versions under config/komet/, from any packer
    [Test]
    public void DeclarationsAndKometVersionsAreFound()
    {
        string[] paths = [@"assets\agx\config\komet.json", @"assets\agx\config\komet\shaders\agx.fsh",
            "assets/game/config/komet/shaderincludes/fogandlight.fsh", "assets/game/config/komet/textures/x.png", "assets/agx/config/other.json"];
        Assert.That((ShaderMods.Declares(paths), ShaderMods.KometVersions(paths)), Is.EqualTo((true, Versions)));
    }

    // A mod replacing files in another mod's domain (DRTAgX in sheydermod/) is told apart from its own files
    [Test]
    public void FilesInAnotherModsDomainAreTheirs()
    {
        string[] paths = [@"assets\sheydermod\shaders\deferredlighting.fsh", "assets/drtagx/shaders/agx.fsh", "assets/game/shaders/final.fsh"];
        Assert.That((ShaderMods.Foreign(paths, "drtagx"), ShaderMods.Tally(paths, "drtagx").Own), Is.EqualTo((Elsewhere, Mine)));
    }

    private static byte[] Zip(string entry, string text)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
            writer.Write(text);
        return memory.ToArray();
    }
}
