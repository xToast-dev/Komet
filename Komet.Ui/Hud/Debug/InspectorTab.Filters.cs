using Komet.Rendering;

namespace Komet.Hud;

// The inspector's search and filters
internal sealed partial class InspectorTab
{
    private readonly RenderFilter _filter = new();
    private HudBody? _body;
    private bool _renderSearching;

    private void Top()
    {
        if (_body is { } body) body.Scroll = 0;
    }

    private void RenderToolbar(HudUi ui)
    {
        ui.Line(R("search", (_renderSearching ? "▸ " : "") + _filter.Query), HudUi.Link, ui.F.Small,
            () => _renderSearching = !_renderSearching);
        ui.Line(R("filter-mod", _filter.Mod.Length == 0 ? R("all") : _filter.Mod), HudUi.Link, ui.F.Small, NextMod);
        ui.Line(R("filter-stage", _filter.Stage.Length == 0 ? R("all") : _filter.Stage), HudUi.Link, ui.F.Small,
            NextStage);
        _ = ui.Chips(ui.X, ui.Y, [
            (R("errors"), _filter.ErrorsOnly, () =>
            {
                _filter.ErrorsOnly = !_filter.ErrorsOnly;
                Top();
            }),
            (R("clear-filter"), false, () =>
            {
                _filter.Clear();
                Top();
                _renderSearching = false;
            })
        ]);
        ui.Y += ui.ChipHeight + ui.Px(4);
        _ = ui.Chips(ui.X, ui.Y, [
            (R("save-reference"), false, RenderDebug.SaveReference),
            (R(RenderDebug.ShowingReference ? "show-current" : "show-reference"), RenderDebug.ShowingReference,
                RenderDebug.Compare)
        ]);
        ui.Y += ui.ChipHeight + ui.Px(4);
        ui.Line(R("override-summary", RenderDebug.OverrideCount,
                R(RenderDebug.ShowingReference ? "reference-active" : "current-active"),
                R(RenderDebug.HasReference ? "reference-saved" : "reference-missing")),
            RenderDebug.Active ? HudUi.Warn : HudUi.Dim, ui.F.Small);
    }

    private bool Matches(string mod, string stage, bool error, string text)
    {
        if (!_filter.Matches(mod, stage, error, text)) return false;

        _renderMatches++;
        return true;
    }

    private void NextMod()
    {
        var mods = ModShaders.Mods().Concat(ShaderMods.All(_capi).Select(m => m.Id))
            .Concat(_programs.SelectMany(p => p.Owners)).Concat(_renderers.SelectMany(r => r.Mods))
            .Append("game").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Take(4096).ToArray();
        var next = Array.IndexOf(mods, _filter.Mod) + 1;
        _filter.Mod = next >= mods.Length ? "" : mods[next];
        Top();
    }

    private void NextStage()
    {
        var stages = Enum.GetNames<EnumRenderStage>().Prepend("Terrain").ToArray();
        var next = Array.IndexOf(stages, _filter.Stage) + 1;
        _filter.Stage = next >= stages.Length ? "" : stages[next];
        Top();
    }

    // Typing into the inspector's search; true: taken
    public bool Char(char c, HudBody body)
    {
        _body = body;
        if (!_renderSearching || char.IsControl(c) || _filter.Query.Length >= 128) return false;

        _filter.Query += c;
        Top();
        return true;
    }

    public bool Key(KeyEvent args, HudBody body)
    {
        _body = body;
        if (!_renderSearching || !NotNull(args)) return false;

        if (args.KeyCode == (int)GlKeys.BackSpace && _filter.Query.Length > 0)
        {
            _filter.Query = _filter.Query[..^1];
        }
        else if (args.KeyCode is (int)GlKeys.Enter or (int)GlKeys.KeypadEnter or (int)GlKeys.Escape)
        {
            _renderSearching = false;
        }
        else
        {
            return false;
        }

        Top();
        return true;
    }
}
