using System.Globalization;
using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The Passes tab: the frame's passes from the PassModel over the panel's whole width, grouped by stage. Vanilla is the
// normal state, so a stage shows only its passes with an override and the pack's own passes; a fold row unfolds the
// unchanged vanilla passes, and a stage without any override is one folded row. A row holds the drag handle for the
// order within its stage, the name that opens its shader on a click (the game's files read-only), the override switch
// or "Create override", a pick where several active projects copy it, and the time: a dash below a hundredth of a
// millisecond, the three dearest passes marked. Switched off, the game's vanilla shader runs and the copy stays. The
// head holds the active overrides, the search over all names, "Disable all overrides" and "Reset to defaults".
internal sealed partial class StudioPanel
{
    private const int Passes = 3;
    private const int MaxPassRows = 512;
    private const double TinyMs = 0.01;

    private readonly TextField _passQuery = new();
    private readonly HashSet<string> _unfolded = new(StringComparer.Ordinal);
    private readonly List<PassRow> _passRows = [];
    private readonly Dictionary<string, DragRegion> _drags = new(StringComparer.Ordinal);
    private string? _marked, _dragId;
    private double _dragFrom, _dragTo, _passTop, _passRowH;
    private int _passFirst;
    private bool _scrollToMark;
    private Action? _focusPasses, _disableAll, _resetAll;

    // A drawn row: a stage head, a pass, or the "N vanilla passes unchanged" fold
    private readonly record struct PassRow(string Stage, PassEntry? Pass, int Vanilla, bool Head);

    public static string Ms(double? ms) =>
        ms is { } v and >= TinyMs ? v.ToString("0.00", CultureInfo.InvariantCulture) + " ms" : "–";

    // The FRAME bar's click: the tab, the pass marked, its stage unfolded when it is vanilla
    public void ShowPass(string id)
    {
        Show(Passes);
        _marked = id;
        if (host.Side.Model.Find(id) is { } pass) _ = _unfolded.Add(pass.Stage);

        _scrollToMark = true;
    }

    private void PassesTab(HudUi ui, double x, double top, double w, double bottom)
    {
        _focusPasses ??= () => host.Focus = StudioFocus.Search;
        _disableAll ??= DisableAll;
        _resetAll ??= ResetAll;
        top = PassesHead(ui, x, top, w);
        host.Hits.Region(x, top, w, bottom - top, this);
        PassRows();
        _passRowH = ui.Px(RowHeight + 4);
        _passTop = top;
        var shown = Math.Max(1, (int)((bottom - top) / _passRowH));
        if (_scrollToMark)
        {
            _scrollToMark = false;
            var at = _passRows.FindIndex(r => r.Pass?.Id == _marked);
            _passFirst = at < 0 ? _passFirst : Math.Max(0, at - shown / 2);
        }

        if (_passRows.Count == 0)
        {
            var q = _passQuery.Text.Trim();
            var empty = q.Length > 0 ? L("no-passes-match", q) : L("no-modules");
            ui.Text(x + ui.Px(16), top + ui.Px(6), ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, empty, w - ui.Px(32)), HudUi.Faint);
            return;
        }

        _passFirst = Math.Clamp(_passFirst, 0, Math.Max(0, _passRows.Count - shown));
        for (var i = 0; i < Math.Min(shown, MaxPassRows) && _passFirst + i < _passRows.Count; i++)
        {
            PassLine(ui, x, top + i * _passRowH, w, _passRows[_passFirst + i]);
        }
    }

    private double PassesHead(HudUi ui, double x, double y, double w)
    {
        var model = host.Side.Model;
        if (docked) return PassesHeadNarrow(ui, x, y, w, model);

        var fw = Math.Min(ui.Px(300), w * 0.3);
        var h = _passQuery.Draw(ui, x + ui.Px(16), y + ui.Px(4), fw, L("search-passes"), host.Focus == StudioFocus.Search);
        host.Hits.Add(x + ui.Px(16), y + ui.Px(4), fw, h, _focusPasses!);
        var at = x + ui.Px(30) + fw;
        var count = L("overrides-active", model.OverridesActive);
        ui.Text(at, y + ui.Px(4), h, ui.F.Small, count, model.OverridesActive > 0 ? HudUi.Link : HudUi.Faint);
        at += ui.TextW(ui.F.Small, count) + ui.Px(24);
        ui.Text(at, y + ui.Px(4), h, ui.F.Small, ui.Fit(ui.F.Small, L("order-hint"), Math.Max(0, w * 0.3)), HudUi.Faint);
        var right = x + w - ui.Px(16);
        right = HeadButton(ui, right, y + ui.Px(4), h, L("reset-defaults"), host.Project is not null, _resetAll!);
        _ = HeadButton(ui, right - ui.Px(16), y + ui.Px(4), h, L("disable-overrides"), model.OverridesActive > 0,
            _disableAll!);
        return y + h + ui.Px(10);
    }

    // The head in the left column: the search over the whole width, the count and the two buttons below it
    private double PassesHeadNarrow(HudUi ui, double x, double y, double w, PassModel model)
    {
        var fw = w - ui.Px(32);
        var h = _passQuery.Draw(ui, x + ui.Px(16), y + ui.Px(4), fw, L("search-passes"), host.Focus == StudioFocus.Search);
        host.Hits.Add(x + ui.Px(16), y + ui.Px(4), fw, h, _focusPasses!);
        y += h + ui.Px(8);
        var count = L("overrides-active", model.OverridesActive);
        ui.Text(x + ui.Px(16), y, h, ui.F.Small, count, model.OverridesActive > 0 ? HudUi.Link : HudUi.Faint);
        var right = HeadButton(ui, x + w - ui.Px(16), y, h, L("reset-defaults"), host.Project is not null, _resetAll!);
        _ = HeadButton(ui, right - ui.Px(8), y, h, L("disable-overrides"), model.OverridesActive > 0, _disableAll!);
        return y + h + ui.Px(10);
    }

    // A text button right-aligned at right; returns its left edge
    private double HeadButton(HudUi ui, double right, double y, double h, string text, bool enabled, Action click)
    {
        var tw = ui.TextW(ui.F.Small, text) + ui.Px(16);
        var left = right - tw;
        var hot = enabled && host.Hot(left, y, tw, h);
        ui.Fill(left, y, tw, h, HudUi.Track with { A = hot ? 0.12 : 0.05 });
        TextField.Frame(ui, left, y, tw, h, HudUi.Divider);
        ui.Text(left + ui.Px(8), y, h, ui.F.Small, text, enabled ? HudUi.Soft : HudUi.Faint);
        if (enabled) host.Hits.Add(left, y, tw, h, click);

        return left;
    }

    // The rows to draw: per stage its deviations, the vanilla fold; a search shows every match unfolded
    private void PassRows()
    {
        _passRows.Clear();
        var q = _passQuery.Text.Trim();
        var passes = host.Side.Model.Passes;
        var start = 0;
        for (var i = 0; i < Math.Min(passes.Length, PassModel.MaxPasses); i++)
        {
            if (i + 1 < passes.Length && passes[i + 1].Stage == passes[i].Stage) continue;

            StageRows(passes[start..(i + 1)], q);
            start = i + 1;
        }
    }

    private void StageRows(PassEntry[] stage, string q)
    {
        var id = stage[0].Stage;
        if (q.Length > 0)
        {
            var head = false;
            foreach (var pass in stage.Bounded(PassModel.MaxPasses))
            {
                if (pass.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                {
                    AddHead(ref head, id);
                    _passRows.Add(new PassRow(id, pass, 0, false));
                }
            }

            return;
        }

        var vanilla = 0;
        foreach (var pass in stage.Bounded(PassModel.MaxPasses))
        {
            vanilla += Deviates(pass) ? 0 : 1;
        }

        var open = _unfolded.Contains(id);
        if (vanilla == stage.Length && !open)
        {
            _passRows.Add(new PassRow(id, null, vanilla, true));
            return;
        }

        _passRows.Add(new PassRow(id, null, 0, true));
        foreach (var pass in stage.Bounded(PassModel.MaxPasses))
        {
            if (Deviates(pass) || open) _passRows.Add(new PassRow(id, pass, 0, false));
        }

        if (vanilla > 0) _passRows.Add(new PassRow(id, null, vanilla, false));
    }

    private void AddHead(ref bool added, string stage)
    {
        if (!added)
        {
            _passRows.Add(new PassRow(stage, null, 0, true));
            added = true;
        }
    }

    private static bool Deviates(PassEntry pass) => pass.HasOverride || pass.Source == PassSource.Pack;

    private void Fold(string stage) => _ = _unfolded.Remove(stage) || _unfolded.Add(stage);

    private void DisableAll()
    {
        var ids = host.Side.Model.Passes.Where(p => p is { HasOverride: true, OverrideOn: true }).Select(p => p.Id).ToArray();
        host.Modal.Confirm(L("disable-overrides"), L("disable-overrides-text", ids.Length), L("disable"), () =>
        {
            _ = host.Link.SetOverrides(ids, false);
            host.AddLog(L("disable-overrides") + ": " + ids.Length.ToString(CultureInfo.InvariantCulture));
            host.Side.Refresh();
        });
    }

    private void ResetAll()
    {
        if (host.Project is not { } project) return;

        host.Modal.Confirm(L("reset-defaults"), L("reset-defaults-text", project), L("reset"), () =>
        {
            host.Link.ResetDefaults(project, host.Side.Pack?.Id);
            host.AddLog(L("reset-defaults") + ": " + project);
            host.Side.Refresh();
        });
    }
}
