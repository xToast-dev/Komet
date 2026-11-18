using System.Globalization;
using Komet.Core;
using Komet.Options;
using Komet.Rendering;

namespace Komet.Sdk;

// The right column as Visual Studio's properties, sections that fold: PREVIEW (hold to see the game, what the game
// shows: the project or the original), SETTINGS (the rows of the options page Komet builds from the active project's
// komet-shader.json that the pack itself or a pass that is on declares, set as the menu sets them), GAME (the game's cloud
// mode, folded by default), FRAME (the total and the stacked ms bar of the passes: hover names one, a click shows it in
// the Passes tab). Rows are the options menu's: the same painter, the same row height, the lit row in the style's
// selection. The passes come from one PassModel, rebuilt with every module poll.
internal sealed partial class StudioSide(ShaderIde host) : IIdeRegion
{
    public const int MaxModules = 128;
    private const double RowHeight = 38, HeadHeight = 36, GroupHeight = 30;

    private ShaderModule[] _modules = [];
    private ShaderPacks.Pack? _pack;
    private string? _packOf;
    private long _packAt, _modulesAt;
    private int _packDisk = -1;
    private double _x, _w, _top, _bottom, _scroll, _content;
    private string _total = "";
    private OptionPage? _page;

    public ShaderModule[] Modules => _modules;

    public PassModel Model { get; } = new();

    private readonly Dictionary<string, string> _keyOf = new(StringComparer.Ordinal); // row label -> setting key

    private MenuControls? Menu => host.Controls;
    private static double S(double size) => MenuControls.S(size);

    // The manifest, its page and the modules again
    public void Refresh() => (_packAt, _modulesAt, _packDisk, _modulesSign) = (0, 0, -1, -1);

    // A key or a character for the value search
    public bool Key(GlKeys key, bool ctrl) => ValuesKey(key, ctrl);

    public void Type(char c) => ValuesType(c);


    public bool Poll(long now)
    {
        var redraw = false;
        if (now - _modulesAt > 500)
        {
            StudioLink.Listening(host is { IsShown: true, Peeking: false });
            _modulesAt = now;
            _modules = host.Link.Modules();
            // a full-screen recompose (and its texture upload) only when what the rows show moved: the poll used to
            // redraw the whole workbench twice a second
            // what the rows are redraws at once; their ms (to a tenth) at most every TimesMs: a full recompose is a
            // Cairo frame and a texture upload, and the ms change on nearly every poll
            var (sign, times) = (Sign(_modules), Times(_modules));
            if (sign != _modulesSign || (times != _timesSign && now - _timesAt >= TimesMs))
            {
                (_modulesSign, _timesSign, _timesAt) = (sign, times, now);
                RebuildModel();
                _total = TotalMs();
                redraw = true;
            }
        }

        if (now - _packAt > 2000 || host.Project != _packOf)
        {
            _packAt = now;
            var (pack, page) = (_pack, _page);
            LoadPack();
            if (!ReferenceEquals(pack, _pack) || !ReferenceEquals(page, _page))
            {
                RebuildModel();
                redraw = true;
            }
        }

        return redraw;
    }

    private long _modulesSign = -1;

    // What the module rows are: names, state, slot and order
    private static long Sign(ShaderModule[] modules)
    {
        var sign = (long)modules.Length;
        foreach (var m in modules.Bounded(PassModel.MaxPasses))
        {
            sign = sign * 31 + m.Id.GetHashCode(StringComparison.Ordinal);
            sign = sign * 31 + (m.Drawn ? 1 : 0) + (int)m.Why * 2 + (int)m.Slot * 64 + m.Order * 4096;
            sign = sign * 31 + (m.Project?.GetHashCode(StringComparison.Ordinal) ?? 0);
        }

        return sign;
    }

    private const long TimesMs = 2000;
    private long _timesSign = -1, _timesAt;

    // The rows' ms as drawn to a tenth
    private static long Times(ShaderModule[] modules)
    {
        var sign = 0L;
        foreach (var m in modules.Bounded(PassModel.MaxPasses))
        {
            sign = sign * 31 + (long)Math.Round((m.CpuMs ?? -1) * 10) + (long)Math.Round((m.DeltaMs ?? -1) * 10) * 7;
        }

        return sign;
    }

    private string TotalMs()
    {
        var total = 0.0;
        foreach (var pass in Model.Passes.Bounded(PassModel.MaxPasses))
        {
            total += pass.Ms ?? 0;
        }

        return total.ToString("0.0", CultureInfo.InvariantCulture) + " ms";
    }

    public void RebuildModel()
    {
        var folder = host.Project is { } project ? Path.Join(StudioLink.ProjectsFolder, project) : null;
        Model.Rebuild(_modules, _page is null ? null : _pack, folder, ValueOf, host.Panel.ErrorsIn);
    }

    // A pack setting's current value through its row on the pack's page (0 without one)
    private double ValueOf(string key) => RowOf(key)?.Get() ?? 0;

    public OptionRow? RowOf(string key)
    {
        if (_page is not { } page) return null;

        for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
        {
            if (page[i].Kind != OptionKind.Group && _keyOf.GetValueOrDefault(page[i].Label) == key) return page[i];
        }

        return null;
    }

    public ShaderPacks.Pack? Pack => _page is null ? null : _pack;

    // "Game runs: Vanilla" is the project switched off: read from it, so it holds across closing and restarts
    private bool RunsVanilla => host.Project is { } project && host.ProjectInfo(project) is { Active: false };

    public bool ShowsOriginal => RunsVanilla;

    private bool Open(string section) => host.Prefs.Sections.Contains(section);

    private void Fold(string section) => _ = host.Prefs.Sections.Remove(section) || AddSection(section);

    private bool AddSection(string section)
    {
        host.Prefs.Sections.Add(section);
        return true;
    }

    // komet-shader.json of the project and the options page Komet made of it (only an active project's pack loads)
    private void LoadPack()
    {
        // the manifest is read and parsed again only after the disk moved (or a Refresh); the page is looked up every
        // time, it comes and goes with the pack being active
        var parsed = host.Project == _packOf && host.DiskVersion == _packDisk;
        (_packOf, _page, _packDisk) = (host.Project, null, host.DiskVersion);
        if (!parsed) ParsePack();

        if (_pack?.Id is not { } id) return;

        for (var i = 0; i < Math.Min(OptionPages.Count, OptionPages.MaxPages); i++)
        {
            if (OptionPages.At(i).Id == "shader-" + id) _page = OptionPages.At(i);
        }
    }

    private void ParsePack()
    {
        _pack = null;
        _keyOf.Clear();
        if (host.Project is not { } project) return;

        var folder = Path.Join(StudioLink.ProjectsFolder, project);
        if (StudioLink.Read(Path.Join(folder, "komet-shader.json"), out var json, out _))
        {
            _pack = ShaderPacks.Parsed(null, json, project.ToLowerInvariant(), folder);
        }

        foreach (var setting in (_pack?.Settings ?? []).Bounded(64))
        {
            _keyOf[ShaderPacks.Said(setting.Label ?? setting.Key)] = setting.Key;
        }
    }

    // Vorschau zeigt Original: the project is switched off while it says so
    private void ShowOriginal(bool original)
    {
        if (RunsVanilla == original || host.Project is not { } project) return;

        host.SetProjectOn(project, !original, false);
    }

    public bool Wheel(double x, int lines)
    {
        _scroll = Math.Clamp(_scroll + lines * S(RowHeight) / 3, 0, Math.Max(0, _content - (_bottom - _top)));
        return true;
    }
}
