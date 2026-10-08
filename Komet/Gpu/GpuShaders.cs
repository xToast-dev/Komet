namespace Komet.Gpu;

// Written for OpenGL and Vulkan alike: #version 460, every resource at an explicit binding no other resource of the shader
// shares (Vulkan has one binding namespace per descriptor set, OpenGL one per kind), parameters in a std140 block.
internal static class GpuShaders
{
    public const string HiZ = "hiz.comp", Occlusion = "occlusion.comp", Cull = "cull.comp", Rows = "rows.comp";
    public const string Sort = "sort.comp";
    private const string Prefix = "Komet.Gpu.Shaders.";

    public static readonly string[] All = [HiZ, Occlusion, Cull, Rows, Sort];

    public static string? Source(string name)
    {
        if (!NotNull(name) || !Assert(Array.IndexOf(All, name) >= 0)) return null;
        using var stream = typeof(GpuShaders).Assembly.GetManifestResourceStream(Prefix + name);
        if (!NotNull(stream)) return null;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        return Assert(text.StartsWith("#version 460", StringComparison.Ordinal)) ? text : null;
    }
}
