using Komet.Hud;
using Komet.Rendering;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// GAME VALUES (folded by default): every value Komet hands shaders as komet… uniforms, with its type and live value
// (colours with a swatch), the search over the names; a click declares it in the open file, declared ones are marked
internal sealed partial class StudioSide
{
    private readonly TextField _valueQuery = new(40);
    private Action? _focusValues;

    private void ValuesType(char c) => _valueQuery.Type(c);

    private bool ValuesKey(GlKeys key, bool ctrl)
    {
        if (key is GlKeys.Enter or GlKeys.KeypadEnter or GlKeys.Escape)
        {
            host.Focus = StudioFocus.Editor;
            return true;
        }

        _ = _valueQuery.Key(key, ctrl, host.Api);
        return true;
    }

    private double Values(HudUi ui, double y)
    {
        _focusValues ??= () => host.Focus = StudioFocus.Values;
        var fw = _w - ui.Px(28);
        var h = _valueQuery.Draw(ui, _x + ui.Px(14), y + ui.Px(4), fw, L("search-values"), host.Focus == StudioFocus.Values);
        Click(_x + ui.Px(14), y + ui.Px(4), fw, h, _focusValues);
        y += h + ui.Px(10);
        var q = _valueQuery.Text.Trim();
        var text = host.Current?.Text ?? "";
        var shown = 0;
        foreach (var value in GameValues.All.Bounded(64))
        {
            if (q.Length > 0 && !value.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) continue;

            y = ValueRow(ui, y, value, ShaderIde.Declared(text, value.Name));
            shown++;
        }

        if (shown == 0)
        {
            ui.Text(_x + ui.Px(14), y, ui.F.BodyRow, ui.F.Small, ui.Fit(ui.F.Small, L("no-values-match", q), fw), HudUi.Faint);
            y += ui.F.BodyRow;
        }

        return y + ui.Px(6);
    }

    private double ValueRow(HudUi ui, double y, GameValues.Value value, bool declared)
    {
        var h = ui.F.BodyRow * 2;
        var hot = host.Hot(_x, y, _w, h) && y >= _top;
        if (hot) ui.Fill(_x + ui.Px(6), y, _w - ui.Px(12), h, HudUi.Track with { A = 0.06 });

        var x = _x + ui.Px(14);
        var live = GameValues.Read(value);
        if (value.Color && live.Length >= 3)
        {
            ui.Fill(x, y + ui.Px(5), ui.Px(10), ui.Px(10), new Rgba(live[0], live[1], live[2], 1));
            x += ui.Px(16);
        }

        ui.Text(x, y, ui.F.BodyRow, ui.F.Mono, ui.Fit(ui.F.Mono, value.Name, _w * 0.6), declared ? HudUi.Link : HudUi.Soft);
        ui.TextRight(_x + _w - ui.Px(14), y, ui.F.BodyRow, ui.F.Small, declared ? L("value-declared") : value.Type,
            declared ? HudUi.Link : HudUi.Faint);
        var numbers = ui.Fit(ui.F.Mono, ShaderIde.Format(live), _w - ui.Px(28));
        ui.Text(_x + ui.Px(14), y + ui.F.BodyRow, ui.F.BodyRow, ui.F.Mono, numbers, HudUi.Faint);
        Click(_x, y, _w, h, () => host.Declare(value));
        host.Tip(_x, Math.Max(y, _top), _w, h, value.Doc + " " + L("tip-value-insert"));
        return y + h;
    }
}
