using Komet.Rendering;

namespace Komet.Sdk;

internal enum PassSource
{
    Vanilla,
    Override,
    Pack
}

// One resolved pass: id, name (never empty), stage, order in the stage, where its shader comes from, whether an override
// exists and is switched on, a failed override (the game runs the last good state or vanilla), main-thread ms, the
// settings it declares (pack-setting keys), the file a click opens (the override's copy, else the game's read-only
// file), the active projects that copy it (more than one: the row offers a pick) and, for a pack pass, its switch key
internal sealed record PassEntry(
    string Id,
    string Name,
    string Stage,
    int Order,
    PassSource Source,
    bool HasOverride,
    bool OverrideOn,
    bool Failed,
    double? Ms,
    string[] Settings,
    string? File,
    string? GameFile,
    string[] Candidates,
    string? When,
    bool On);

// The workbench's single view of the frame's passes: the engine's modules with their overrides (the projects' copies) and
// the active pack's own passes. The Passes tab, SETTINGS and the FRAME bar read only this. Rebuilt with every module
// poll; Changed fires once when anything but the times differs (ids, sources, switches, failures, settings).
internal sealed class PassModel
{
    public const int MaxPasses = 256;
    private const string PackStage = "AfterFinalComposition";

    // Who copies a module's programs and the text of pack fragments: looked up again every few seconds, not every poll
    private readonly Dictionary<string, string[]> _copiers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);
    private long _cachedAt;

    private string _signature = "";

    public PassEntry[] Passes { get; private set; } = [];

    // Pack setting keys some pass declares; the others belong to the pack as a whole
    private HashSet<string> Owned { get; } = new(StringComparer.Ordinal);

    public int OverridesActive => Passes.Count(p => p is { HasOverride: true, OverrideOn: true });

    public event Action? Changed;

    // The keys SETTINGS shows: the pack's own settings plus those of passes that are on, minus the pack passes' switches
    public bool Shows(string key)
    {
        if (Passes.Any(p => p.When == key)) return false;

        return !Owned.Contains(key) || Passes.Any(p => p.On && p.Settings.Contains(key));
    }

    public PassEntry? Find(string id) => Array.Find(Passes, p => p.Id == id);

    public void Rebuild(ShaderModule[] modules, ShaderPacks.Pack? pack, string? folder,
        System.Func<string, double> value, System.Func<string, int> errors)
    {
        Expire();
        List<PassEntry> passes = [];
        foreach (var module in modules.Bounded(MaxPasses))
        {
            passes.Add(Engine(module, errors));
        }

        Owned.Clear();
        if (pack is not null && folder is not null)
        {
            var order = passes.Count(p => p.Stage == PackStage);
            var at = passes.FindLastIndex(p => p.Stage == PackStage) + 1;
            at = at > 0 ? at : passes.Count;
            foreach (var spec in pack.Passes.Bounded(32))
            {
                passes.Insert(at++, Packed(spec, pack, folder, value, order++));
            }
        }

        Passes = [.. passes];
        var signature = string.Join('\n', Passes.Select(p =>
            $"{p.Id}|{p.Name}|{p.Order}|{p.Source}|{p.HasOverride}|{p.OverrideOn}|{p.Failed}|{p.On}|{p.Candidates.Length}|{string.Join(',', p.Settings)}"));
        if (signature != _signature)
        {
            _signature = signature;
            Changed?.Invoke();
        }
    }

    private void Expire()
    {
        var now = Environment.TickCount64;
        if (now - _cachedAt > 3000)
        {
            _cachedAt = now;
            _copiers.Clear();
            _texts.Clear();
        }
    }

    // Over all its programs and stage files, as the backend sees them: an override exists while an active project
    // copies one of them; it is on while one of those projects keeps a program off its vanilla list
    private PassEntry Engine(ShaderModule module, System.Func<string, int> errors)
    {
        if (!_copiers.TryGetValue(module.Id, out var candidates))
        {
            candidates = ShaderModules.Copiers(module.Programs);
            _copiers[module.Id] = candidates;
        }

        var game = GameFile(module);
        var owner = module.Project ?? candidates.FirstOrDefault();
        var copy = owner is null ? null : CopyOf(owner, module.Programs);
        var on = copy is not null && candidates.Any(p => module.Programs.Any(g =>
            ShaderModules.Delta(p)?.Vanilla.Contains(g) != true));
        var failed = on && errors(copy!) > 0;
        var source = module.Slot == ModuleSlot.Project ? PassSource.Override : PassSource.Vanilla;
        return new PassEntry(module.Id, Name(module.Display, module.Programs.FirstOrDefault(), module.Id), module.Stage,
            module.Order, source, copy is not null, on, failed, module.Drawn ? module.CpuMs : null, [],
            on ? copy : game, game, candidates, null, module.Slot != ModuleSlot.Off);
    }

    // The project's first copy of a stage file of the programs (fragment first)
    private static string? CopyOf(string project, string[] programs)
    {
        foreach (var program in programs.Bounded(32))
        {
            foreach (var stage in ShaderModules.StageFiles(program).Bounded(3))
            {
                if (ShaderProjects.CopyPath(project, stage) is { } path && File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    // A pack pass: on while its switch is; it declares the settings whose define or uniform its fragment reads
    private PassEntry Packed(ShaderPacks.PassSpec spec, ShaderPacks.Pack pack, string folder,
        System.Func<string, double> value,
        int order)
    {
        var file = spec.Fragment.Length > 0 ? Path.Join(folder, spec.Fragment) : null;
        var text = file is null ? "" : _texts.GetValueOrDefault(file) ?? "";
        if (file is not null && !_texts.ContainsKey(file))
        {
            text = StudioLink.Read(file, out var read, out _) ? read : "";
            _texts[file] = text;
        }

        List<string> declared = [];
        foreach (var setting in pack.Settings.Bounded(64))
        {
            var name = setting.Define ?? setting.Uniform;
            if (setting.Key != spec.When && name is { Length: > 0 } && text.Contains(name, StringComparison.Ordinal))
            {
                declared.Add(setting.Key);
                _ = Owned.Add(setting.Key);
            }
        }

        var on = spec.When is not { } when || value(when) >= 0.5;
        var name0 = spec.Id.Length == 0 ? "" : char.ToUpperInvariant(spec.Id[0]) + spec.Id[1..].Replace('-', ' ');
        return new PassEntry("Pack:" + spec.Id, Name(name0, Path.GetFileNameWithoutExtension(spec.Fragment), spec.Id),
            PackStage, order, PassSource.Pack, false, false, false, null, [.. declared], file, null, [], spec.When, on);
    }

    // Never empty: the shown name, else the file's name, else the id
    public static string Name(string? display, string? file, string id)
    {
        if (!string.IsNullOrWhiteSpace(display)) return display;

        return !string.IsNullOrWhiteSpace(file) ? file : id;
    }

    // The game shader of a module's first program (fragment, else vertex)
    private static string? GameFile(ShaderModule module)
    {
        if (module.Programs.Length == 0) return null;

        foreach (var stage in ShaderModules.StageFiles(module.Programs[0]).Bounded(3))
        {
            if (ShaderAssets.InstalledPath(stage) is { } path && File.Exists(path)) return path;
        }

        return null;
    }
}
