using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The Inspector tab: the preview's last click as a table over the panel's width (target, value in words, a colour
// swatch where one makes sense); targets with nothing at that place faint at the end. A click in the preview opens it.
internal sealed partial class StudioPanel
{
    private const int Inspector = 4;
    private const double InspectorHeight = 300;

    public bool InspectorShown => Shown && _tab == Inspector;

    // A click in the preview: the tab opens, tall enough for a few rows
    public void ShowInspector()
    {
        Show(Inspector);
        host.Prefs.Panel = Math.Max(host.Prefs.Panel, InspectorHeight);
    }

    private void InspectorTab(HudUi ui, double x, double top, double w, double bottom)
    {
        var view = host.Buffers;
        var row = ui.Px(RowHeight);
        var at = view.InspectedAt;
        var note = at is { } uv
            ? L("inspector-at", (int)(uv.U * 100), (int)(uv.V * 100), view.Shown.Name)
            : L("inspector-empty");
        ui.Text(x + ui.Px(16), top + ui.Px(2), row, ui.F.Small, ui.Fit(ui.F.Small, note, w - ui.Px(32)), HudUi.Faint);
        top += row + ui.Px(4);
        host.Hits.Region(x, top, w, bottom - top, this);
        var rows = view.Inspected;
        if (at is null || rows.Count == 0) return;

        var nameW = ui.TextW(ui.F.Body, L("inspector-target")) + ui.Px(10);
        var valueW = ui.TextW(ui.F.Body, L("inspector-value"));
        foreach (var line in rows.Bounded(BufferView.MaxTargets + 8))
        {
            nameW = Math.Max(nameW, ui.TextW(ui.F.Body, line.Name));
            valueW = Math.Max(valueW, ui.TextW(ui.F.Mono, line.Value));
        }

        // as many column blocks side by side as the width holds: the panel's few rows show every target
        var swatchW = ui.TextW(ui.F.Small, Caps(L("inspector-swatch"))) + ui.Px(20);
        var blockW = ui.Px(16) + nameW + ui.Px(24) + valueW + ui.Px(24) + swatchW + ui.Px(24);
        var blocks = Math.Clamp((int)(w / Math.Max(1, blockW)), 1, 4);
        var per = Math.Max(1, (int)((bottom - top - row) / row));
        _first = Math.Clamp(_first, 0, Math.Max(0, rows.Count - blocks * per)); // the wheel scrolls row by row
        for (var b = 0; b < Math.Min(blocks, 4); b++)
        {
            var bx = x + b * blockW;
            var cols = (Name: bx + ui.Px(16), Value: bx + ui.Px(40) + nameW, Swatch: bx + ui.Px(64) + nameW + valueW);
            InspectorHead(ui, cols, top, row);
            for (var i = 0; i < Math.Min(per, BufferView.MaxTargets + 8); i++)
            {
                var index = _first + b * per + i;
                if (index < rows.Count) InspectorRow(ui, cols, top + row + i * row, row, rows[index]);
            }
        }
    }

    private static void InspectorHead(HudUi ui, (double Name, double Value, double Swatch) cols, double y, double h)
    {
        _ = ui.Spaced(cols.Name, y, h, ui.F.Small, Caps(L("inspector-target")), ui.Px(2), HudUi.Faint);
        _ = ui.Spaced(cols.Value, y, h, ui.F.Small, Caps(L("inspector-value")), ui.Px(2), HudUi.Faint);
        _ = ui.Spaced(cols.Swatch, y, h, ui.F.Small, Caps(L("inspector-swatch")), ui.Px(2), HudUi.Faint);
    }

    // A row in full: the columns are as wide as their longest entry
    private static void InspectorRow(HudUi ui, (double Name, double Value, double Swatch) cols, double y, double h,
        InspectRow line)
    {
        var ink = line.Empty ? HudUi.Faint with { A = 0.6 } : HudUi.Soft;
        ui.Text(cols.Name, y, h, ui.F.Body, line.Name, ink);
        ui.Text(cols.Value, y, h, line.Empty ? ui.F.Small : ui.F.Mono, line.Value, line.Empty ? ink : Rgba.White(0.9));
        if (line.Swatch is { Length: >= 3 } c)
        {
            var s = h - ui.Px(8);
            var color = new Rgba(Math.Clamp(c[0], 0, 1), Math.Clamp(c[1], 0, 1), Math.Clamp(c[2], 0, 1), 1);
            ui.Fill(cols.Swatch, y + ui.Px(4), s * 1.6, s, color, ui.Px(2));
            TextField.Frame(ui, cols.Swatch, y + ui.Px(4), s * 1.6, s, HudUi.Divider);
        }
    }
}
