using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace Komet.Sdk;

// What the Engine view shows for a shader program: the methods that name it (its literal, its ShaderPrograms getter),
// the sites that set its uniforms (callers of ShaderProgram<Name>'s property setters, methods loading a uniform's
// literal, its own class first), each decompiled on a worker with a header saying where it is. The text is cached on
// disk per game version (GamePaths.Cache/komet-engine), never in the mod. Null text: nothing found, with the reason.
internal static partial class EngineSource
{
    private const int MaxShown = 14, MaxPerUniform = 3;

    private static readonly Dictionary<string, Task<string?>> Running = new(StringComparer.OrdinalIgnoreCase);

    private static string CacheFile(string program) =>
        Path.Join(GamePaths.Cache, "komet-engine", GameVersion.ShortGameVersion, program.ToLowerInvariant() + ".cs");

    // The engine code for a program and the uniforms its file declares; null while it runs (poll again) — or the
    // finished text, "" when the game has none for it
    public static string? Query(string program, string[] uniforms)
    {
        var cache = CacheFile(program);
        if (File.Exists(cache)) return File.ReadAllText(cache);

        if (!Ready) return Failed ? "" : null;

        if (!Running.TryGetValue(program, out var task))
        {
            task = Task.Run(() => Build(program, uniforms));
            Running[program] = task;
        }

        return task.IsCompleted ? task.Result ?? "" : null;
    }

    private static string? Build(string program, string[] uniforms)
    {
        var sites = Pick(program, uniforms);
        if (sites.Count == 0) return null;

        var text = new StringBuilder();
        _ = text.Append("// Engine code that uses the shader program \"").Append(program)
            .Append("\", decompiled from your own ")
            .Append(
                "game installation (read-only).\n// Ctrl+click a uniform's declaration in the shader to jump to where it is set.\n\n");
        foreach (var group in sites.GroupBy(s => s.Assembly).ToArray().Bounded(8))
        {
            Decompile(text, group.Key, [.. group]);
        }

        var result = text.ToString();
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(CacheFile(program))!);
            File.WriteAllText(CacheFile(program), result);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Error = e.Message; // shown anyway, only not kept
        }

        return result;
    }

    // The program's sites first, then per uniform its own class's setter and that setter's callers, else its literals
    private static List<Site> Pick(string program, string[] uniforms)
    {
        List<Site> picked = [];
        Take(picked, Strings.GetValueOrDefault(program), MaxShown);
        Take(picked, Calls.GetValueOrDefault("ShaderPrograms::get_" + program), MaxShown);
        var own = "ShaderProgram" + program + "::set_";
        foreach (var uniform in uniforms.Bounded(32))
        {
            var setter = own + uniform;
            Take(picked, Calls.TryGetValue(setter, out var callers) ? callers : Strings.GetValueOrDefault(uniform),
                MaxPerUniform);
        }

        return picked;
    }

    private static void Take(List<Site> picked, List<Site>? sites, int max)
    {
        var taken = 0;
        foreach (var site in (sites ?? []).Bounded(MaxSites))
        {
            if (taken >= max || picked.Count >= MaxShown) return;

            if (!picked.Contains(site))
            {
                picked.Add(site);
                taken++;
            }
        }
    }

    private static void Decompile(StringBuilder text, int assembly, Site[] sites)
    {
        var path = Paths[assembly];
        using var file = new PEFile(path);
        var resolver = new UniversalAssemblyResolver(path, false, file.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(GamePaths.Binaries);
        resolver.AddSearchDirectory(Path.Join(GamePaths.Binaries, "Lib"));
        var decompiler = new CSharpDecompiler(file, resolver,
            new DecompilerSettings { ThrowOnAssemblyResolveErrors = false });
        foreach (var site in sites.Bounded(MaxShown))
        {
            var method = file.Metadata.GetMethodDefinition(site.Method);
            var type = file.Metadata.GetTypeDefinition(method.GetDeclaringType());
            _ = text.Append("// ").Append(Path.GetFileNameWithoutExtension(path)).Append(" · ")
                .Append(file.Metadata.GetString(type.Namespace)).Append('.').Append(file.Metadata.GetString(type.Name))
                .Append('.').Append(file.Metadata.GetString(method.Name)).Append('\n');
            try
            {
                _ = text.Append(decompiler.DecompileAsString(site.Method).ReplaceLineEndings("\n")).Append("\n\n");
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _ = text.Append("// could not be decompiled: ").Append(e.Message).Append("\n\n");
            }
        }
    }

    // The first line that sets a uniform: its literal in quotes, else an assignment to its setter property
    public static int LineOf(string text, string uniform)
    {
        var lines = text.Split('\n');
        var quoted = "\"" + uniform + "\"";
        var assigned = char.ToUpperInvariant(uniform[0]) + uniform[1..] + " =";
        var fallback = -1;
        for (var i = 0; i < Math.Min(lines.Length, 1 << 16); i++)
        {
            if (lines[i].Contains(quoted, StringComparison.Ordinal)) return i;

            fallback = fallback < 0 && lines[i].Contains(assigned, StringComparison.Ordinal) ? i : fallback;
        }

        return fallback;
    }
}
