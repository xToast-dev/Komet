using System.Globalization;
using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The centre: tabs, the bar (breadcrumb from Vintagestory, the module's cost, Meins · Original · Vergleich, history,
// caret), the notices, the code or the comparison with the find widget over it, a read-only file's footer, and the
// bottom panel under it all
internal sealed partial class ShaderIde
{
    private const double TabsHeight = 34, BarHeight = 36, StripHeight = 28, FootHeight = 34;

    private readonly List<(double X, double W)> _tabXs = [];
    private (string Old, string New)? _change;
    private bool _historyOpen;
    private double _historyX, _historyY;

    private Action? _modeMine, _modeOriginal, _modeDiff, _modeEngine;
    private long _noticeAt;

    // A notice per state: the game changed the original, another project's copy draws, the module is not drawn
    private string? _noticeOf;
    private string _position = "1:1";
    private int _positionAt = -1;
    private TabStrip? _tabStrip;
    private string? _winner;

    // The bottom panel's height this frame (0 hidden): the enlarged preview leaves it free, the Inspector shows there
    private double PanelHeight { get; set; }

    private void EditorArea(HudUi ui, double x, double y, double w, double h)
    {
        var panelH = Panel.Shown ? Math.Clamp(ui.Px(Prefs.Panel), ui.Px(90), h * 0.6) : 0;
        PanelHeight = panelH;
        var bottom = y + h - panelH;
        TabStripDraw(ui, x, y, w);
        y += ui.Px(TabsHeight);
        if (Current is not { } document)
        {
            Start(ui, x, y, w, bottom);
        }
        else
        {
            y = Bar(ui, x, y, w, document);
            y = Notices(ui, x, y, w, document);
            var foot = document.ReadOnly && !StudioLink.IsGameShader(document.Path) ? ui.Px(FootHeight) : 0;
            Body(ui, x, y, w, bottom - foot);
            if (foot > 0) Footer(ui, x, bottom - foot, w, foot, document);
        }

        if (panelH > 0) Panel.Draw(ui, x, bottom, w, panelH);
    }

    // The tabs: the open one on the ground with the accent under it, a dot for unsaved, × on the hovered and open one
    private void TabStripDraw(HudUi ui, double x, double y, double w)
    {
        var h = ui.Px(TabsHeight);
        ui.Fill(x, y, w, h, BandGround);
        ui.Fill(x, y + h - 1, w, 1, HudUi.Divider);
        _tabXs.Clear();
        var at = x;
        for (var i = 0; i < Math.Min(_tabs.Count, MaxTabs + 1); i++)
        {
            var tab = _tabs[i];
            var on = tab == Current;
            var name = ui.Fit(ui.F.Body, tab.Name,
                ui.Px(200)); // long names end in "…"; marks and the dot keep their room
            var mark = tab.ReadOnly || OverrideState(tab.Path).Override;
            var nameW = ui.TextW(ui.F.Body, name);
            var tw = ui.Px(12) + nameW + ui.Px(8) + (mark ? ui.Px(16) : 0) + ui.Px(12) + ui.Px(26);
            if (at + tw > x + w) break;

            if (on)
            {
                ui.Fill(at, y, tw, h, Ground);
                ui.Fill(at, y + h - ui.Px(2), tw, ui.Px(2), HudUi.Accent);
            }

            ui.Fill(at + tw - 1, y, 1, h, HudUi.Divider);
            ui.Text(at + ui.Px(12), y, h, ui.F.Body, name, TabInk(on, tab.ReadOnly));
            Tip(at, y, tw - ui.Px(26), h, tab.Name);
            var after = at + ui.Px(12) + nameW + ui.Px(8);
            if (mark)
            {
                TabMark(ui, after, y, h, tab);
                after += ui.Px(16);
            }

            if (tab.Dirty)
            {
                ui.Disc(after + ui.Px(4), y + h / 2, ui.Px(3), HudUi.Link);
                Tip(after - ui.Px(2), y, ui.Px(12), h, L("tip-unsaved"));
            }

            if (on || Hot(at, y, tw, h)) StudioIcons.Close(ui, at + tw - ui.Px(14), y + h / 2, ui.Px(7), HudUi.Soft);

            _tabXs.Add((at, tw));
            at += tw;
        }

        if (TabDrop >= 0 && _tabXs.Count > 0)
        {
            var slot = Math.Min(TabDrop, _tabXs.Count);
            var mx = slot < _tabXs.Count ? _tabXs[slot].X : _tabXs[^1].X + _tabXs[^1].W;
            ui.Fill(mx - ui.Px(1), y, ui.Px(2), h, HudUi.Accent);
        }

        _tabStrip ??= new TabStrip(this);
        Hits.Region(x, y, w, h, _tabStrip);
    }

    // Lock: vanilla, read-only; ring: an override (filled while it runs, hollow while switched off)
    private void TabMark(HudUi ui, double x, double y, double h, ShaderDocument tab)
    {
        if (tab.ReadOnly)
        {
            StudioIcons.Lock(ui, x, y + h / 2, ui.Px(9), HudUi.Faint);
            Tip(x - ui.Px(4), y, ui.Px(16), h, L("tip-tab-readonly"));
            return;
        }

        var (copy, on) = OverrideState(tab.Path);
        if (!copy) return;

        if (on)
        {
            ui.Disc(x + ui.Px(4), y + h / 2, ui.Px(3.5), HudUi.Link);
        }
        else
        {
            ui.Ring(x + ui.Px(4), y + h / 2, ui.Px(3.5), HudUi.Faint, Math.Max(1, ui.Px(1)));
        }

        Tip(x - ui.Px(4), y, ui.Px(16), h, L(on ? "tip-tab-override-on" : "tip-tab-override-off"));
    }

    // Vintagestory › … › file, the module's cost, the view switch, the history clock, line:column
    private double Bar(HudUi ui, double x, double y, double w, ShaderDocument document)
    {
        var h = ui.Px(BarHeight);
        ui.Fill(x, y + h - 1, w, 1, HudUi.Divider);
        var right = x + w - ui.Px(18);
        right = Position(ui, right, y, h, document);
        if (!document.ReadOnly)
        {
            right -= ui.Px(26);
            ui.Fill(right + ui.Px(30), y + h / 2 - ui.Px(8), Math.Max(1, ui.Px(1)), ui.Px(16), HudUi.Divider);
            StudioIcons.Clock(ui, right + ui.Px(5), y + h / 2, ui.Px(15), _historyOpen ? HudUi.Link : HudUi.Faint);
            Hits.Add(right, y + ui.Px(5), ui.Px(26), h - ui.Px(10), ToggleHistory);
            Tip(right, y + ui.Px(5), ui.Px(26), h - ui.Px(10), L("tip-history"));
            (_historyX, _historyY) = (right + ui.Px(26), y + h);
            right -= ui.Px(14);
        }

        if (StudioLink.GameFileOf(document.Path) is not null)
        {
            right = Segment(ui, right, y, h) - ui.Px(14);
        }
        else if (StudioLink.IsGameShader(document.Path))
        {
            right = VanillaHead(ui, right, y, h, document) - ui.Px(14);
        }

        right = ModuleInfo(ui, right, y, h, document) - ui.Px(14);
        right = GameShows(ui, right, y, h, document);
        Crumbs(ui, x + ui.Px(18), y, h, right - x - ui.Px(18), document);
        return y + h;
    }

    private double Position(HudUi ui, double right, double y, double h, ShaderDocument document)
    {
        var shown = Active ?? document;
        if (shown.Caret != _positionAt)
        {
            var line = shown.LineAt(shown.Caret);
            _position = L("ln") + " " + (line + 1).ToString(CultureInfo.InvariantCulture) + ", " + L("col") + " " +
                        (shown.Caret - shown.LineOffset(line) + 1).ToString(CultureInfo.InvariantCulture);
            _positionAt = shown.Caret;
        }

        var w = ui.TextW(ui.F.Small, _position);
        ui.Text(right - w, y, h, ui.F.Small, _position, HudUi.Faint);
        return right - w - ui.Px(18); // room between the position and the history clock
    }

    // MEINS · ORIGINAL · VERGLEICH, the shown one lit in the selection's colour
    private double Segment(HudUi ui, double right, double y, double h)
    {
        ReadOnlySpan<(string Key, EditorMode Mode)> parts =
        [
            ("mine", EditorMode.Mine), ("original", EditorMode.Original), ("compare-short", EditorMode.Diff),
            ("engine", EditorMode.Engine)
        ];
        ReadOnlySpan<string> tips = ["tip-mine", "tip-original", "tip-compare", "tip-engine"];
        var widths = 0.0;
        foreach (var (key, _) in parts.Bounded(4))
        {
            widths += ui.TextW(ui.F.Small, Caps(L(key))) + ui.Px(2) * L(key).Length + ui.Px(24);
        }

        var (x, top, bh) = (right - widths, y + (h - ui.Px(24)) / 2, ui.Px(24));
        TextField.Frame(ui, x, top, widths, bh, HudUi.Divider);
        foreach (var (key, mode) in parts.Bounded(4))
        {
            var caps = Caps(L(key));
            var sw = ui.TextW(ui.F.Small, caps) + ui.Px(2) * caps.Length + ui.Px(24);
            var on = Mode == mode;
            if (on) ui.Fill(x, top, sw, bh, Controls?.Style["select"] ?? HudUi.SelectionBack);

            _ = ui.Spaced(x + ui.Px(12), top, bh, ui.F.Small, caps, ui.Px(2), on ? Rgba.White(1) : HudUi.Faint);
            Tip(x, top, sw, bh, L(tips[(int)mode]));
            Hits.Add(x, top, sw, bh, mode switch
            {
                EditorMode.Mine => _modeMine!,
                EditorMode.Original => _modeOriginal!,
                EditorMode.Engine => _modeEngine!,
                _ => _modeDiff!
            });
            x += sw;
        }

        return right - widths;
    }

    // The open override's state (the TEST chip's word) and, while drawn, its pass's time
    private double ModuleInfo(HudUi ui, double right, double y, double h, ShaderDocument document)
    {
        (_modeMine, _modeOriginal, _modeDiff) = (_modeMine ?? (() => SetMode(EditorMode.Mine)),
            _modeOriginal ?? (() => SetMode(EditorMode.Original)), _modeDiff ?? (() => SetMode(EditorMode.Diff)));
        _modeEngine ??= () => SetMode(Mode == EditorMode.Engine ? EditorMode.Mine : EditorMode.Engine);
        if (StatusOf(document) is not { } status) return right;

        var ms = status.State == OverrideRun.Active && ModuleOf(document) is { CpuMs: { } cpu }
            ? "  " + Ms(cpu) + " ms"
            : "";
        var text = StateWord(status.State) + ms;
        var tw = ui.TextW(ui.F.Small, text);
        ui.Text(right - tw, y, h, ui.F.Small, text, StateInk(status.State));
        Tip(right - tw, y, tw, h, StateWord(status.State) + ": " + status.Reason);
        return right - tw;
    }

    private static Rgba TabInk(bool on, bool readOnly)
    {
        if (on) return Rgba.White(1);

        return readOnly ? HudUi.Faint : HudUi.Soft;
    }

    private static string Ms(double? ms) => ms is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    private static string Why(ModuleWhy why) =>
        why switch
        {
            ModuleWhy.CloudMode => L("why-cloud-mode"),
            ModuleWhy.Off => L("why-off"),
            ModuleWhy.PostSetting => L("why-post-setting"),
            ModuleWhy.Unattributed => L("why-unattributed"),
            _ => L("why-not-drawn")
        };

    private static void Crumbs(HudUi ui, double x, double y, double h, double w, ShaderDocument document)
    {
        var display = StudioLink.Display(document.Path);
        var text = L("root-name") + " › " + display.Replace("/", " › ", StringComparison.Ordinal);
        text = ui.Fit(ui.F.Small, text, Math.Max(ui.Px(40), w));
        ui.Text(x, y, h, ui.F.Small, text, HudUi.Faint);
    }

    // The game update and the overlay read files: asked again for another file, after a build or every few seconds
    private void NoticesStale() => _noticeOf = null;

    private void NoticeState(ShaderDocument document)
    {
        var now = Environment.TickCount64;
        if (_noticeOf == document.Path && now - _noticeAt < SlowMs) return;

        (_noticeOf, _noticeAt) = (document.Path, now);
        var copy = StudioLink.GameFileOf(document.Path) is not null;
        (_change, _winner) = copy
            ? (StudioLink.OriginalChanged(document.Path), StudioLink.OverlaidBy(document.Path))
            : (null, null);
    }

    private double Notices(HudUi ui, double x, double y, double w, ShaderDocument document)
    {
        NoticeState(document);
        if (_change is { } change)
        {
            y = Strip(ui, x, y, w, L("original-changed"), StudioColors.Gold,
                (L("differences"), () => Diff.Show(L("original-then"), change.Old, L("original-now"), change.New,
                    [(L("mark-seen"), () => Seen(document))])), (L("mark-seen"), () => Seen(document)));
        }

        if (_winner is { } winner)
        {
            y = Strip(ui, x, y, w, L("overlaid-by", winner),
                StudioColors.Gold, (L("open-project", winner),
                    () => OpenWinner(document)), default);
        }

        if (StatusOf(document) is { State: not OverrideRun.Active } status)
        {
            y = Strip(ui, x, y, w, StateWord(status.State) + ": " + status.Reason, StateInk(status.State), default,
                default);
        }

        if (Mode == EditorMode.Original && StudioLink.GameFileOf(document.Path) is not null)
        {
            y = Strip(ui, x, y, w, L("vanilla-readonly-strip"), StudioColors.Gold, (L("edit-my-version"),
                _modeMine), default);
        }

        return y;
    }

    private double Strip(HudUi ui, double x, double y, double w, string text, Rgba color, (string, Action?) a,
        (string, Action?) b)
    {
        var h = ui.Px(StripHeight);
        ui.Fill(x, y, w, h, color with { A = 0.08 });
        ui.Fill(x, y + h - 1, w, 1, HudUi.Divider);
        ui.Text(x + ui.Px(18), y, h, ui.F.Small, ui.Fit(ui.F.Small, text, w * 0.6), color);
        var right = x + w - ui.Px(18);
        foreach (var (label, run) in (ReadOnlySpan<(string, Action?)>)[b, a])
        {
            if (run is null || label is null) continue;

            var caps = Caps(label);
            var lw = ui.TextW(ui.F.Small, caps) + ui.Px(1.5) * caps.Length;
            _ = ui.Spaced(right - lw, y, h, ui.F.Small, caps, ui.Px(1.5), HudUi.Link);
            Hits.Add(right - lw - ui.Px(4), y, lw + ui.Px(8), h, run);
            right -= lw + ui.Px(14);
        }

        return y + h;
    }

    private void Seen(ShaderDocument document)
    {
        StudioLink.MarkSeen(document.Path);
        NoticesStale();
        Relist();
        Diff.Close();
        AddLog(document.Name + ": " + L("seen-update"));
        Toast(L("marked-seen"));
    }

    private void OpenWinner(ShaderDocument document)
    {
        if (StudioLink.GameFileOf(document.Path) is { } game && StudioLink.Winner(game) is { } copy) Open(copy);
    }
}
