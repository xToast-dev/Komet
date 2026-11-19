using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The dialog's box: a spaced title, the body (the question, or the zip's parts and modinfo.json's fields as the menu's
// rows), the footer with the check's result, Abbrechen and the accent button
internal sealed partial class StudioModal
{
    private Action?[]? _focusFields;

    public void Draw(HudUi ui)
    {
        if (!Open) return;

        _cancel ??= Close;
        _accept ??= Accept;
        _focusFields ??= [() => _focus = 0, () => _focus = 1, () => _focus = 2];
        ui.Fill(ui.X, 0, ui.W, host.Height, Rgba.Black(0.55));
        host.Hits.Add(ui.X, 0, ui.W, host.Height, _cancel);
        var w = Math.Min(ui.Px(480), ui.W - ui.Px(40));
        var row = ui.Px(34);
        var bodyH = _project is null ? ui.Px(60) : ui.Px(30) * 2 + row * (_parts.Count + 3);
        var h = ui.Px(48) + bodyH + ui.Px(56);
        var (x, y) = (ui.X + (ui.W - w) / 2, Math.Max(ui.Px(40), (host.Height - h) / 3));
        ui.Panel(x, y, w, h, HudUi.PopupBack with { A = 1 });
        TextField.Frame(ui, x, y, w, h, HudUi.Divider);
        host.Hits.Block(x, y, w, h);
        var tw = ui.Spaced(x + ui.Px(20), y, ui.Px(48), ui.F.Small, Caps(_title), ui.Px(2.5), HudUi.Soft);
        if (_sub.Length > 0) ui.Text(x + ui.Px(30) + tw, y, ui.Px(48), ui.F.Small, _sub, HudUi.Faint);

        ui.Fill(x, y + ui.Px(48) - 1, w, 1, HudUi.Divider);
        var top = y + ui.Px(48);
        if (_project is null)
        {
            var column = ui.Column(x + ui.Px(20), w - ui.Px(40), top + ui.Px(14));
            column.Wrap(_text, HudUi.Soft, ui.F.Body);
        }
        else
        {
            ExportBody(ui, x, top, w, row);
        }

        Foot(ui, x, y + h - ui.Px(56), w);
    }

    private void ExportBody(HudUi ui, double x, double y, double w, double row)
    {
        y = Heading(ui, x, y, w, L("zip-content"));
        foreach (var (name, info, bad) in _parts.Bounded(8))
        {
            var folder = name.EndsWith('/');
            if (folder)
            {
                StudioIcons.Folder(ui, x + ui.Px(20), y + row / 2, ui.Px(14), StudioColors.Gold);
            }
            else
            {
                StudioIcons.File(ui, x + ui.Px(20), y + row / 2, ui.Px(14), ".json");
            }

            ui.Text(x + ui.Px(44), y, row, ui.F.Body, name, HudUi.Soft);
            ui.TextRight(x + w - ui.Px(20), y, row, ui.F.Small, info, bad ? HudUi.Err : HudUi.Faint);
            y += row;
        }

        y = Heading(ui, x, y, w, "modinfo.json");
        ReadOnlySpan<string> labels = [L("name"), L("mod-id"), L("version")];
        for (var i = 0; i < Math.Min(labels.Length, 3); i++)
        {
            ui.Text(x + ui.Px(20), y, row, ui.F.Body, labels[i], HudUi.Soft);
            var fw = w * 0.56;
            var fy = y + (row - ui.F.BodyRow - ui.Px(8)) / 2;
            _ = _fields[i].Draw(ui, x + w - ui.Px(20) - fw, fy, fw, "", _focus == i);
            host.Hits.Add(x + w - ui.Px(20) - fw, y, fw, row, _focusFields![i]!);
            y += row;
        }
    }

    private static double Heading(HudUi ui, double x, double y, double w, string text)
    {
        var h = ui.Px(30);
        var caps = Caps(text);
        var tw = ui.Spaced(x + ui.Px(12), y, h, ui.F.Small, caps, ui.Px(2), HudUi.Faint);
        ui.Fill(x + ui.Px(22) + tw, y + h / 2, Math.Max(0, w - tw - ui.Px(34)), 1, HudUi.Divider);
        return y + h;
    }

    private void Foot(HudUi ui, double x, double y, double w)
    {
        var h = ui.Px(56);
        ui.Fill(x, y, w, 1, HudUi.Divider);
        if (_project is not null)
        {
            var bad = Check();
            var text = bad.Length > 0 ? bad : "Mods/" + _fields[1].Text.Trim() + "-" + _fields[2].Text.Trim() + ".zip";
            ui.Text(x + ui.Px(20), y, h, ui.F.Small, ui.Fit(ui.F.Small, text, w * 0.45),
                bad.Length > 0 ? HudUi.Err : HudUi.Faint);
        }

        var right = x + w - ui.Px(20);
        right = Button(ui, right, y, h, _ok, true, _accept!);
        _ = Button(ui, right - ui.Px(10), y, h, L("cancel"), false, _cancel!);
    }

    private double Button(HudUi ui, double right, double y, double h, string label, bool primary, Action click)
    {
        var caps = Caps(label);
        var bw = ui.TextW(ui.F.Small, caps) + ui.Px(2) * caps.Length + ui.Px(32);
        var (bx, by, bh) = (right - bw, y + (h - ui.Px(32)) / 2, ui.Px(32));
        var hot = host.Hot(bx, by, bw, bh);
        if (primary)
        {
            ui.Fill(bx, by, bw, bh, hot ? HudUi.Accent : HudUi.Accent with { A = 0.0 });
            TextField.Frame(ui, bx, by, bw, bh, HudUi.Accent with { A = 0.8 });
        }
        else
        {
            ui.Fill(bx, by, bw, bh, hot ? HudUi.Track : HudUi.Track with { A = 0.04 });
            TextField.Frame(ui, bx, by, bw, bh, HudUi.Divider);
        }

        _ = ui.Spaced(bx + ui.Px(16), by, bh, ui.F.Small, caps, ui.Px(2), Rgba.White(1));
        host.Hits.Add(bx, by, bw, bh, click);
        return bx;
    }
}
