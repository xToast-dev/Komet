using Komet.Hud;

namespace Komet.Sdk;

// The right-click menu: entries the region under the mouse adds, drawn over everything, taken by mouse or keys
internal sealed class ContextMenu : IIdeRegion
{
    private const int MaxItems = 40;
    private readonly List<(string Label, Action? Run)> _items = [];
    private double _anchorX, _anchorY, _x, _y, _w, _row;
    private int _selected;

    public bool IsOpen { get; private set; }

    // A group's heading: drawn faint, never chosen
    public void Header(string label)
    {
        if (_items.Count < MaxItems) _items.Add((label, null));
    }

    public void Add(string label, Action run)
    {
        if (_items.Count < MaxItems) _items.Add((label, run));
    }

    public void Begin(double x, double y)
    {
        _items.Clear();
        (_anchorX, _anchorY, _selected, _row, IsOpen) = (x, y, 0, 0, false);
    }

    // Opens when the regions added anything
    public bool End()
    {
        IsOpen = _items.Count > 0;
        return IsOpen;
    }

    public void Close() => IsOpen = false;

    public void Draw(HudUi ui, double bottom)
    {
        if (!IsOpen) return;

        var count = Math.Min(_items.Count, MaxItems);
        _w = Math.Min(ui.W, ui.Px(340));
        _row = Math.Min(ui.F.BodyRow, Math.Max(1, (bottom - ui.Y) / count));
        _x = Math.Clamp(_anchorX, ui.X, ui.X + ui.W - _w);
        _y = Math.Clamp(_anchorY, ui.Y, Math.Max(ui.Y, bottom - _row * count));
        ui.Panel(_x, _y, _w, _row * count, HudUi.PopupBack);
        for (var i = 0; i < Math.Min(count, MaxItems); i++)
        {
            var y = _y + i * _row;
            if (i == _selected)
            {
                ui.Fill(_x, y, _w, _row, HudUi.SelectedBack);
                ui.Fill(_x, y, Math.Max(1, ui.Px(2)), _row, HudUi.Accent);
            }

            var header = _items[i].Run is null;
            var ink = i == _selected ? HudUi.Link : HudUi.Soft;
            ui.Text(_x + ui.Px(header ? 6 : 18), y, _row, ui.F.Small, ui.Fit(ui.F.Small, _items[i].Label, _w - ui.Px(24)),
                header ? HudUi.Faint : ink);
        }
    }

    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        var index = Index(x, y);
        IsOpen = false;
        if (index >= 0 && button == EnumMouseButton.Left) _items[index].Run?.Invoke();

        return true;
    }

    public void Hover(double x, double y)
    {
        if (Index(x, y) is var index and >= 0 && _items[index].Run is not null) _selected = index;
    }

    public bool Wheel(double x, int lines)
    {
        IsOpen = false;
        return true;
    }

    public bool Key(GlKeys key)
    {
        switch (key)
        {
            case GlKeys.Up: _selected = Math.Max(0, _selected - 1); break;
            case GlKeys.Down: _selected = Math.Min(_items.Count - 1, _selected + 1); break;
            case GlKeys.Escape: IsOpen = false; break;
            case GlKeys.Enter:
                IsOpen = false;
                _items[Math.Clamp(_selected, 0, _items.Count - 1)].Run?.Invoke();
                break;
        }

        return true;
    }

    private int Index(double x, double y)
    {
        if (_row <= 0 || x < _x || x >= _x + _w || y < _y) return -1;

        var index = (int)((y - _y) / _row);
        return index < Math.Min(_items.Count, MaxItems) ? index : -1;
    }
}
