using Cake.Common;
using Cake.Common.IO;
using Cake.Common.Tools.DotNet;
using Cake.Common.Tools.DotNet.Build;
using Cake.Common.Tools.DotNet.MSBuild;
using Cake.Core;
using Cake.Frosting;

namespace CakeBuild;

public static class Program
{
    public static int Main(string[] args)
    {
        return new CakeHost().UseContext<BuildContext>().Run(args);
    }
}

// The checks live in the build itself (Komet.Rules analyzer, the ValidateJson target, warnings as errors); Cake only drives it.
public sealed class BuildContext : FrostingContext
{
    public const string Project = "../Komet/Komet.csproj";

    public BuildContext(ICakeContext context) : base(context)
    {
        ArgumentNullException.ThrowIfNull(context);
        BuildConfiguration = context.Argument("configuration", "Release");
        if (BuildConfiguration is not ("Release" or "Debug"))
            throw new InvalidDataException($"Unknown configuration: {BuildConfiguration}");
    }

    public string BuildConfiguration { get; }
}

// Release runs the Package target: JSON check, compile, then ../Releases/komet and ../Releases/komet_<version>.zip. Debug only compiles.
[TaskName("Build")]
public sealed class BuildTask : FrostingTask<BuildContext>
{
    public override void Run(BuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.FileExists(BuildContext.Project))
            throw new FileNotFoundException("Project file missing", BuildContext.Project);
        var msbuild = new DotNetMSBuildSettings();
        if (context.BuildConfiguration == "Release") msbuild.Targets.Add("Package");
        context.DotNetBuild(BuildContext.Project,
            new DotNetBuildSettings { Configuration = context.BuildConfiguration, MSBuildSettings = msbuild });
    }
}

[TaskName("Default")]
[IsDependentOn(typeof(BuildTask))]
public sealed class DefaultTask : FrostingTask;
