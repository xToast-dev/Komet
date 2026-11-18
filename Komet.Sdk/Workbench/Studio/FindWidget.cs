using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Find and replace in the shown file (Strg F, Strg H), over the code's top right: the query with "n von m", Aa, up,
// down, close; the replace row with Ersetzen and Alle (not for a read-only file). Matches light up in the code.
internal sealed class FindWidget(ShaderIde host)
{
    private const int MaxMatches = 4096;
    private readonly TextField _query = new(), _replace = new();
    private bool _case;
    private int _queryVersion = -1, _textVersion = -1;
    private ShaderDocument? _of;
    private Action? _toggle, _caseClick, _prev, _next, _close, _one, _all, _focusQuery, _focusReplace;

    public bool Open { get; private set; }
    private bool Replacing { get; set; }

    private bool ReadOnly => host.Active is not { ReadOnly: false } || host.Mode != EditorMode.Mine;

    public void Begin(bool replace)
    {
        if (host.Active is not { } document)
        {
            host.Toast(L("no-file"));
            return;
        }

        if (host.Mode == EditorMode.Diff) host.SetMode(EditorMode.Mine);

        (Open, Replacing) = (true, replace);
        if (document.HasSelection && !document.Selected.Contains('\n', StringComparison.Ordinal))
        {
            _query.Set(document.Selected);
        }

        _query.SelectAll();
        host.Focus = replace && _query.Text.Length > 0 && !ReadOnly ? StudioFocus.Replace : StudioFocus.Find;
        _queryVersion = -1;
        Refresh();
        Go(0, document.Caret);
    }

    public void Close()
    {
        Open = false;
        host.Focus = StudioFocus.Editor;
        host.Editor.MatchLength = 0;
        host.Editor.Matches.Clear();
    }

    // The matches again when the query, the case switch or the text changed
    public void Refresh()
    {
        var document = host.Active;
        if (!Open || document is null || _query.Text.Length == 0)
        {
            host.Editor.Matches.Clear();
            host.Editor.MatchLength = 0;
            return;
        }

        if (_of == document && _queryVersion == _query.Version && _textVersion == document.Version) return;

        (_of, _queryVersion, _textVersion) = (document, _query.Version, document.Version);
        var matches = host.Editor.Matches;
        matches.Clear();
        var comparison = _case ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var at = 0;
        for (var i = 0; i < MaxMatches; i++)
        {
            var found = document.Text.IndexOf(_query.Text, at, comparison);
            if (found < 0) break;

            matches.Add(found);
            at = found + Math.Max(1, _query.Text.Length);
        }

        host.Editor.MatchLength = _query.Text.Length;
        host.Editor.Match = Math.Clamp(host.Editor.Match, matches.Count > 0 ? 0 : -1, matches.Count - 1);
    }

    // To the next (+1), previous (-1) or the first at or after the caret (0)
    private void Go(int step, int from = -1)
    {
        Refresh();
        var matches = host.Editor.Matches;
        if (matches.Count == 0 || host.Active is not { } document) return;

        var index = step == 0 ? Math.Max(0, matches.FindIndex(m => m >= from)) : host.Editor.Match + step;
        index = ((index % matches.Count) + matches.Count) % matches.Count;
        host.Editor.Match = index;
        document.Select(matches[index], matches[index] + _query.Text.Length);
    }

    private void ReplaceOne()
    {
        var matches = host.Editor.Matches;
        if (ReadOnly || host.Active is not { } document || matches.Count == 0) return;

        var at = matches[Math.Clamp(host.Editor.Match, 0, matches.Count - 1)];
        _ = document.Replace(at, at + _query.Text.Length, _replace.Text);
        Refresh();
        Go(0, at + _replace.Text.Length);
    }

    private void ReplaceAll()
    {
        Refresh();
        var matches = host.Editor.Matches;
        if (ReadOnly || host.Active is not { } document || matches.Count == 0) return;

        var count = matches.Count;
        var text = document.Text.Replace(_query.Text, _replace.Text,
            _case ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        _ = document.Replace(0, document.Text.Length, text);
        Refresh();
        host.Toast(L("replaced", count));
    }

    public bool Key(GlKeys key, bool ctrl, bool shift)
    {
        var field = host.Focus == StudioFocus.Replace ? _replace : _query;
        switch (key)
        {
            case GlKeys.Enter or GlKeys.KeypadEnter when host.Focus == StudioFocus.Replace:
                if (ctrl)
                {
                    ReplaceAll();
                }
                else
                {
                    ReplaceOne();
                }

                return true;
            case GlKeys.Enter or GlKeys.KeypadEnter: Go(shift ? -1 : 1); return true;
            case GlKeys.Tab when Replacing && !ReadOnly:
                host.Focus = host.Focus == StudioFocus.Find ? StudioFocus.Replace : StudioFocus.Find;
                return true;
        }

        _ = field.Key(key, ctrl, host.Api);
        if (field == _query)
        {
            Refresh();
            Go(0, host.Active?.SelectionStart ?? 0);
        }

        return true;
    }

    public void Type(char c)
    {
        if (host.Focus == StudioFocus.Replace)
        {
            _replace.Type(c);
            return;
        }

        _query.Type(c);
        Refresh();
        Go(0, host.Active?.SelectionStart ?? 0);
    }

    public void Draw(HudUi ui, double x, double y, double w)
    {
        if (!Open || host.Active is null) return;

        Actions();
        var row = ui.Px(30);
        var h = row * (Replacing ? 2 : 1) + ui.Px(10);
        ui.Panel(x, y, w, h, HudUi.PopupBack with { A = 1 });
        TextField.Frame(ui, x, y, w, h, HudUi.Divider);
        host.Hits.Block(x, y, w, h);
        QueryRow(ui, x + ui.Px(5), y + ui.Px(5), w - ui.Px(10), row);
        if (Replacing) ReplaceRow(ui, x + ui.Px(5), y + ui.Px(5) + row, w - ui.Px(10), row);
    }

    private void Actions()
    {
        _toggle ??= () => Begin(!Replacing);
        _caseClick ??= () =>
        {
            _case = !_case;
            _queryVersion = -1;
            Refresh();
        };
        (_prev, _next, _close) = (_prev ?? (() => Go(-1)), _next ?? (() => Go(1)), _close ?? Close);
        (_one, _all) = (_one ?? ReplaceOne, _all ?? ReplaceAll);
        _focusQuery ??= () => host.Focus = StudioFocus.Find;
        _focusReplace ??= () => host.Focus = ReadOnly ? StudioFocus.Find : StudioFocus.Replace;
    }

    private void QueryRow(HudUi ui, double x, double y, double w, double h)
    {
        var b = ui.Px(26);
        Icon(ui, x, y, b, h, _toggle!, false);
        _ = ui.Caret(x + ui.Px(9), y, h, Replacing, HudUi.Faint);
        var count = Count();
        var countW = ui.Px(70);
        var fw = w - b - countW - 4 * b - ui.Px(8);
        _ = _query.Draw(ui, x + b, y + (h - ui.F.BodyRow - ui.Px(8)) / 2, fw, L("find-placeholder"),
            host.Focus == StudioFocus.Find);
        host.Hits.Add(x + b, y, fw, h, _focusQuery!);
        var at = x + b + fw + ui.Px(8);
        ui.Text(at, y, h, ui.F.Small, ui.Fit(ui.F.Small, count, countW - ui.Px(4)), HudUi.Faint);
        at += countW;
        Icon(ui, at, y, b, h, _caseClick!, _case);
        ui.Text(at + ui.Px(5), y, h, ui.F.Small, "Aa", _case ? HudUi.Link : HudUi.Faint);
        Icon(ui, at + b, y, b, h, _prev!, false);
        StudioIcons.Triangle(ui, at + b + b / 2, y + h / 2, ui.Px(8), true, HudUi.Faint);
        Icon(ui, at + 2 * b, y, b, h, _next!, false);
        StudioIcons.Triangle(ui, at + 2 * b + b / 2, y + h / 2, ui.Px(8), false, HudUi.Faint);
        Icon(ui, at + 3 * b, y, b, h, _close!, false);
        StudioIcons.Close(ui, at + 3 * b + b / 2, y + h / 2, ui.Px(8), HudUi.Faint);
    }

    private string _countText = "";
    private int _countAt = -2, _countOf = -2;

    private string Count()
    {
        var matches = host.Editor.Matches;
        if (_query.Text.Length == 0) return "";

        if (_countAt != host.Editor.Match || _countOf != matches.Count)
        {
            (_countAt, _countOf) = (host.Editor.Match, matches.Count);
            _countText = matches.Count == 0 ? L("no-hits")
                : L("n-of-m", host.Editor.Match + 1, matches.Count);
        }

        return _countText;
    }

    private void ReplaceRow(HudUi ui, double x, double y, double w, double h)
    {
        var b = ui.Px(26);
        var ro = ReadOnly;
        var one = Caps(L("replace"));
        var all = Caps(L("all"));
        var oneW = ui.TextW(ui.F.Small, one) + ui.Px(1.5) * one.Length + ui.Px(20);
        var allW = ui.TextW(ui.F.Small, all) + ui.Px(1.5) * all.Length + ui.Px(20);
        var fw = w - b - oneW - allW - ui.Px(16);
        _ = _replace.Draw(ui, x + b, y + (h - ui.F.BodyRow - ui.Px(8)) / 2, fw, L("replace-placeholder"),
            host.Focus == StudioFocus.Replace);
        host.Hits.Add(x + b, y, fw, h, _focusReplace!);
        var at = x + b + fw + ui.Px(8);
        Button(ui, at, y, oneW, h, one, ro ? null : _one);
        Button(ui, at + oneW + ui.Px(8), y, allW, h, all, ro ? null : _all);
    }

    private void Button(HudUi ui, double x, double y, double w, double h, string caps, Action? click)
    {
        var top = y + (h - ui.Px(24)) / 2;
        if (click is not null && host.Hot(x, top, w, ui.Px(24))) ui.Fill(x, top, w, ui.Px(24), HudUi.Track);

        TextField.Frame(ui, x, top, w, ui.Px(24), HudUi.Divider);
        _ = ui.Spaced(x + ui.Px(10), top, ui.Px(24), ui.F.Small, caps, ui.Px(1.5),
            click is null ? HudUi.Faint with { A = 0.3 } : HudUi.Soft);
        if (click is not null) host.Hits.Add(x, top, w, ui.Px(24), click);
    }

    private void Icon(HudUi ui, double x, double y, double w, double h, Action click, bool on)
    {
        if (on || host.Hot(x, y, w, h))
        {
            ui.Fill(x, y + ui.Px(2), w, h - ui.Px(4), on ? HudUi.SelectedBack : HudUi.Track with { A = 0.06 });
        }

        host.Hits.Add(x, y, w, h, click);
    }
}
