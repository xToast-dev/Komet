using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Two texts side by side in one scroller: equal lines paired, a changed run's removed and added lines paired row by row
// (red left, green right). Its header names both sides, counts the changed rows and carries the caller's actions.
internal sealed class SideDiff(ShaderIde host) : IIdeRegion
{
    private const int MaxLines = 1500;
    private readonly List<(int Left, int Right, bool Changed)> _rows = [];
    private (string Label, Action Run)[] _actions = [];
    private Action? _close;
    private int _first, _liveVersion = -1;
    private string[] _left = [], _right = [];
    private string _leftTitle = "", _rightTitle = "", _summary = "";
    private bool _live;

    public bool Open { get; private set; }

    public bool Wheel(double x, int lines)
    {
        _first = Math.Max(0, _first + lines);
        return true;
    }

    // live: the right side is the shown copy's draft and follows its edits (Vergleich)
    public void Show(string leftTitle, string left, string rightTitle, string right, (string, Action)[] actions,
        bool live = false)
    {
        (_leftTitle, _rightTitle, _actions, _live, _first, Open) = (leftTitle, rightTitle, actions, live, 0, true);
        _left = left.Split('\n');
        _right = right.Split('\n');
        _liveVersion = host.Current?.Version ?? -1;
        Compute();
        host.Redraw();
    }

    public void Close()
    {
        Open = false;
        _rows.Clear();
    }

    // Line LCS over at most MaxLines a side
    private void Compute()
    {
        _rows.Clear();
        int n = Math.Min(_left.Length, MaxLines), m = Math.Min(_right.Length, MaxLines);
        var table = new int[n + 1][];
        for (var r = 0; r <= Math.Min(n, MaxLines); r++)
        {
            table[r] = new int[m + 1];
        }

        for (var k = 0; k < Math.Min(n, MaxLines); k++)
        {
            var i = n - 1 - k;
            for (var q = 0; q < Math.Min(m, MaxLines); q++)
            {
                var j = m - 1 - q;
                table[i][j] = _left[i] == _right[j]
                    ? table[i + 1][j + 1] + 1
                    : Math.Max(table[i + 1][j], table[i][j + 1]);
            }
        }

        List<int> removed = [], added = [];
        var (a, b) = (0, 0);
        for (var step = 0; step < 2 * MaxLines + 2 && (a < n || b < m); step++)
        {
            if (a < n && b < m && _left[a] == _right[b])
            {
                Flush(removed, added);
                _rows.Add((a++, b++, false));
            }
            else if (b >= m || (a < n && table[a + 1][b] >= table[a][b + 1]))
            {
                removed.Add(a++);
            }
            else
            {
                added.Add(b++);
            }
        }

        Flush(removed, added);
        var changed = _rows.Count(r => r.Changed);
        _summary = changed switch
        {
            0 => L("same"),
            1 => L("one-line-differs", changed),
            _ => L("lines-differ", changed)
        };
    }

    private void Flush(List<int> removed, List<int> added)
    {
        for (var q = 0; q < Math.Min(Math.Max(removed.Count, added.Count), 2 * MaxLines); q++)
        {
            _rows.Add((q < removed.Count ? removed[q] : -1, q < added.Count ? added[q] : -1, true));
        }

        removed.Clear();
        added.Clear();
    }

    public void Draw(HudUi ui, double x, double y, double w, double h)
    {
        if (_live && host.Current is { } copy && copy.Version != _liveVersion)
        {
            _liveVersion = copy.Version;
            _right = copy.Text.Split('\n');
            Compute();
        }

        var head = ui.Px(32);
        var half = w / 2;
        ui.Fill(x, y, w, head, host.BandGround);
        ui.Fill(x, y + head - 1, w, 1, HudUi.Divider);
        ui.Fill(x + half, y, 1, h, HudUi.Divider);
        Title(ui, x + ui.Px(14), y, head, _leftTitle, L("read-only-lower"));
        Title(ui, x + half + ui.Px(14), y, head, _rightTitle, _summary);
        Links(ui, x + w - ui.Px(12), y, head);
        var row = ui.F.BodyRow;
        var shown = Math.Max(1, (int)((h - head) / row));
        _first = Math.Clamp(_first, 0, Math.Max(0, _rows.Count - shown));
        for (var i = 0; i < Math.Min(shown, 256) && _first + i < _rows.Count; i++)
        {
            var (l, r, changed) = _rows[_first + i];
            var top = y + head + i * row;
            Cell(ui, x, top, half, l, _left, changed, HudUi.Err);
            Cell(ui, x + half, top, half, r, _right, changed, HudUi.Ok);
        }

        host.Hits.Region(x, y + head, w, h - head, this);
    }

    private static void Title(HudUi ui, double x, double y, double h, string title, string dim)
    {
        var caps = Caps(title);
        var w = ui.Spaced(x, y, h, ui.F.Small, caps, ui.Px(2), HudUi.Faint);
        ui.Text(x + w + ui.Px(10), y, h, ui.F.Small, dim, HudUi.Faint with { A = 0.6 });
    }

    private void Links(HudUi ui, double right, double y, double h)
    {
        _close ??= () =>
        {
            Close();
            host.Mode = EditorMode.Mine;
        };
        StudioIcons.Close(ui, right - ui.Px(8), y + h / 2, ui.Px(8), HudUi.Faint);
        host.Hits.Add(right - ui.Px(20), y, ui.Px(20), h, _close);
        right -= ui.Px(30);
        foreach (var (label, run) in _actions.Bounded(4))
        {
            var lw = ui.TextW(ui.F.Small, label);
            ui.Text(right - lw, y, h, ui.F.Small, label, HudUi.Link);
            host.Hits.Add(right - lw - ui.Px(4), y, lw + ui.Px(8), h, run);
            right -= lw + ui.Px(16);
        }
    }

    private static void Cell(HudUi ui, double x, double y, double w, int index, string[] lines, bool changed,
        Rgba tone)
    {
        var row = ui.F.BodyRow;
        if (index < 0)
        {
            ui.Fill(x + 1, y, w - 1, row, HudUi.Track with { A = 0.03 });
            return;
        }

        if (changed) ui.Fill(x + 1, y, w - 1, row, tone with { A = 0.1 });

        var column = ui.Column(x + ui.Px(4), w - ui.Px(8), y);
        CodeText.Line(column, ui.Fit(ui.F.Mono, lines[index], w - ui.Px(60)), CodeText.Gutter(index));
    }

    public bool Key(GlKeys key)
    {
        switch (key)
        {
            case GlKeys.Up:
                _first = Math.Max(0, _first - 1);
                return true;
            case GlKeys.Down:
                _first++;
                return true;
            case GlKeys.PageUp:
                _first = Math.Max(0, _first - 20);
                return true;
            case GlKeys.PageDown:
                _first += 20;
                return true;
            default: return false;
        }
    }
}
