using Komet.Gpu;

namespace Komet.Test.Gpu;

// glslangValidator does the compiling; without it on the PATH those cases are skipped.
public sealed class GpuShadersTests
{
    private static readonly string Folder = Path.Combine(Paths.KometDir, "Gpu", "Shaders");

    [TestCaseSource(typeof(GpuShaders), nameof(GpuShaders.All))]
    public void TheAssemblyCarriesTheFile(string name)
    {
        Assert.That(GpuShaders.Source(name), Is.EqualTo(File.ReadAllText(Path.Combine(Folder, name))));
    }

    [TestCaseSource(typeof(GpuShaders), nameof(GpuShaders.All))]
    public void ItCompilesForOpenGl(string name)
    {
        Compile(name, "-G", "opengl");
    }

    [TestCaseSource(typeof(GpuShaders), nameof(GpuShaders.All))]
    public void ItCompilesForVulkan(string name)
    {
        Compile(name, "-V", "vulkan1.3");
    }

    private static void Compile(string name, string api, string target)
    {
        var info = new ProcessStartInfo("glslangValidator")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            ArgumentList = { api, "--target-env", target, "-S", "comp", "-o", "/dev/null", Path.Combine(Folder, name) }
        };
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Ignore("glslangValidator is not installed");
            return;
        }

        using (process)
        {
            Assert.That(process, Is.Not.Null);
            var output = process!.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.That(process.ExitCode, Is.Zero, output);
        }
    }
}
