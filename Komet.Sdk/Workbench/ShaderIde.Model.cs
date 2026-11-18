using Komet.Api;
using Komet.Rendering;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The 3D view's sources: the open shader's two stages as the editor has them (an open tab's text, else the file: the
// project's version where the open file is one, else the game's), expanded without the game (ShaderBuild.Draft) and
// linked by the view 300 ms after the last edit. The game's program of the same name is only read (its last values).
internal sealed partial class ShaderIde
{
    private const long ModelDelayMs = 300;

    private long _modelStamp = -1, _modelAt;
    private int _modelDisk = -1;
    private ShaderDocument? _modelFor;

    public ModelView Model3D { get; } = new();

    public bool CtrlDown => Modifiers.Ctrl;

    private string? PreviewProgram
    {
        get
        {
            if (!ReferenceEquals(_current, _modelFor) || _modelDisk != DiskVersion)
            {
                field = ModelOwner(_current, field);
                (_modelFor, _modelDisk) = (_current, DiskVersion);
            }

            return field;
        }
    }

    public bool HasEngineScene => PreviewProgram is not null;

    public string? PreviewIncludeOwner => IncludeFile(_current) is not null ? PreviewProgram : null;

    public bool LiveModel => Model3D is { On: true, Shape: ModelView.ShapeCount } && HasEngineScene;

    public bool VolumetricModel => LiveModel && PreviewProgram is ("cloudvolumetric" or "cloudmap");

    public void ToggleModel()
    {
        if (_autoModel && Model3D.On) _declinedFor = _current;

        _autoModel = false; // a click makes the view the player's
        Model3D.On = !Model3D.On;
        _modelStamp = -1;
        if (Model3D.On)
        {
            if (TakingSnapshot) SnapshotEnd();

            Model3D.Shape = 0;
            Buffers.Split = -1;
        }
        else
        {
            Model3D.Clear(""); // its GL objects go with the next session end; the program now
        }
    }

    private const long AutoAfterMs = 1500;
    private bool _autoModel;
    private ShaderDocument? _declinedFor; // the player closed the automatic 3D view for this file: it stays closed
    private long _undrawnSince = -1;

    // A shader the game is not drawing now (rift outside a storm, colormap, a pass that is off): the preview shows it in
    // the 3D view instead of an empty picture, and goes back to the game's picture once the game draws it again or
    // another file opens. A 3D view the player turned on stays theirs.
    private bool AutoModel(long now)
    {
        if (Testing) return false; // the shader test holds the view
        var program = _current is { } document && ShaderPaths.IsStage(document.Path) ? PreviewProgram : null;
        var drawn = program is null || DrawCounter.Drawn(program);
        if (drawn || (Model3D.On && !_autoModel))
        {
            _undrawnSince = -1;
            if (!_autoModel || !Model3D.On) return false;

            ToggleModel(); // drawn again or nothing to show: the game's picture
            return true;
        }

        if (Model3D.On || ReferenceEquals(_declinedFor, _current)) return false;

        if (_undrawnSince < 0) _undrawnSince = now;
        if (now - _undrawnSince < AutoAfterMs) return false;

        ToggleModel();
        Model3D.Shape = AutoShape(program!);
        (_autoModel, _declinedFor) = (true, null);
        AddLog(L("model-auto", program!));
        return true;
    }

    // The fitting object for a shader the game is not drawing: a mechanical part on one block, the rest in the scene
    // (effects like rift and galaxy stand there as one portal, sky shaders draw the sky)
    private static int AutoShape(string program)
    {
        const int cube = 2;
        return Array.Exists(ObjectNames, n => program.Contains(n, StringComparison.OrdinalIgnoreCase)) ? cube : 0;
    }

    // Shaders the game draws on single things (an entity, an item, a particle, the GUI), not on the terrain: on the
    // terrain they drew nothing (the shader test showed only the water)
    private static readonly string[] ObjectNames = ["gear", "mech", "entity", "gui", "item", "instanced", "particle"];

    // True while the 3D view shows because the game does not draw the open shader
    public bool AutoModelShown => _autoModel && Model3D.On;

    // Per poll: the sources again after an edit, a tab or project switch
    private bool ModelPoll(long now)
    {
        var auto = AutoModel(now);
        if (!Model3D.On) return auto;

        if (LiveModel)
        {
            _modelStamp = -1;
            return false;
        }

        long stamp = (_current?.Path.GetHashCode(StringComparison.Ordinal) ?? 0) * 31L +
                     (Project?.GetHashCode(StringComparison.Ordinal) ?? 0) + Model3D.Shape;
        for (var i = 0; i < Math.Min(_tabs.Count, MaxTabs + 1); i++)
        {
            stamp = stamp * 31 + _tabs[i].Version;
        }

        // a reload of the game's shaders (a build, a render setting): new program objects, another prefix and
        // include table for the draft
        var engine = PreviewProgram is { } program ? DrawCounter.Program(program) : null;
        stamp = stamp * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine);
        stamp = stamp * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(engine?.VertexShader?.PrefixCode);
        stamp = stamp * 31 + DiskVersion;
        if (stamp != _modelStamp) (_modelStamp, _modelAt) = (stamp, now + (Model3D.Has ? ModelDelayMs : 0));

        if (_modelAt < 0 || now < _modelAt) return false;

        _modelAt = -1;
        ModelSources();
        return true;
    }

    private void ModelSources()
    {
        if (_current is not { } document)
        {
            Model3D.Clear("");
            return;
        }

        if (PreviewPass(document)) return;

        if (PreviewProgram is not { } program)
        {
            Model3D.Clear(L(IncludeFile(document) is null ? "model-no-shader" : "model-no-include-owner"));
            return;
        }

        var vanilla = StudioLink.IsGameShader(document.Path);
        var game = IncludeFile(document) is not null ? ModelGameFile(program) : ModelStageFile(document, vanilla);
        if (game is null || StageText(game, ".fsh", vanilla) is not { } fragment)
        {
            Model3D.Clear(L("model-no-stage"));
            return;
        }

        // a fragment stage the game ships without its vertex stage (galaxy.fsh): the view writes one that feeds it
        var vertex = StageText(game, ".vsh", vanilla) ?? StandInVertex(fragment);
        fragment = OldNoise(fragment);

        if (program == "cloudmap") CloudMapCompanion();

        string? Include(string name) => DraftInclude(name, document, vanilla);
        Model3D.Source(program, ShaderBuild.Draft(_capi, program, ".vsh", vertex, Include),
            ShaderBuild.Draft(_capi, program, ".fsh", fragment, Include));
    }

    private static readonly System.Text.RegularExpressions.Regex InRx =
        new(@"^\s*in\s+(\w+)\s+(\w+)\s*;", System.Text.RegularExpressions.RegexOptions.Multiline,
            TimeSpan.FromMilliseconds(100));

    // A vertex stage for a lone fragment stage: the quad's position through the camera, every input the fragment reads
    // written (uv from the quad, colours white, the rest zero)
    private static string StandInVertex(string fragment)
    {
        var inputs = InRx.Matches(fragment).Take(32).Select(m => (Type: m.Groups[1].Value, Name: m.Groups[2].Value)).ToArray();
        var outs = string.Concat(inputs.Select(i => $"out {i.Type} {i.Name};\n"));
        var sets = string.Concat(inputs.Select(i => $"    {i.Name} = {StandInValue(i.Type, i.Name.ToLowerInvariant())};\n"));
        return "#version 330 core\nlayout(location = 0) in vec3 vertexPosition;\nlayout(location = 1) in vec2 uvIn;\n" +
               "uniform mat4 projectionMatrix;\nuniform mat4 modelViewMatrix;\n" + outs +
               "void main()\n{\n    gl_Position = projectionMatrix * modelViewMatrix * vec4(vertexPosition, 1.0);\n" + sets + "}\n";
    }

    // galaxy.fsh calls cnoise(vec2), a name noise2d.ash no longer has (it is cnoise2 now); the game never loads the
    // file, so it never failed there. The view maps the old name for its own compile only, the file stays as it is.
    private static string OldNoise(string fragment)
    {
        if (!fragment.Contains("cnoise(", StringComparison.Ordinal) ||
            fragment.Contains("float cnoise(", StringComparison.Ordinal)) return fragment;

        var line = fragment.IndexOf('\n', StringComparison.Ordinal);
        return line > 0 && fragment.StartsWith("#version", StringComparison.Ordinal)
            ? fragment[..(line + 1)] + "#define cnoise cnoise2\n" + fragment[(line + 1)..]
            : "#define cnoise cnoise2\n" + fragment;
    }

    private static string StandInValue(string type, string name) => type switch
    {
        "vec2" when name.Contains("uv", StringComparison.Ordinal) || name.Contains("tex", StringComparison.Ordinal) => "uvIn",
        "vec4" when name.Contains("color", StringComparison.Ordinal) || name.Contains("rgba", StringComparison.Ordinal) => "vec4(1.0)",
        "float" => "0.0",
        "int" => "0",
        _ => type + "(0.0)"
    };

    private static string? ModelGameFile(string program) =>
        ShaderAssets.InstalledPath(ShaderAssets.ProgramKey(program, ".fsh"));

    private static string? ModelStageFile(ShaderDocument document, bool vanilla) =>
        vanilla ? document.Path : StudioLink.GameFileOf(document.Path);

    private static string? IncludeFile(ShaderDocument? document)
    {
        if (document is null) return null;

        var game = StudioLink.IsGameShader(document.Path) ? document.Path : StudioLink.GameFileOf(document.Path);
        var relative = game is null ? null : StudioLink.Relative(game);
        return relative is not null && ShaderAssets.Split(relative, out _, out var asset) &&
               (asset.StartsWith("shaderincludes/", StringComparison.Ordinal) ||
                asset.EndsWith(".ash", StringComparison.OrdinalIgnoreCase))
            ? Path.GetFileName(asset)
            : null;
    }

    // Preview includes through a complete program, preferring the previous consumer so the scene stays in context.
    private string? ModelOwner(ShaderDocument? document, string? previous)
    {
        if (IncludeFile(document) is not { } include) return Program(document);

        var consumers = ShaderMods.Diagnostics(_capi).Where(p => p.Compiled &&
            p.Includes.Contains(include, StringComparer.OrdinalIgnoreCase) && File.Exists(ModelGameFile(p.Pass)) &&
            File.Exists(Path.ChangeExtension(ModelGameFile(p.Pass), ".vsh"))).Select(p => p.Pass).ToArray();
        if (previous is not null && consumers.Contains(previous, StringComparer.Ordinal)) return previous;

        foreach (var preferred in (ReadOnlySpan<string>)["chunkopaque", "standard", "entityanimated", "helditem", "sky", "final"])
        {
            if (consumers.Contains(preferred, StringComparer.Ordinal)) return preferred;
        }

        // an include the game's GUI also reads (normalshading): a world program shows it in the scene, the GUI's not
        return consumers.FirstOrDefault(p => !p.StartsWith("gui", StringComparison.Ordinal)) ?? consumers.FirstOrDefault();
    }

    private string? DraftInclude(string name, ShaderDocument document, bool vanilla)
    {
        if (IncludeFile(document) == name) return document.Text;

        if (vanilla || Project is not { } project || Path.GetFileName(name) != name) return null;

        if (ShaderProjects.CopyPath(project, "shaderincludes/" + name) is not { } copy) return null;

        return _tabs.Find(tab => StudioLink.PathEquals(tab.Path, copy))?.Text ?? StudioFiles.Read(copy);
    }

    private void CloudMapCompanion()
    {
        const string program = "cloudvolumetric";
        var game = ModelGameFile(program);
        if (game is null || StageText(game, ".vsh", true) is not { } vertex ||
            StageText(game, ".fsh", true) is not { } fragment) return;

        Model3D.WeatherCompanion(ShaderBuild.Draft(_capi, program, ".vsh", vertex),
            ShaderBuild.Draft(_capi, program, ".fsh", fragment));
    }

    // A Komet pass is a fragment body. Preview it even before it is registered in the manifest.
    private bool PreviewPass(ShaderDocument document)
    {
        if (!document.Path.EndsWith(".fsh", StringComparison.OrdinalIgnoreCase) ||
            StudioLink.ProjectOf(document.Path) is not { } project) return false;

        var folder = Path.Join(StudioLink.ProjectsFolder, project);
        var manifest = Path.Join(folder, ShaderPacks.PackFile);
        var relative = Path.GetRelativePath(folder, document.Path).Replace('\\', '/');
        var json = _tabs.Find(tab => StudioLink.PathEquals(tab.Path, manifest))?.Text ?? StudioFiles.Read(manifest);
        var pack = json is null ? null : ShaderPacks.Parsed(null, json, project.ToLowerInvariant(), manifest);
        var spec = pack?.Passes.Find(pass => pass.Fragment.Replace('\\', '/') == relative);
        if (spec is null && !relative.StartsWith(ShaderProjects.PassesFolder + "/", StringComparison.Ordinal)) return false;

        var mod = ShaderPacks.ModPrefix + (pack?.Id ?? project.ToLowerInvariant());
        var id = spec?.Id ?? Path.GetFileNameWithoutExtension(document.Path);
        var pass = new KapiPass(id, document.Text);
        Model3D.Source(id, ModPasses.Vertex, ModPasses.Source(mod, pass), fullscreenPass: true);
        return true;
    }

    // One stage's text: the project's version unless the open file is the game's own, an open tab before the disk
    private string? StageText(string game, string ext, bool vanilla)
    {
        var file = Path.ChangeExtension(game, ext);
        var path = !vanilla && Project is { } project && StudioLink.CopyPath(project, file) is { } copy &&
                   File.Exists(copy) ? copy : file;
        for (var i = 0; i < Math.Min(_tabs.Count, MaxTabs + 1); i++)
        {
            if (StudioLink.PathEquals(_tabs[i].Path, path)) return _tabs[i].Text;
        }

        return StudioLink.Read(path, out var text, out _) ? text : null;
    }

    private int ModelPicture(int sharp)
    {
        var engine = PreviewProgram is { } name ? DrawCounter.Program(name)?.ProgramId ?? 0 : 0;
        var texture = Model3D.Picture(_capi, sharp, engine);
        if (Model3D.TakeLog() is { Length: > 0 } log) AddLog(L("model-failed", log.Trim()), true);

        return texture;
    }
}
