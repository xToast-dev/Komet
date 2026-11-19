using Komet.Core;
using Komet.Hud;
using Komet.Options;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Held: the screen hides and the game shows
internal sealed class PeekRegion(ShaderIde host) : IIdeRegion
{
    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        host.Peeking = button == EnumMouseButton.Left;
        return host.Peeking;
    }

    public void Up() => host.Peeking = false;

    public bool Wheel(double x, int lines) =>
        host.Side.Wheel(x, lines); // the column scrolls as a whole, over the button too
}

// A setting's rail dragged: the knob follows, the value lands in the #define on release
internal sealed class RailRegion(StudioSide side) : IIdeRegion
{
    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        side.RailDrag(x);
        return true;
    }

    public void Drag(double x, double y) => side.RailDrag(x);

    public void Up() => side.RailDrop();
}

// A setting's row and the Frame section
internal sealed partial class StudioSide
{
    private readonly List<(double Y, double From, double W, OptionRow Row)> _railRows = [];

    // A row of the pack's page as the menu draws it: a group, a pill, a wheel or a rail (buttons and keys are left out)
    private double Setting(MenuControls menu, double y, OptionRow row)
    {
        if (row.Kind == OptionKind.Group) return Group(menu, y, row.Label);

        if (row.Kind is not (OptionKind.Switch or OptionKind.Choice or OptionKind.Slider)) return y;

        var h = S(RowHeight);
        var enabled = row.IsEnabled;
        var value = ReferenceEquals(_dragRow, row) ? _dragValue : row.Get();
        var (left, right) = Row(menu, y, row.Label, enabled);
        switch (row.Kind)
        {
            case OptionKind.Switch:
                menu.Pill(left, right, y, h, value >= 0.5, menu.Text, enabled);
                Click(_x, y, _w, h, () => Set(row, value >= 0.5 ? 0 : 1));
                break;
            case OptionKind.Choice when row.Names.Length > 0:
                var offered = Math.Max(1, Math.Min(row.Names.Length, (int)row.Max + 1));
                var index = Math.Clamp((int)Math.Round(value), 0, row.Names.Length - 1);
                menu.Wheel(left, right, y, h, row.Names[index], index, offered, menu.Text, enabled);
                WheelHits(left, right, y, back => Set(row, (index + back + offered) % offered));
                break;
            case OptionKind.Slider when row.Max > row.Min && row.Step > 0:
                Slider(menu, (left, right, y), row, value, enabled);
                break;
        }

        return y + h;
    }

    // A pack value set as the menu sets it (a define recompiles, a uniform follows at once)
    private void Set(OptionRow row, double value)
    {
        if (!row.IsEnabled) return;

        row.Set(row.Kind == OptionKind.Slider ? Math.Clamp(value, row.Min, row.Max) : value);
        host.AddLog(row.Label + ": " + row.ValueText(row.Get()));
        host.Redraw();
    }

    private void Slider(MenuControls menu, (double Left, double Right, double Y) at, OptionRow row, double value,
        bool enabled)
    {
        var fraction = Math.Clamp((value - row.Min) / (row.Max - row.Min), 0, 1);
        var standard = row.Standard;
        var mark = double.IsFinite(standard) && Math.Abs(standard - value) > 1e-9
            ? Math.Clamp((standard - row.Min) / (row.Max - row.Min), 0, 1)
            : double.NaN;
        var h = S(RowHeight);
        var (from, w, end) = menu.Rail(at.Left, at.Right, at.Y, h, row.ValueText(value),
            (fraction, mark, (int)Math.Round((row.Max - row.Min) / row.Step)), menu.Text, enabled,
            ReferenceEquals(_dragRow, row));
        var box = S(MenuControls.ArrowBox);
        Click(at.Left, at.Y, box, h, () => Set(row, value - row.Step));
        Click(end - box, at.Y, box, h, () => Set(row, value + row.Step));
        if (enabled && w > 0 && at.Y >= _top && at.Y + h <= _bottom)
        {
            host.Hits.Region(from, at.Y, w, h, _rail!);
            _railRows.Add((at.Y, from, w, row));
        }
    }

    public void RailDrag(double x)
    {
        if (_dragRow is null)
        {
            var y = host.MouseY;
            foreach (var rail in _railRows.Bounded(64))
            {
                if (y >= rail.Y && y < rail.Y + S(RowHeight))
                {
                    (_dragRow, _railFrom, _railW) = (rail.Row, rail.From, rail.W);
                }
            }
        }

        if (_dragRow is not { } row || _railW <= 0) return;

        _dragValue = row.At((x - _railFrom) / _railW);
        host.Redraw();
    }

    public void RailDrop()
    {
        if (_dragRow is { } row) Set(row, _dragValue);

        _dragRow = null;
    }

    // The total in the head, the stacked bar of the passes here: hover names a pass and its time, a click shows it in
    // the Passes tab
    private double Frame(HudUi ui, MenuControls menu, double y)
    {
        if (Modules.Length == 0)
        {
            ui.Text(_x + ui.Px(14), y, ui.F.BodyRow, ui.F.Small, ui.Fit(ui.F.Small, L("no-modules"), _w - ui.Px(28)),
                HudUi.Faint);
            return y + ui.F.BodyRow + ui.Px(8);
        }

        _ = menu;
        return Bar(ui, y);
    }

    private double Bar(HudUi ui, double y)
    {
        var (x, w, h) = (_x + ui.Px(14), _w - ui.Px(28), ui.Px(20));
        var total = 0.0;
        foreach (var pass in Model.Passes.Bounded(PassModel.MaxPasses))
        {
            total += pass.Ms ?? 0;
        }

        var at = x;
        var stage = -1;
        string? name = null;
        PassEntry? hovered = null;
        foreach (var pass in Model.Passes.Bounded(PassModel.MaxPasses))
        {
            if (pass.Stage != name) (name, stage) = (pass.Stage, stage + 1);

            if (pass.Ms is not { } ms || ms <= 0 || total <= 0) continue;

            var part = Math.Max(ui.Px(2), w * ms / total - 1);
            var span = Math.Min(part, x + w - at);
            var hot = host.Hot(at, y, span + 1, h + ui.Px(2));
            var alpha = hot ? 0.9 : 0.55;
            var color = pass.Source == PassSource.Override
                ? HudUi.Link
                : StudioColors.Stages[stage % StudioColors.Stages.Length] with { A = alpha };
            ui.Fill(at, y + ui.Px(2), span, h, color);
            var id = pass.Id;
            Click(at, y, span + 1, h + ui.Px(2), () => host.Left.ShowPass(id));
            hovered = hot ? pass : hovered;
            at += part + 1;
        }

        y += h + ui.Px(6);
        var tip = hovered is { } p ? p.Name + "  " + StudioPanel.Ms(p.Ms) : "";
        ui.Text(x, y, ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, tip, w), HudUi.Soft);
        return y + ui.F.SmallRow + ui.Px(6);
    }

    // The engine's stage ids as players read them
    internal static string StageTitle(string stage) =>
        stage switch
        {
            "Before" => L("stage-before"),
            "ShadowFar" or "ShadowFarDone" => L("stage-shadow-far"),
            "ShadowNear" or "ShadowNearDone" => L("stage-shadow-near"),
            "Opaque" => L("stage-opaque"),
            "OIT" => L("stage-oit"),
            "AfterOIT" => L("stage-after-oit"),
            ShaderModules.PostStage => L("stage-post"),
            "AfterPostProcessing" or "AfterFinalComposition" => L("stage-final"),
            "AfterBlit" => L("stage-after-blit"),
            "Ortho" => L("stage-ortho"),
            _ => stage
        };
}
