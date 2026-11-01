using Komet.Rendering;

namespace Komet.Test.Gpu;

// glslangValidator, the reference compiler, is stricter than some drivers; without it on the PATH those cases are skipped. The
// Vulkan side is compiled by shaderc (the same glslang) in the GPU tests that run the shaders.
public sealed class GpuShadersTests
{
    private static readonly string Folder = Path.Combine(Paths.Repo, "Komet.Rendering", "Gpu", "Shaders");

    [TestCaseSource(typeof(GpuShaders), nameof(GpuShaders.All))]
    public void TheAssemblyCarriesTheFile(string name)
    {
        Assert.That(GpuShaders.Source(name), Is.EqualTo(File.ReadAllText(Path.Combine(Folder, name))));
    }

    [TestCaseSource(typeof(GpuShaders), nameof(GpuShaders.All))]
    public void ItCompilesForOpenGl(string name)
    {
        var info = new ProcessStartInfo("glslangValidator")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            ArgumentList =
            {
                "-G", "--target-env", "opengl", "-S", "comp", "-o", "/dev/null", Path.Combine(Folder, name)
            }
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
