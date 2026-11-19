using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

// Builds a project's changed files in the running game (Strg+S / Testen). A copy of a game shader is tried as a draft
// (ProjectAssets.Try), the shaders reload, and every program that reads the file is looked up anew by name
// (capi.Shader.GetProgramByName: the reload disposed the old objects, so a check on a kept reference reads a stale
// program). Its compile log is parsed and each line mapped from the source the driver got (prefix after #version,
// includes expanded) back to the file and line the editor shows. A failed file rolls back to its last good state (the
// game reloads once more without the draft; the file on disk stays as written) and keeps its errors; a good build keeps
// each changed file in the history (10 a file), whose newest entry is the "letzter guter Stand" the game runs.
// A pass file or komet-shader.json reloads the packs: passes compile at their next draw, so their state is Pending until
// Poll reads ModPasses. Vulkan: a program's port (ShaderMods.Ported) is Pending until its worker finished; Poll again.
//
// API (main thread): Build(api, project, files) -> BuildResult; Poll(api, project) -> BuildResult? (pending states
// settled); Errors(project); History(path) -> HistoryEntry[] newest first; LastGood(path); Snapshot(path, text); Restore
// takes a history entry back and returns the text it wrote, or null.
internal static partial class ShaderBuild
{
    internal const int MaxDiagnostics = 200;
    private const int HistoryPerFile = 10;
    private const int MaxPrograms = 64;

    private static readonly Dictionary<string, BuildResult> Results = new(StringComparer.Ordinal);

    private static readonly AccessTools.FieldRef<Dictionary<string, string>?>? Includes =
        AccessTools.Field(typeof(ShaderRegistry), "includes") is { IsStatic: true } field &&
        field.FieldType == typeof(Dictionary<string, string>)
            ? AccessTools.StaticFieldRefAccess<Dictionary<string, string>?>(field)
            : null;

    private static readonly Regex LogLine =
        new(
            @"^\s*(?:(?<sev>ERROR|WARNING):\s*)?\d+[:(](?<line>\d+)\)?(?:\(\d+\))?\s*:\s*(?:(?<sev2>error|warning)\b[^:]*:)?\s*(?<msg>.*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromMilliseconds(100));

    public static BuildResult Build(ICoreClientAPI api, string project, string[] files)
    {
        if (!GL.IsEnabled(EnableCap.DebugOutput)) return BuildDraft(api, project, files);

        // The game's debug callback throws on compiler errors. Keep them in the shader log during a trial.
        GL.PushDebugGroup(DebugSourceExternal.DebugSourceApplication, 0, -1, "Komet shader trial");
        try
        {
            GL.DebugMessageControl(DebugSourceControl.DebugSourceShaderCompiler, DebugTypeControl.DebugTypeError,
                DebugSeverityControl.DontCare, 0, Array.Empty<int>(), false);
            return BuildDraft(api, project, files);
        }
        finally
        {
            GL.PopDebugGroup();
        }
    }

    private static BuildResult BuildDraft(ICoreClientAPI api, string project, string[] files)
    {
        if (ShaderRegistry.SupressShaderAndBufferReloads)
        {
            return Keep(new BuildResult(project, false, true, false, []));
        }

        var copies = files.Where(f => ShaderProjects.GameFileOf(f) is not null).Distinct().Take(MaxPrograms).ToArray();
        var packs = files.Any(f => !copies.Contains(f) && ShaderProjects.ProjectOf(f) == project);
        Dictionary<string, string> drafts = [];
        foreach (var copy in copies.Bounded(MaxPrograms))
        {
            if (StudioFiles.Read(copy) is { } text)
            {
                drafts[copy] = text;
                ProjectAssets.Try(copy, Encoding.UTF8.GetBytes(text));
            }
        }

        if (packs) ShaderPacks.Load(api);

        ProjectAssets.Changed();
        var reloaded = api.Shader.ReloadShaders();
        var checks = copies.Select(c => Check(api, project, c)).ToArray();
        var failed = checks.Where(c => c.Gl == BuildState.Error).Select(c => c.File).ToHashSet();
        // only a draft the game compiled is a good state: off, overlaid or unused ones proved nothing
        var proven = checks.Where(c => c.Gl == BuildState.Ok).Select(c => c.File).ToHashSet();
        foreach (var copy in copies.Bounded(MaxPrograms))
        {
            ProjectAssets.EndTrial(copy);
            ProjectAssets.Built(copy, !failed.Contains(copy));
            if (proven.Contains(copy) && drafts.TryGetValue(copy, out var good)) Snapshot(copy, good);
        }

        if (failed.Count > 0)
        {
            _ = api.Shader.ReloadShaders(); // the last good states back in the game
        }

        FileBuild[] all = [.. checks, .. files.Where(f => !copies.Contains(f)).Select(PassFile)];
        return Keep(new BuildResult(project, reloaded && failed.Count == 0, false, failed.Count > 0, all));
    }

    // Pending states settled where they could: Vulkan ports done, passes compiled at their draw
    public static BuildResult? Poll(ICoreClientAPI api, string project)
    {
        if (!Results.TryGetValue(project, out var result)) return null;

        var files = result.Files.Select(f => Settled(api, f)).ToArray();
        return Keep(result with { Files = files });
    }

    private static FileBuild Settled(ICoreClientAPI api, FileBuild file)
    {
        if (file.Gl == BuildState.Pending) return PassFile(file.File);

        if (file.Vulkan != BuildState.Pending) return file;

        return file with { Vulkan = Port(api, file.Programs) };
    }

    private static BuildResult Keep(BuildResult result)
    {
        Results[result.Project] = result;
        return result;
    }

    private static FileBuild Check(ICoreClientAPI api, string project, string copy)
    {
        var game = ShaderProjects.GameFileOf(copy)!;
        if (ProjectAssets.InterfaceError(copy) is { } error)
        {
            var diagnostic = new BuildDiagnostic(copy, 1, 0, true, error, "Engine");
            return new FileBuild(copy, BuildState.Error, BuildState.NotUsed, [], [diagnostic], []);
        }

        if (!ShaderProjects.IsActive(project))
        {
            return new FileBuild(copy, BuildState.Off, BuildState.Off, [], [], Warnings(copy));
        }

        if (!ProjectAssets.Winners.TryGetValue(game, out var winner) ||
            !string.Equals(winner, StudioFiles.Full(copy), StringComparison.Ordinal))
        {
            return new FileBuild(copy, BuildState.Overlaid, BuildState.Overlaid, [], [], Warnings(copy));
        }

        var programs = Programs(api, game);
        List<BuildDiagnostic> diagnostics = [];
        var gl = programs.Length == 0 ? BuildState.NotUsed : BuildState.Ok;
        foreach (var name in programs.Bounded(MaxPrograms))
        {
            // looked up anew: the reload replaced the program objects
            if (ShaderMods.ProgramByName(api, name) is { ProgramId: > 0, LoadError: false } &&
                ShaderMods.CompiledNow(name) != false)
            {
                continue;
            }

            gl = BuildState.Error;
            diagnostics.AddRange(Logged(api, name, copy));
        }

        if (gl == BuildState.Error && !diagnostics.Exists(d => d.Error))
        {
            diagnostics.Add(new BuildDiagnostic(copy, 1, 0, true, "compile failed (no log)", "OpenGL"));
        }

        var vulkan = gl == BuildState.Ok ? Port(api, programs) : BuildState.NotUsed;
        return new FileBuild(copy, gl, vulkan, programs, [.. diagnostics.Take(MaxDiagnostics)], Warnings(copy));
    }

    // The programs reading a game file: shaders/<name>.* is the program <name>; an include, every program including it
    private static string[] Programs(ICoreClientAPI api, string game)
    {
        if (ShaderAssets.Split(game, out var domain, out var path) &&
            path.StartsWith("shaders/", StringComparison.Ordinal) && !path.EndsWith(".ash", StringComparison.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (ShaderMods.ProgramByName(api, name) is { } program && (program.AssetDomain ?? "game") == domain)
            {
                return [name];
            }
        }

        var file = Path.GetFileName(game).ToLowerInvariant();
        return
        [
            .. ShaderMods.Diagnostics(api).Where(p => p.Includes.Any(i =>
                    Path.GetFileName(i).Equals(file, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Pass).Distinct(StringComparer.Ordinal).Take(MaxPrograms)
        ];
    }

    // Vulkan: Ok when every program's port went through, Pending while one translates, NotUsed under OpenGL
    private static BuildState Port(ICoreClientAPI api, string[] programs)
    {
        if (ShaderHub.Renderer != KapiRenderer.Vulkan || programs.Length == 0) return BuildState.NotUsed;

        var pending = false;
        foreach (var name in programs.Bounded(MaxPrograms))
        {
            switch (ShaderMods.PortState(api, name))
            {
                case null:
                    pending = true;
                    break;
                case { Length: > 0 }:
                    return BuildState.Error;
            }
        }

        return pending ? BuildState.Pending : BuildState.Ok;
    }

    // A pass file or komet-shader.json: the pass's state once it was built at its draw
    private static FileBuild PassFile(string file)
    {
        var relative = ShaderProjects.ProjectOf(file) is { } project
            ? Path.GetRelativePath(ShaderProjects.Folder(project), file).Replace('\\', '/')
            : "";
        if (!relative.StartsWith(ShaderProjects.PassesFolder + "/", StringComparison.Ordinal))
        {
            return new FileBuild(file, BuildState.NotUsed, BuildState.NotUsed, [], [], []);
        }

        var pass = Pass(file, relative);
        if (pass is null or { Program: <= 0, Failed: false })
        {
            var state = pass is not null && pass.LastEnabled != false ? BuildState.Pending : BuildState.NotUsed;
            return new FileBuild(file, state, BuildState.NotUsed, [], [], Warnings(file));
        }

        BuildDiagnostic[] diagnostics = pass.Failed ? [.. Parsed(pass.Failure, file)] : [];
        return new FileBuild(file, pass.Failed ? BuildState.Error : BuildState.Ok, BuildState.NotUsed, [pass.Id],
            diagnostics, Warnings(file));
    }

    // The registered pass a project's pass file is: komet-shader.json names it (the pass keeps its source, not its file)
    private static ModPasses.Diagnostic? Pass(string file, string relative)
    {
        if (ShaderProjects.ProjectOf(file) is not { } project || !ShaderProjects.IsActive(project) ||
            StudioFiles.Read(Path.Join(ShaderProjects.Folder(project), ShaderPacks.PackFile)) is not { } json ||
            ShaderPacks.Parsed(null, json, project.ToLowerInvariant(), file) is not { } pack ||
            pack.Passes.Find(p => p.Fragment.Replace('\\', '/') == relative) is not { } spec)
        {
            return null;
        }

        var mod = ShaderPacks.ModPrefix + pack.Id;
        return ModPasses.Diagnostics().FirstOrDefault(d => d.Mod == mod && d.Id == spec.Id);
    }

    private static List<BuildDiagnostic> Parsed(string log, string file)
    {
        List<BuildDiagnostic> found = [];
        foreach (var raw in log.Split('\n').Bounded(MaxDiagnostics))
        {
            var match = LogLine.Match(raw.TrimEnd('\r'));
            if (match.Success && int.TryParse(match.Groups["line"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var line))
            {
                found.Add(new BuildDiagnostic(file, Math.Max(1, line), 0, !Warning(match),
                    match.Groups["msg"].Value.Trim(), "OpenGL"));
            }
        }

        return found;
    }

    private static bool Warning(Match match) =>
        match.Groups["sev"].Value.Equals("WARNING", StringComparison.OrdinalIgnoreCase) ||
        match.Groups["sev2"].Value.Equals("warning", StringComparison.OrdinalIgnoreCase);
}

internal enum BuildState
{
    Ok,
    Error,
    Pending,
    NotUsed, // no program read it in this frame's game (or OpenGL draws: no Vulkan state)
    Off, // its project is switched off
    Overlaid // another active project's copy wins
}

// One message at a file's line (1-based) and column (0: unknown), error or warning, from OpenGL, Vulkan or Komet's lint
internal sealed record BuildDiagnostic(string File, int Line, int Column, bool Error, string Message, string Renderer);

// One built file: its OpenGL and Vulkan states, the programs that read it, its messages, warnings
internal sealed record FileBuild(
    string File,
    BuildState Gl,
    BuildState Vulkan,
    string[] Programs,
    BuildDiagnostic[] Diagnostics,
    BuildDiagnostic[] Warnings);

// A build: its project, whether all went in, skipped (reloads suppressed), rolled back (a file failed), files
internal sealed record BuildResult(
    string Project,
    bool Ok,
    bool Skipped,
    bool RolledBack,
    FileBuild[] Files);

// A good state of a file: when, and its text
internal sealed record HistoryEntry(DateTime Time, string Path);
