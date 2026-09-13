using System.Globalization;
using System.Text.RegularExpressions;
using Cake.Common;
using Cake.Common.Diagnostics;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Clean;
using Cake.Common.Tools.DotNet.Publish;
using Cake.Core;
using Cake.Frosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args) => new CakeHost().UseContext<BuildContext>().Run(args);
}

// A build script recovers from a failed check by failing the build: every check throws.
public sealed class BuildContext : FrostingContext
{
    public const string Project = "../Komet";
    public const int MaxFiles = 4096;
    public string BuildConfiguration { get; }
    public bool SkipJsonValidation { get; }
    public string ModId { get; }
    public string Version { get; }

    public BuildContext(ICakeContext context) : base(context)
    {
        ArgumentNullException.ThrowIfNull(context);
        BuildConfiguration = context.Argument("configuration", "Release");
        SkipJsonValidation = context.Argument("skipJsonValidation", false);
        if (BuildConfiguration is not ("Release" or "Debug")) throw new InvalidDataException($"Unknown configuration: {BuildConfiguration}");
        var info = JObject.Parse(File.ReadAllText($"{Project}/modinfo.json"));
        ModId = (string?)info["modid"] ?? throw new InvalidDataException("modinfo.json: modid missing");
        Version = (string?)info["version"] ?? throw new InvalidDataException("modinfo.json: version missing");
        if (ModId.Length == 0 || Version.Length == 0) throw new InvalidDataException("modinfo.json: modid and version must not be empty");
    }
}

[TaskName("ValidateJson")]
public sealed class ValidateJsonTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SkipJsonValidation) return;
        var files = context.GetFiles($"{BuildContext.Project}/assets/**/*.json");
        if (files.Count == 0) throw new InvalidDataException("No JSON assets found, wrong working directory?");
        foreach (var path in files.Select(file => file.FullPath).Take(BuildContext.MaxFiles))
        {
            try { _ = JToken.Parse(File.ReadAllText(path)); }
            catch (JsonException e) { throw new InvalidDataException($"Invalid JSON: {path}\n{e.Message}", e); }
        }
    }
}

// Power-of-Ten checks the compiler does not make: assertion density of at least two per function, no preprocessor directives, no goto,
// no while/do, every for bounded by a constant or Math.Min(…, constant), every foreach through Bounded() or over a collection literal.
// A function is a member or local function declaration; an assertion is a call of one of the Contracts helpers.
[TaskName("Rules")]
[IsDependentOn(typeof(ValidateJsonTask))]
public sealed partial class RulesTask : FrostingTask<BuildContext>
{
    private const double MinDensity = 2;

    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|protected|static|override|sealed|partial|virtual|extern|async|readonly)\s+)+(?:[\w<>\[\]?,.]+\s+)?\w+\s*(?:<[^>]*>)?\s*\([^;{}]*\)\s*(?:where[^{;]*)?(?::\s*base\([^)]*\)\s*)?(?:=>|\{|$)|^\s+(?:void|double|int|bool|string|float|long)\s+\w+\s*\([^;{}]*\)\s*(?:=>|\{|$)", RegexOptions.Multiline)]
    private static partial Regex Function();

    [GeneratedRegex(@"\b(?:Assert|NotNull|Finite|Index|Bounded)\(")]
    private static partial Regex Assertion();

    [GeneratedRegex(@"^\s*#|\bgoto\b|\bwhile\s*\(|\bdo\s*\{", RegexOptions.Multiline)]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"^.*\b(?:for|foreach)\s*\(.*$", RegexOptions.Multiline)]
    private static partial Regex Loop();

    [GeneratedRegex(@"\bforeach\s*\(.*(?:\.Bounded\(|\)\s*\[)|\bfor\s*\(.*;\s*\w+\s*<=?\s*(?:Math\.Min\(|[A-Z]\w*(?:\.[A-Z]\w*)?\s*(?:;|&&|\)))")]
    private static partial Regex BoundedLoop();

    public override void Run(BuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        int functions = 0, assertions = 0;
        var sources = context.GetFiles($"{BuildContext.Project}/**/*.cs").Select(file => file.FullPath)
            .Where(path => !path.Contains("/obj/", StringComparison.Ordinal) && !path.Contains("/bin/", StringComparison.Ordinal)).Take(BuildContext.MaxFiles);
        foreach (var path in sources)
        {
            var source = File.ReadAllText(path);
            var forbidden = Forbidden().Match(source);
            if (forbidden.Success) throw new InvalidDataException($"{path}: preprocessor directive, goto or while: {forbidden.Value.Trim()}");
            if (path.EndsWith("Contracts.cs", StringComparison.Ordinal)) continue;   // the helpers themselves, Bounded() takes its bound as a parameter
            var unbounded = Loop().Matches(source).Select(match => match.Value).Take(BuildContext.MaxFiles).FirstOrDefault(loop => !BoundedLoop().IsMatch(loop));
            if (unbounded != null) throw new InvalidDataException($"{path}: loop without a constant bound: {unbounded.Trim()}");
            functions += Function().Count(source);
            assertions += Assertion().Count(source);
        }
        if (functions == 0) throw new InvalidDataException("No functions found, wrong working directory?");
        var density = (double)assertions / functions;
        context.Information("Assertion density: {0} assertions in {1} functions = {2}", assertions, functions, density.ToString("F2", CultureInfo.InvariantCulture));
        if (density < MinDensity) throw new InvalidDataException($"Assertion density {density.ToString("F2", CultureInfo.InvariantCulture)} is below {MinDensity}");
    }
}

[TaskName("Build")]
[IsDependentOn(typeof(RulesTask))]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        const string csproj = $"{BuildContext.Project}/Komet.csproj";
        if (!context.FileExists(csproj)) throw new FileNotFoundException("Project file missing", csproj);
        context.DotNetClean(csproj, new DotNetCleanSettings { Configuration = context.BuildConfiguration });
        context.DotNetPublish(csproj, new DotNetPublishSettings { Configuration = context.BuildConfiguration });
    }
}

[TaskName("Package")]
[IsDependentOn(typeof(BuildTask))]
public sealed class PackageTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var publish = $"{BuildContext.Project}/bin/{context.BuildConfiguration}/Mods/mod/publish";
        if (!context.DirectoryExists(publish)) throw new DirectoryNotFoundException($"Publish output missing: {publish}");
        var dir = $"../Releases/{context.ModId}";
        context.CleanDirectory("../Releases");
        context.EnsureDirectoryExists(dir);
        context.CopyFiles($"{publish}/*", dir);
        context.CopyDirectory($"{BuildContext.Project}/assets", $"{dir}/assets");
        context.CopyFile($"{BuildContext.Project}/modinfo.json", $"{dir}/modinfo.json");
        if (context.FileExists($"{BuildContext.Project}/modicon.png")) context.CopyFile($"{BuildContext.Project}/modicon.png", $"{dir}/modicon.png");
        context.Zip(dir, $"../Releases/{context.ModId}_{context.Version}.zip");
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(PackageTask))]
public sealed class DefaultTask : FrostingTask;
