using System.Globalization;
using System.Text.RegularExpressions;
using Komet.Rendering;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

// The compile log's lines mapped to the editor's files, the lint warnings, and the history of good states
internal static partial class ShaderBuild
{
    private const int MaxLines = 1 << 16, Lookahead = 96, MaxDepth = 8;

    private static readonly Regex Define =
        new(@"^#define\s+(?<name>[A-Za-z_]\w*)",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex Uniform =
        new(@"^\s*uniform\s+(?:(?:lowp|mediump|highp)\s+)?\w+\s+(?<name>[A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*;",
            RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    // A failed program's stage logs, each line at the file and line it came from (a copy, an include, the game's file)
    private static List<BuildDiagnostic> Logged(ICoreClientAPI api, string name, string copy)
    {
        List<BuildDiagnostic> found = [];
        var program = ShaderMods.ProgramByName(api, name);
        foreach (var (ext, shader) in (ReadOnlySpan<(string, Shader?)>)
                 [(".vsh", program?.VertexShader), (".fsh", program?.FragmentShader), (".gsh", program?.GeometryShader)])
        {
            var stage = "shaders/" + name + ext;
            if (ShaderMods.StageError(stage) is not { Length: > 0 } log) continue;

            var key = ShaderAssets.Key(program?.AssetDomain, stage);
            var origins = shader?.Code is { } code ? Origins(api, key, code, shader.PrefixCode) : [];
            foreach (var diagnostic in Parsed(log, copy).Bounded(MaxDiagnostics))
            {
                var at = diagnostic.Line - 1;
                found.Add(at >= 0 && at < origins.Length && origins[at] is { } origin
                    ? diagnostic with { File = origin.File, Line = origin.Line }
                    : diagnostic with { Message = "(" + name + ext + ":" + diagnostic.Line + ") " + diagnostic.Message });
            }
        }

        return found;
    }

    // For each line of the source the driver got (the prefix after #version, includes expanded): the file and line it
    // came from, null for the engine's own lines. The expansion is redone with origins and matched line by line, so
    // Komet's and other mods' edits shift nothing.
    private static (string File, int Line)?[] Origins(ICoreClientAPI api, string gameFile, string code, string? prefix)
    {
        var at = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
        var compiled = code.Insert(at, prefix ?? "").Split('\n');
        var source = ShaderAssets.Split(gameFile, out _, out var asset)
            ? api.Assets.TryGet(ShaderAssets.Location(asset))?.ToText() ?? ""
            : "";
        List<(string Text, string File, int Line)> expanded = [];
        Expand(source, Shown(gameFile), expanded);
        var result = new (string, int)?[Math.Min(compiled.Length, MaxLines)];
        var next = 0;
        for (var i = 0; i < Math.Min(result.Length, MaxLines); i++)
        {
            var line = compiled[i].Trim();
            for (var k = 0; k < Lookahead && next + k < expanded.Count; k++)
            {
                // a brace, blank or #endif matches only where the cursor is: it would pull every later line along
                if (expanded[next + k].Text == line && (k == 0 || (line.Length > 3 && !line.StartsWith("#e", StringComparison.Ordinal))))
                {
                    result[i] = (expanded[next + k].File, expanded[next + k].Line);
                    next += k + 1;
                    break;
                }
            }
        }

        return result;
    }

    // The engine's HandleIncludes with origins: "#include x" at a line's start becomes x's lines, each include once (a
    // stack of the files being read instead of recursion)
    private static void Expand(string text, string file, List<(string, string, int)> into,
        System.Func<string, string?>? draftInclude = null)
    {
        HashSet<string> seen = [];
        var stack = new Stack<(string[] Lines, string File, int Next)>();
        stack.Push((text.Split('\n'), file, 0));
        for (var step = 0; step < MaxLines && stack.Count > 0 && into.Count < MaxLines; step++)
        {
            var (lines, name, next) = stack.Pop();
            if (next >= lines.Length) continue;

            stack.Push((lines, name, next + 1));
            var line = lines[next].TrimEnd('\r');
            if (stack.Count <= MaxDepth && line.StartsWith("#include", StringComparison.Ordinal) && line.Length > 8 &&
                char.IsWhiteSpace(line[8]))
            {
                var include = line[8..].Trim().ToLowerInvariant();
                if (seen.Add(include) &&
                    (draftInclude?.Invoke(include) ?? Includes?.Invoke()?.GetValueOrDefault(include)) is { } included)
                {
                    stack.Push((included.Split('\n'), Shown(IncludeAsset(include)), 0));
                }

                continue;
            }

            into.Add((line.Trim(), name, next + 1));
        }
    }

    private static string IncludeAsset(string name) =>
        ProjectAssets.Winners.Keys.FirstOrDefault(key => Path.GetFileName(key).Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? "shaderincludes/" + name;

    // A game file as the tree shows it: the winning copy's path, else the installed file's
    private static string Shown(string gameFile) =>
        ProjectAssets.Winners.TryGetValue(gameFile, out var copy)
            ? copy
            : ShaderAssets.InstalledPath(gameFile) ?? gameFile;

    // Komet's lint: a #define never used again, a uniform never read in the file
    private static BuildDiagnostic[] Warnings(string file)
    {
        if (StudioFiles.Read(file) is not { } text || text.Length > MaxText) return [];

        List<BuildDiagnostic> found = [];
        foreach (var (regex, what) in (ReadOnlySpan<(Regex, string)>)[(Define, "define"), (Uniform, "uniform")])
        {
            foreach (var match in regex.Matches(text).Bounded(MaxDiagnostics))
            {
                var name = match.Groups["name"].Value;
                if (Words(text, name) <= 1)
                {
                    var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                    found.Add(new BuildDiagnostic(file, line, 0, false, "unused " + what + " " + name, "Komet"));
                }
            }
        }

        return [.. found];
    }

    private const int MaxText = 1 << 20;

    // How often a name stands as a whole word in the text
    private static int Words(string text, string name)
    {
        var count = 0;
        var at = text.IndexOf(name, StringComparison.Ordinal);
        for (var hit = 0; hit < MaxLines && at >= 0 && count < 2; hit++)
        {
            var before = at == 0 || !(char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_');
            var end = at + name.Length;
            var after = end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_');
            count += before && after ? 1 : 0;
            at = text.IndexOf(name, end, StringComparison.Ordinal);
        }

        return count;
    }

    // A file's good states, newest first
    public static HistoryEntry[] History(string path)
    {
        if (HistoryFolder(path) is not { } folder || !Directory.Exists(folder)) return [];

        try
        {
            return
            [
                .. Directory.GetFiles(folder, "*.txt").OrderDescending(StringComparer.Ordinal).Take(HistoryPerFile)
                    .Select(f => new HistoryEntry(Stamp(f), f))
            ];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // The newest good state's text, the one the game runs
    public static string? LastGood(string path) =>
        History(path) is [var newest, ..] ? StudioFiles.Read(newest.Path) : null;

    // A good state kept (unless it is the newest already); the oldest past ten go
    public static void Snapshot(string path, string text)
    {
        if (HistoryFolder(path) is not { } folder || LastGood(path) == text) return;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        _ = StudioFiles.Write(Path.Join(folder, stamp + ".txt"), text);
        try
        {
            foreach (var old in Directory.GetFiles(folder, "*.txt").OrderDescending(StringComparer.Ordinal)
                         .Skip(HistoryPerFile).Bounded(MaxLines))
            {
                File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // an old state left: the next snapshot tries again
        }
    }

    // A good state written back into the file (then built like an edit); its text, null when there is none
    public static string? Restore(string path, int index)
    {
        var history = History(path);
        if (!Index(index, history.Length) || StudioFiles.Read(history[index].Path) is not { } text) return null;

        return StudioFiles.Write(path, text) ? text : null;
    }

    private static string? HistoryFolder(string path)
    {
        if (ShaderProjects.ProjectOf(path) is not { } project) return null;

        var folder = ShaderProjects.Folder(project);
        var relative = Path.GetRelativePath(folder, StudioFiles.Full(path));
        return relative.StartsWith(ShaderProjects.MetaFolder, StringComparison.Ordinal)
            ? null
            : Path.Join(folder, ShaderProjects.MetaFolder, "history", relative);
    }

    private static DateTime Stamp(string file) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time)
            ? time
            : File.GetLastWriteTime(file);
}
