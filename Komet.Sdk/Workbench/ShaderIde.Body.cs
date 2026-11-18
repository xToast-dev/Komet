using System.Globalization;
using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The tabs as one region: a left click shows a tab (or closes it on its ×), a middle click closes it, a drag reorders
internal sealed class TabStrip(ShaderIde host) : IIdeRegion
{
    private const double DragStart = 6;
    private int _from = -1;
    private double _pressX;
    private bool _moving;

    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        var index = host.TabAt(x, out var onClose);
        if (index < 0 || index >= host.Tabs.Count) return false;

        var tab = host.Tabs[index];
        if (button == EnumMouseButton.Middle || onClose)
        {
            host.CloseTab(tab);
            return false;
        }

        if (button != EnumMouseButton.Left) return false;

        host.Select(tab);
        (_from, _pressX, _moving) = (index, x, false);
        return true;
    }

    public void Drag(double x, double y)
    {
        if (_from < 0 || (!_moving && Math.Abs(x - _pressX) < DragStart)) return;

        _moving = true;
        host.TabDrop = host.TabSlotAt(x);
    }

    public void Up()
    {
        if (_moving && host.TabDrop >= 0) host.MoveTab(_from, host.TabDrop);
        (_from, _moving, host.TabDrop) = (-1, false, -1);
    }
}

// The code (or the comparison) with the find widget, the read-only footer, the history dropdown
internal sealed partial class ShaderIde
{
    // The tab under x (its ×: the last 26 px), -1 for none
    public int TabAt(double x, out bool onClose)
    {
        onClose = false;
        for (var i = 0; i < Math.Min(_tabXs.Count, MaxTabs + 1); i++)
        {
            var (at, w) = _tabXs[i];
            if (x >= at && x < at + w && _ui is { } ui)
            {
                onClose = x >= at + w - ui.Px(26);
                return i;
            }
        }

        return -1;
    }

    // The insertion slot a tab drag shows (0..count), -1 while none
    public int TabDrop { get; set; } = -1;

    // The slot between tabs nearest x: before the first tab whose middle lies right of x
    public int TabSlotAt(double x)
    {
        for (var i = 0; i < Math.Min(_tabXs.Count, MaxTabs + 1); i++)
        {
            if (x < _tabXs[i].X + _tabXs[i].W / 2) return i;
        }

        return Math.Min(_tabXs.Count, MaxTabs + 1);
    }

    // Moves the tab at from to the slot (counted before the move); the open tab stays open, the order is kept
    public void MoveTab(int from, int slot)
    {
        if (from < 0 || from >= _tabs.Count) return;

        var to = Math.Clamp(slot > from ? slot - 1 : slot, 0, _tabs.Count - 1);
        if (to == from) return;

        var tab = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, tab);
        Persist();
    }

    private void Body(HudUi ui, double x, double y, double w, double bottom)
    {
        if (Diff.Open)
        {
            Diff.Draw(ui, x, y, w, bottom - y);
            return;
        }

        if (Active is not { } document) return;

        var pad = ui.Px(10);
        var column = ui.Column(x + pad, w - 2 * pad, y + ui.Px(8));
        Editor.Baseline = Mode == EditorMode.Mine && StudioLink.GameFileOf(document.Path) is { } game
            ? Baseline(game)
            : null;
        Editor.Draw(column, bottom, document);
        Hits.Region(x, y, w, bottom - y, Editor);
        Find.Draw(ui, x + w - ui.Px(450) - ui.Px(20), y + ui.Px(6), ui.Px(450));
        Rename.Draw(ui, x + w - ui.Px(450) - ui.Px(20), y + ui.Px(Find.Open ? 80 : 6), ui.Px(450));
    }

    private string? _baselineOf;
    private string[]? _baselineLines;

    // The game original's lines for the changed-line marks, read once per file
    private string[]? Baseline(string game)
    {
        if (_baselineOf != game)
        {
            _baselineOf = game;
            _baselineLines = StudioLink.Read(game, out var text, out _) ? text.Split('\n') : null;
        }

        return _baselineLines;
    }

    // A read-only file's footer: a game shader offers its copy, anything else says where Komet writes
    private void Footer(HudUi ui, double x, double y, double w, double h, ShaderDocument document)
    {
        ui.Fill(x, y, w, 1, HudUi.Divider);
        ui.Fill(x, y + 1, w, h - 1, BandGround);
        var game = StudioLink.IsGameShader(document.Path);
        ui.Text(x + ui.Px(18), y, h, ui.F.Small, game ? L("game-file-readonly") : L("readonly-where"), HudUi.Faint);
        if (!game) return;

        var copy = Project is null ? null : StudioLink.CopyPath(Project, document.Path);
        var label = copy is not null ? L("open-my-copy")
            : L("copy-into", Project ?? L("project-word"));
        var bw = ui.TextW(ui.F.Small, label) + ui.Px(24);
        var (bx, by, bh) = (x + w - ui.Px(18) - bw, y + (h - ui.Px(24)) / 2, ui.Px(24));
        ui.Fill(bx, by, bw, bh, Hot(bx, by, bw, bh) ? HudUi.Track : HudUi.Track with { A = 0.05 });
        TextField.Frame(ui, bx, by, bw, bh, HudUi.Divider);
        ui.Text(bx + ui.Px(12), by, bh, ui.F.Small, label, HudUi.Soft);
        var path = document.Path;
        Hits.Add(bx, by, bw, bh, copy is not null ? () => Open(copy) : () => CopyIn(path));
    }

    private void ToggleHistory() => _historyOpen = !_historyOpen;

    private void OpenHistory()
    {
        if (_current is null || _current.ReadOnly)
        {
            Toast(L("no-history"));
            return;
        }

        _historyOpen = true;
    }

    // Verlauf: each good build's state of the file, the newest the last good one; a row compares, its link restores
    private void History(HudUi ui)
    {
        if (!_historyOpen || _current is not { } document)
        {
            _historyOpen = false;
            return;
        }

        var shots = StudioLink.History(document.Path);
        var row = ui.Px(30);
        var w = ui.Px(300);
        var h = ui.Px(30) + Math.Max(1, shots.Length) * row + ui.Px(8);
        var (x, y) = (Math.Max(ui.X, _historyX - w), _historyY + ui.Px(2));
        Hits.Block(ui.X, 0, ui.W, _height);
        Hits.Add(ui.X, 0, ui.W, _height, ToggleHistory);
        ui.Panel(x, y, w, h, HudUi.PopupBack with { A = 1 });
        TextField.Frame(ui, x, y, w, h, HudUi.Divider);
        Hits.Block(x, y, w, h);
        _ = ui.Spaced(x + ui.Px(14), y + ui.Px(4), ui.Px(22), ui.F.Small, Caps(L("history")), ui.Px(2), HudUi.Faint);
        var top = y + ui.Px(30);
        if (shots.Length == 0)
        {
            ui.Text(x + ui.Px(14), top, row, ui.F.Small, ui.Fit(ui.F.Small, L("no-snapshot"), w - ui.Px(28)),
                HudUi.Faint);
            return;
        }

        for (var i = 0; i < Math.Min(shots.Length, 10); i++)
        {
            HistoryRow(ui, x, top + i * row, w, row, shots[i], i, document);
        }
    }

    private void HistoryRow(HudUi ui, double x, double y, double w, double h, StudioSnapshot shot, int index,
        ShaderDocument document)
    {
        if (Hot(x, y, w, h)) ui.Fill(x, y, w, h, HudUi.Track with { A = 0.06 });

        var time = shot.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ui.Text(x + ui.Px(14), y, h, ui.F.Mono, time, HudUi.Soft);
        if (index == 0) ui.Text(x + ui.Px(90), y, h, ui.F.Small, L("last-good"), HudUi.Ok);

        var restore = L("restore");
        var rw = ui.TextW(ui.F.Small, restore);
        ui.Text(x + w - ui.Px(14) - rw, y, h, ui.F.Small, restore, HudUi.Link);
        Hits.Add(x, y, w - rw - ui.Px(20), h, () =>
        {
            _historyOpen = false;
            Diff.Show(L("state") + " " + time + (index == 0 ? " · " + L("last-good-short") : ""),
                StudioLink.SnapshotText(shot), L("now"), document.Text,
                [(L("restore"), () => RestoreSnapshot(document, index))]);
        });
        Hits.Add(x + w - rw - ui.Px(20), y, rw + ui.Px(20), h, () => RestoreSnapshot(document, index));
    }

    // A good state back into the file (the backend writes it), into the draft, then built like an edit
    private void RestoreSnapshot(ShaderDocument document, int index)
    {
        _historyOpen = false;
        Diff.Close();
        Mode = EditorMode.Mine;
        var shots = StudioLink.History(document.Path);
        if (index >= shots.Length || StudioLink.RestoreHistory(document.Path, index) is not { } text)
        {
            Toast(L("no-good-state"));
            return;
        }

        _ = document.Replace(0, document.Text.Length, text);
        document.Disk = text;
        Changed(document.Path);
        var time = shots[index].At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        AddLog(document.Name + ": " + L("state-restored", time));
        Toast(L("state-restored", time));
    }

    private void RestoreGood()
    {
        if (_current is not { } document || StudioLink.History(document.Path) is not { Length: > 0 } shots)
        {
            Toast(L("no-good-state"));
            return;
        }

        if (document.Text == StudioLink.SnapshotText(shots[0]))
        {
            Toast(L("already-good"));
            return;
        }

        RestoreSnapshot(document, 0);
    }
}
