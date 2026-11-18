using Komet.Core;
using Komet.Hud;
using Komet.Options;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The right column's sections and rows, painted with the options menu's controls
internal sealed partial class StudioSide
{
    private OptionRow? _dragRow;
    private double _dragValue, _railFrom, _railW;
    private RailRegion? _rail;

    public void Draw(HudUi ui, double x, double top, double w, double h)
    {
        (_x, _w, _top, _bottom) = (x, w, top, top + h);
        ui.Fill(x, top, w, h, host.PanelGround);
        ui.Fill(x, top, 1, h, HudUi.Divider);
        host.Hits.Region(x, top, w, h, this); // under the rows' buttons: the wheel scrolls the column
        _rail ??= new RailRegion(this);
        if (Menu is not { } menu) return;

        _railRows.Clear();
        host.Buffers.Marking = host.Panel.InspectorShown && !host.TakingSnapshot; // no ring in the vanilla snapshot
        // PREVIEW stays put above the scrolled sections while it leaves them room: the picture is live only when it
        // shows whole, so it must not scroll half out of the column
        var pinned = _viewHeight < h * 0.6;
        var band = top;
        if (pinned)
        {
            menu.Canvas.Clip(x, top, w, h);
            band = Math.Min(View(ui, top), top + h);
            menu.Canvas.Unclip();
            _viewHeight = band - top;
            ui.Fill(x + 1, band - 1, w - 1, 1, HudUi.Divider);
        }

        (_top, h) = (band, top + h - band);
        menu.Canvas.Clip(x, band, w, h);
        var y = band - _scroll;
        if (!pinned)
        {
            y = View(ui, y);
            _viewHeight = y - (band - _scroll);
        }

        host.TipBand = (band, band + h);
        y = Sections(ui, menu, y);
        host.TipBand = (double.MinValue, double.MaxValue);
        menu.Canvas.Unclip();
        _content = y + _scroll - band + ui.Px(24);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _content - h));
        ScrollBar(ui, band, h);
    }

    private double _viewHeight;

    // The column's scroll bar on its right edge, shown whenever the sections are taller than the column
    private void ScrollBar(HudUi ui, double top, double h)
    {
        if (_content <= h + 1) return;

        var bw = ui.Px(5);
        var thumb = Math.Max(ui.Px(24), h * h / _content);
        var at = top + (h - thumb) * (_scroll / Math.Max(1, _content - h));
        ui.Fill(_x + _w - bw - ui.Px(2), top, bw, h, HudUi.Track with { A = 0.05 }, bw / 2);
        ui.Fill(_x + _w - bw - ui.Px(2), at, bw, thumb, HudUi.Faint with { A = 0.55 }, bw / 2);
    }

    // PREVIEW, SETTINGS, THIS SHADER, GAME VALUES, FRAME, each folding (the folds are kept in studio.json)
    private double View(HudUi ui, double y)
    {
        y = Section(ui, y, "view", L("preview"), ViewCost() is > 0 and var ms ? L("buffer-cost", BufferView.Format((float)ms)) : "");
        if (Open("view"))
        {
            y = Toolbar(ui, y);
            y = Picture(ui, y);
            y = CloudPreviewControl(ui, y);
            y = ClimateControl(ui, y);
            y = PreviewFoot(ui, y);
        }

        return y;
    }

    private double ViewCost()
    {
        if (host.LiveModel) return 0;

        return host.Model3D.On ? host.Model3D.LastMs : host.Buffers.LastMs;
    }

    // Under the 3D view of a shader that reads the climate tint: temperature, rainfall and season as bars, a click sets
    // the value where it lands; a reset puts the scene's default back
    private double ClimateControl(HudUi ui, double y)
    {
        var model = host.Model3D;
        if (!model.On || host.LiveModel || !model.UsesClimate) return y;

        y = ClimateBar(ui, y, L("model-temperature"), model.Temperature, v => model.Temperature = v);
        y = ClimateBar(ui, y, L("model-rainfall"), model.Rainfall, v => model.Rainfall = v);
        y = ClimateBar(ui, y, L("model-season"), model.Season, v => model.Season = v);
        var label = L("model-climate-reset");
        var h = ui.Px(24);
        Tool(ui, _x + ui.Px(6), y, ui.TextW(ui.F.Small, label) + ui.Px(16), h, label, false, true, model.ResetClimate,
            L("tip-model-climate"));
        return y + h + ui.Px(6);
    }

    private double ClimateBar(HudUi ui, double y, string label, float value, Action<float> set)
    {
        var h = ui.Px(22);
        var lw = Math.Min(ui.Px(110), _w * 0.35);
        var (tx, tw) = (_x + ui.Px(14) + lw, _w - lw - ui.Px(28));
        ui.Text(_x + ui.Px(14), y, h, ui.F.Small, ui.Fit(ui.F.Small, label, lw - ui.Px(6)), HudUi.Soft);
        ui.Fill(tx, y + h / 2 - ui.Px(3), tw, ui.Px(6), HudUi.Track with { A = 0.15 });
        ui.Fill(tx, y + h / 2 - ui.Px(3), tw * value, ui.Px(6), HudUi.Link);
        ui.TextRight(tx + tw, y - h * 0.55, h, ui.F.Small, ((int)(value * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + " %", HudUi.Faint);
        Click(tx, y, tw, h, () =>
        {
            set((float)((host.MouseX - tx) / Math.Max(tw, 1)));
            host.Redraw();
        });
        host.Tip(tx, y, tw, h, L("tip-model-climate"));
        return y + h;
    }

    private double CloudPreviewControl(HudUi ui, double y)
    {
        const int volumetric = 1;
        if (!host.VolumetricModel || Vintagestory.Client.NoObf.ClientSettings.CloudRenderMode == volumetric) return y;

        var label = L("model-enable-volumetric");
        var width = ui.TextW(ui.F.Small, label) + ui.Px(16);
        var height = ui.Px(26);
        Tool(ui, _x + ui.Px(6), y, width, height, label, false, true, () =>
        {
            Vintagestory.Client.NoObf.ClientSettings.CloudRenderMode = volumetric;
            host.AddLog(L("model-enable-volumetric"));
        },
            L("tip-model-enable-volumetric"));
        return y + height + ui.Px(6);
    }

    private double Sections(HudUi ui, MenuControls menu, double y)
    {
        y = Section(ui, y, "set", L("settings"), host.Project ?? "");
        if (Open("set")) y = Settings(ui, menu, y);

        y = Section(ui, y, "shader", L("this-shader"), "");
        if (Open("shader")) y = ThisShader(ui, menu, y);

        y = Section(ui, y, "values", L("game-values"), "");
        if (Open("values")) y = Values(ui, y);

        y = Section(ui, y, "frame", L("frame"), _total);
        if (Open("frame")) y = Frame(ui, menu, y);

        return y;
    }

    // A section's head: the triangle, the spaced caps, a faint note on the right; a click folds it
    private double Section(HudUi ui, double y, string id, string title, string note)
    {
        var h = ui.Px(HeadHeight);
        var open = Open(id);
        StudioIcons.Twist(ui, _x + ui.Px(10), y + h / 2, ui.Px(14), open, HudUi.Faint);
        _ = ui.Spaced(_x + ui.Px(30), y, h, ui.F.Small, Caps(title), ui.Px(2.5), HudUi.Soft);
        if (note.Length > 0)
        {
            ui.TextRight(_x + _w - ui.Px(14), y, h, ui.F.Small, ui.Fit(ui.F.Small, note, _w * 0.4), HudUi.Faint);
        }

        Click(_x, y, _w, h, () => Fold(id));
        if (!open) ui.Fill(_x + 1, y + h - 1, _w - 1, 1, HudUi.Divider);

        return y + h;
    }

    private void Click(double x, double y, double w, double h, Action click)
    {
        var top = Math.Max(y, _top);
        var bottom = Math.Min(y + h, _bottom);
        if (bottom > top) host.Hits.Add(x, top, w, bottom - top, click);
    }

    // A menu row: the label in the menu's font, the lit row under the mouse; the control column's span
    private (double Left, double Right) Row(MenuControls menu, double y, string label, bool enabled = true)
    {
        var h = S(RowHeight);
        var lit = host.Hot(_x, y, _w, h) && y >= _top;
        if (lit) menu.Lit(_x + S(6), y, _w - S(12), h);

        var cw = Math.Min(S(MenuControls.ControlWidth), _w * 0.56);
        var (text, font) = menu.Shrunk(label, _w - cw - S(30), menu.Fonts.Text);
        menu.Canvas.Text(_x + S(14), y, h, font, text, menu.Ink(enabled, lit));
        return (_x + _w - S(12) - cw, _x + _w - S(12));
    }

    private double Group(MenuControls menu, double y, string text)
    {
        var h = S(GroupHeight);
        menu.Group(_x + S(4), y, _w - S(8), h, Caps(text), S(18));
        return y + h;
    }

    // Under the picture: "Show the game" (held, the studio hides; the hint is its tooltip) and what the game runs, the
    // project or vanilla, switched live (not a frozen picture as the split is)
    private double PreviewFoot(HudUi ui, double y)
    {
        var x = _x + ui.Px(6);
        var label = L("hold-peek");
        var (bw, bh) = (ui.TextW(ui.F.Small, label) + ui.Px(20), ui.Px(26));
        var hot = host.Hot(x, y, bw, bh) || host.Peeking;
        ui.Fill(x, y, bw, bh, hot ? HudUi.Accent : HudUi.Accent with { A = 0.3 }, ui.Px(3));
        ui.Text(x + ui.Px(10), y, bh, ui.F.Small, label, Rgba.White(1));
        if (y + bh > _top && y < _bottom)
        {
            host.Hits.Region(x, Math.Max(y, _top), bw, Math.Min(y + bh, _bottom) - Math.Max(y, _top), _peek);
        }

        host.Tip(x, y, bw, bh, L("hold-peek-hint"));
        if (host.Project is null) return y + bh + ui.Px(8);

        // "Game runs": a labelled switch of its own row, so it never reads as the Compare split
        y += bh + ui.Px(6);
        var mine = L("mine");
        var vanilla = L("original");
        var (mw, vw) = (ui.TextW(ui.F.Small, mine) + ui.Px(16), ui.TextW(ui.F.Small, vanilla) + ui.Px(16));
        var sx = _x + _w - ui.Px(6) - mw - vw;
        var runs = L("game-runs");
        ui.Text(x + ui.Px(2), y, bh, ui.F.Small, ui.Fit(ui.F.Small, runs, Math.Max(ui.Px(20), sx - x - ui.Px(8))), HudUi.Soft);
        host.Tip(x, y, sx - x, bh, L("tip-game-runs"));
        Tool(ui, sx, y, mw, bh, mine, !RunsVanilla, true, () => ShowOriginal(false), L("tip-game-runs"));
        Tool(ui, sx + mw, y, vw, bh, vanilla, RunsVanilla, true, () => ShowOriginal(true), L("tip-game-runs"));
        return ForceRow(ui, x, y + bh + ui.Px(6), bh);
    }

    // "Force in the game": the open shader drawn live although its conditions (climate, time, a graphics setting) hide it
    private double ForceRow(HudUi ui, double x, double y, double bh)
    {
        var program = host.Active is { } document && ShaderPaths.IsStage(document.Path)
            ? Path.GetFileNameWithoutExtension(document.Path)
            : null;
        if (program is null) return y + ui.Px(2);

        if (!LiveForce.Can(program))
        {
            // nothing hides it: the game draws it whenever its object is on screen
            ui.Text(x + ui.Px(2), y, bh, ui.F.Small, ui.Fit(ui.F.Small, L("force-always"), _w - ui.Px(12)), HudUi.Faint);
            host.Tip(x, y, _w, bh, L("tip-force-always"));
            return y + bh + ui.Px(8);
        }

        var (off, on) = (L("force-off"), L("force-on"));
        var (fw, nw) = (ui.TextW(ui.F.Small, off) + ui.Px(16), ui.TextW(ui.F.Small, on) + ui.Px(16));
        var sx = _x + _w - ui.Px(6) - fw - nw;
        var forced = LiveForce.IsForced(program);
        ui.Text(x + ui.Px(2), y, bh, ui.F.Small, ui.Fit(ui.F.Small, L("force-live"), Math.Max(ui.Px(20), sx - x - ui.Px(8))),
            HudUi.Soft);
        host.Tip(x, y, sx - x, bh, L("tip-force-live"));
        Tool(ui, sx, y, fw, bh, off, !forced, true, () => LiveForce.Set(program, false), L("tip-force-live"));
        Tool(ui, sx + fw, y, nw, bh, on, forced, true, () => LiveForce.Set(program, true), L("tip-force-live"));
        return y + bh + ui.Px(8);
    }

    private readonly PeekRegion _peek = new(host);

    // What the active pack and its passes that are on declare, as the menu's rows; a pass switched off takes its rows
    // away (their values stay)
    private double Settings(HudUi ui, MenuControls menu, double y)
    {
        if (_page is not { } page)
        {
            var note = L("no-active-shader");
            ui.Text(_x + ui.Px(14), y, ui.F.BodyRow, ui.F.Small, ui.Fit(ui.F.Small, note, _w - ui.Px(28)), HudUi.Faint);
            return y + ui.F.BodyRow + ui.Px(6);
        }

        for (var i = 0; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
        {
            var row = page[i];
            if (row.Kind == OptionKind.Group ? i > 0 && GroupHasRows(page, i) : Shows(row)) y = Setting(menu, y, row);
        }

        return y + ui.Px(6);
    }

    private bool Shows(OptionRow row) =>
        row.Kind is OptionKind.Switch or OptionKind.Choice or OptionKind.Slider &&
               Model.Shows(_keyOf.GetValueOrDefault(row.Label) ?? "");

    // A group's heading only over rows that show (an empty LOOK heading stays away)
    private bool GroupHasRows(OptionPage page, int group)
    {
        for (var i = group + 1; i < Math.Min(page.Count, OptionPage.MaxRows); i++)
        {
            if (page[i].Kind == OptionKind.Group) return false;

            if (Shows(page[i])) return true;
        }

        return false;
    }

    // A wheel's arrows turn it back and on, the rest of the row on
    private void WheelHits(double left, double right, double y, Action<int> turn, bool wide = true)
    {
        var (box, h) = (S(MenuControls.ArrowBox), S(RowHeight));
        Click(wide ? _x : left, y, wide ? _w : right - left, h, () => turn(1));
        Click(left, y, box, h, () => turn(-1));
        Click(right - box, y, box, h, () => turn(1));
    }
}
