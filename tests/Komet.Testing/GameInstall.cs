namespace Komet.Testing;

// The Vintage Story installation a test reads assets and engine assemblies from: the VINTAGE_STORY environment variable, else the
// VsInstall this library was built against, else /opt/vintagestory; the first folder that exists. A test that reads the game's
// assets calls RequireAssets first, so a machine without an installation skips it instead of failing.
public static class GameInstall
{
    public const string Variable = "VINTAGE_STORY", Fallback = "/opt/vintagestory";

    private static int _resolving;

    public static string Directory { get; } = Resolve();
    public static string Assets => Path.Combine(Directory, "assets");
    public static string Lib => Path.Combine(Directory, "Lib");
    public static string Mods => Path.Combine(Directory, "Mods");

    public static void RequireAssets()
    {
        if (!System.IO.Directory.Exists(Path.Combine(Assets, "game", "shapes")))
            Assert.Ignore($"no game assets under {Assets}: this test needs a Vintage Story installation");
    }

    // The game's own dependencies from the installation, where the engine's AssemblyResolver finds them: the game folder, Lib and
    // Mods, not the data folder's Mods. A test's output holds only what it references, so code that reaches protobuf-net, cairo,
    // OpenTK.Graphics or SQLite needs this. Registered once per process, as the game registers its resolver; later calls do nothing.
    public static void ResolveAssemblies()
    {
        if (Interlocked.Exchange(ref _resolving, 1) == 0) AppDomain.CurrentDomain.AssemblyResolve += FromInstall;
    }

    private static string Resolve()
    {
        var stamped = typeof(GameInstall).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "VsInstall")?.Value;
        string?[] candidates = [Environment.GetEnvironmentVariable(Variable), stamped];
        return candidates.FirstOrDefault(dir => !string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) ??
               Fallback;
    }

    private static Assembly? FromInstall(object? sender, ResolveEventArgs args)
    {
        var file = new AssemblyName(args.Name).Name + ".dll";
        return new[] { Directory, Lib, Mods }.Select(directory => Path.Combine(directory, file)).Where(File.Exists)
            .Select(Assembly.LoadFrom).FirstOrDefault();
    }
}
