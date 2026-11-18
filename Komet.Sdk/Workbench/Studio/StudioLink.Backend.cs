using Komet.Rendering;

namespace Komet.Sdk;

// Projects (ShaderProjects), the frame's modules (ShaderModules), builds and history (ShaderBuild). Game files go in
// and out as full paths; the backend's asset paths stay in here.
internal sealed partial class StudioLink
{
    public static ShaderProject[] Projects() => ShaderProjects.All();

    // The project in the editor: its copies win first
    public static void Editing(string? project)
    {
        if (ShaderProjects.Editing != project) ShaderProjects.Editing = project;
    }

    public bool SetActive(string project, bool on) => ShaderProjects.SetActive(capi, project, on);

    public static string? CreateProject(string name) => ShaderProjects.Create(name, "empty");

    // A game shader into a project; the copy's path (the existing copy when there is one)
    public static string? CopyIn(string gameFile, string project) =>
        Relative(gameFile) is { } relative ? ShaderProjects.CopyIn(relative, project) : null;

    public static string? CopyPath(string project, string gameFile) =>
        Relative(gameFile) is { } relative && ShaderProjects.CopyPath(project, relative) is { } copy &&
               File.Exists(copy)
            ? copy
            : null;

    // The copy the game draws for a game shader
    public static string? Winner(string gameFile) =>
        Relative(gameFile) is { } relative && ShaderProjects.Winner(relative) is { } project
            ? CopyPath(project, gameFile)
            : null;

    // The other project whose copy beats this copy, null when this one draws
    public static string? OverlaidBy(string copy) =>
        ProjectOf(copy) is { } project && ShaderProjects.GameFileOf(copy) is { } game
            ? ShaderProjects.OverlaidBy(project, game)
            : null;

    public static (string Old, string New)? OriginalChanged(string copy) =>
        ProjectOf(copy) is null ? null : ShaderProjects.OriginalChanged(copy);

    public static void MarkSeen(string copy) => ShaderProjects.MarkSeen(copy);

    // Every stage's modules in frame order
    public ShaderModule[] Modules()
    {
        List<ShaderModule> modules = [];
        foreach (var stage in ShaderModules.Stages(capi).Bounded(64))
        {
            foreach (var module in stage.Modules.Bounded(MaxEntries))
            {
                modules.Add(module);
            }
        }

        return [.. modules];
    }

    // The Frame section shows: the backend attributes programs and times while it listens
    public static void Listening(bool on)
    {
        if (ShaderModules.Listening != on) ShaderModules.Listening = on;
    }

    // One place up or down within the stage, kept as the project's order
    public bool MoveModule(string id, int direction, string? project) =>
        ShaderModules.MoveKept(capi, id, direction, project);

    public bool SetOverrides(string[] ids, bool on) => ShaderModules.SetOverrides(capi, ids, on);

    // Several active projects copy the file: whose copy runs
    public void Pick(string id, string project) => ShaderModules.SetPick(capi, id, project);

    // Reset to defaults: the project's overrides on, its orders and the pack's values back to komet-shader.json
    public void ResetDefaults(string project, string? pack)
    {
        if (pack is not null) ShaderPacks.Reset(capi, pack);

        ShaderModules.Reset(capi, project);
    }

    public static string ModulesError => ShaderModules.Error;

    // Builds the project's changed files: the game runs them or keeps the last good state; each problem on its line
    public BuildReport Build(string project, IReadOnlyList<string> files) =>
        Report(ShaderBuild.Build(capi, project, [.. files]));

    // Pending states (Vulkan ports, passes compiling at their draw) settled; null without a build of the project
    public BuildReport? Poll(string project) => ShaderBuild.Poll(capi, project) is { } result ? Report(result) : null;

    private static BuildReport Report(BuildResult result)
    {
        List<BuildIssue> issues = [];
        var pending = false;
        foreach (var file in result.Files.Bounded(MaxEntries))
        {
            pending |= file.Gl == BuildState.Pending || file.Vulkan == BuildState.Pending;
            foreach (var d in file.Diagnostics.Bounded(ShaderBuild.MaxDiagnostics))
            {
                issues.Add(Issue(d));
            }

            foreach (var d in file.Warnings.Bounded(ShaderBuild.MaxDiagnostics))
            {
                issues.Add(Issue(d));
            }
        }

        var files = result.Files.Select(f => f.File).ToArray();
        return new BuildReport(result is { Ok: true, RolledBack: false }, [.. issues], result.Skipped, pending, files);
    }

    private static BuildIssue Issue(BuildDiagnostic d) =>
        new(StudioFiles.Full(d.File), Math.Max(0, d.Line - 1), Math.Max(1, d.Column), d.Error,
            d.Message, d.Renderer is "Vulkan" or "OpenGL" ? d.Renderer : "");

    // A file's good states, newest first (the first is the last good one); the text read when asked
    public static StudioSnapshot[] History(string file) =>
        [.. ShaderBuild.History(file).Select(h => new StudioSnapshot(h.Time, h.Path))];

    public static string SnapshotText(StudioSnapshot shot) => StudioFiles.Read(shot.Path) ?? "";

    // A good state written back into the file; its text, null when there is none
    public static string? RestoreHistory(string file, int index) => ShaderBuild.Restore(file, index);

    // Mods/<modid>-<version>.zip; null with why
    public static string? Export(string project, string modId, string name, string version, out string error)
    {
        var zip = ShaderProjects.Export(project, modId, name, version);
        error = zip is null ? ShaderProjects.Error : "";
        return zip;
    }
}
