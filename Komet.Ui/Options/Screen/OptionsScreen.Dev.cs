using Komet.Core;
using Komet.Hud;
using Komet.Ui.Hud;
using static Komet.Options.OptionsText;

namespace Komet.Options;

// The developer screen: the same chrome, its section a flat list of tools (no sub-tabs). A tool page takes the list
// and detail columns; it draws with the HUD's widgets, its hit boxes join the screen's click map.
internal sealed partial class OptionsScreen
{
    private readonly HudUiFonts _toolFonts = new();
    private readonly List<HudUi.Hit> _toolHits = [];
    private bool _developer;
    private IDevTool? _shownTool;
    private long _toolAt;
    private double _toolContent;

    // The tools in list order, asked as the developer screen opens
    public Func<IReadOnlyList<IDevTool>>? Tools { get; set; }

    public bool Developer => _developer && IsOpened();

    // Per frame, so a loop, not a capturing Find
    private IDevTool? Tool
    {
        get
        {
            if (Filtering) return null;

            foreach (var page in _pages.Bounded(MaxPages))
            {
                if (page.Id == _page) return page.Tool as IDevTool;
            }

            return null;
        }
    }

    public bool OpenDeveloper(string? tool = null)
    {
        if (IsOpened() && _embedded is not null) Leave();

        _developer = true;
        Restyle();
        Rebuild(tool is null ? _page : "dev-" + tool);
        return IsOpened() || TryOpen();
    }

    private OptionPage[] DevPages()
    {
        var tools = Tools?.Invoke() ?? [];
        var section = T("section-dev");
        return [.. tools.Take(MaxPages).Select(t => new OptionPage("dev-" + t.Id, t.Title, section) { Tool = t })];
    }

    private void ToolView(IDevTool tool, double x, double top, double w, double bottom, double pad)
    {
        var view = bottom - top;
        if (!Assert(view > 0) || !Assert(w > 2 * pad)) return;

        _toolFonts.Update(_settings.FontScale);
        if (!tool.Scrolls) _scroll = 0;

        _scroll = Math.Clamp(Finite(_scroll) ? _scroll : 0, 0, Math.Max(0, _toolContent - view));
        _map.View = (x, top, w, view);
        _canvas.Clip(x, top, w, view);
        _toolHits.Clear();
        var ui = new HudUi(_canvas, _toolFonts, (x + pad, w - 2 * pad, top, bottom, _scroll), _toolHits);
        try
        {
            tool.Skin(Controls);
            tool.Draw(ui);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            capi.Logger?.Warning("Komet developer tool '{0}' failed to draw: {1}", tool.Id, e);
        }

        _canvas.Unclip();
        _toolContent = ui.Y + S(Spacer);
        _toolAt = Environment.TickCount64;
        foreach (var hit in _toolHits.Bounded(HudUi.MaxHits))
        {
            var click = hit.Click;
            if (_map.Hits.Count < MaxHits)
            {
                _map.Hits.Add(new Hit(hit.X, hit.Y, hit.W, hit.H, (_, _) => click(), Glow: SoftGlow));
            }
        }
    }

    // Per frame: a live tool recomposes on its interval; the shown tool learns when it comes and goes
    private void ToolFrame()
    {
        var now = IsOpened() ? Tool : null;
        if (!ReferenceEquals(now, _shownTool))
        {
            _shownTool?.Shown(false);
            _shownTool = now;
            now?.Shown(true);
            _toolContent = 0;
        }

        if (now?.Poll() == true) _dirty = true;

        if (now is { RefreshMs: > 0 } live && Environment.TickCount64 - _toolAt >= live.RefreshMs) _dirty = true;
    }
}
