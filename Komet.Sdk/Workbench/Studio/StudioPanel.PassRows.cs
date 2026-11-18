using Komet.Core;
using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The Passes tab's rows and what they do
internal sealed partial class StudioPanel
{
    private void PassLine(HudUi ui, double x, double y, double w, PassRow row)
    {
        var h = _passRowH;
        if (row.Pass is not { } pass)
        {
            FoldLine(ui, x, y, w, h, row);
            return;
        }

        if (pass.Id == _marked)
        {
            ui.Fill(x, y, w, h, HudUi.Link with { A = 0.16 });
        }
        else if (host.Hot(x, y, w, h))
        {
            ui.Fill(x, y, w, h, HudUi.Track with { A = 0.045 });
        }

        if (_dragId == pass.Id) ui.Fill(x, y + _dragTo - _dragFrom, w, Math.Max(1, ui.Px(2)), HudUi.Link);

        Handle(ui, x + ui.Px(16), y, h, pass);
        var timeW = ui.Px(90);
        var switchW = Math.Min(ui.Px(300), w * 0.35);
        var nameLeft = x + ui.Px(40);
        var nameW = Math.Max(ui.Px(40), w - (nameLeft - x) - switchW - timeW - ui.Px(32));
        PassName(ui, (nameLeft, y, nameW, h), pass);
        PassSwitch(ui, (nameLeft + nameW + ui.Px(16), y, switchW, h), pass);
        var dear = Dearest(pass);
        ui.TextRight(x + w - ui.Px(16), y, h, ui.F.Mono, Ms(pass.Ms), dear ? StudioColors.Pending : HudUi.Faint);
    }

    // A stage head (a click folds its vanilla passes) or the "N vanilla passes unchanged" fold
    private void FoldLine(HudUi ui, double x, double y, double w, double h, PassRow row)
    {
        var open = _unfolded.Contains(row.Stage);
        var stage = row.Stage;
        if (host.Hot(x, y, w, h)) ui.Fill(x, y, w, h, HudUi.Track with { A = 0.045 });

        if (row.Head)
        {
            StudioIcons.Twist(ui, x + ui.Px(16), y + h / 2, ui.Px(12), open || row.Vanilla == 0, HudUi.Faint);
            var title = Caps(StudioSide.StageTitle(stage));
            var tw = ui.Spaced(x + ui.Px(30), y, h, ui.F.Small, title, ui.Px(2), HudUi.Soft);
            if (row.Vanilla > 0)
            {
                ui.Text(x + ui.Px(44) + tw, y, h, ui.F.Small, L("vanilla-unchanged", row.Vanilla), HudUi.Faint);
            }
        }
        else
        {
            var text = open ? L("vanilla-hide") : L("vanilla-unchanged", row.Vanilla);
            ui.Text(x + ui.Px(40), y, h, ui.F.Small, text, HudUi.Faint);
        }

        host.Hits.Add(x, y, w, h, () => Fold(stage));
    }

    // The name in full; cut, its row shows it whole in a tip while the mouse is on it
    private void PassName(HudUi ui, (double X, double Y, double W, double H) at, PassEntry pass)
    {
        var name = ui.Fit(ui.F.Body, pass.Name, at.W);
        var ink = pass.Source == PassSource.Vanilla ? HudUi.Soft : Rgba.White(1);
        ui.Text(at.X, at.Y, at.H, ui.F.Body, name, ink);
        if (pass.Failed) ui.Disc(at.X - ui.Px(8), at.Y + at.H / 2, ui.Px(3), HudUi.Err);

        var file = pass.File;
        host.Hits.Add(at.X, at.Y, at.W, at.H, () => OpenPass(pass, file));
        if (name != pass.Name && host.Hot(at.X, at.Y, at.W, at.H))
        {
            var tw = ui.TextW(ui.F.Small, pass.Name) + ui.Px(12);
            ui.Fill(at.X, at.Y - at.H, tw, at.H, host.Ground);
            TextField.Frame(ui, at.X, at.Y - at.H, tw, at.H, HudUi.Divider);
            ui.Text(at.X + ui.Px(6), at.Y - at.H, at.H, ui.F.Small, pass.Name, Rgba.White(1));
        }
    }

    private void OpenPass(PassEntry pass, string? file)
    {
        _marked = pass.Id;
        if (file is not null)
        {
            host.Open(file);
            return;
        }

        host.Toast(pass.Name + ": " + L("no-shader-file"));
    }

    // Override on/off (with a failed build's marker and a pick between projects), "Create override", or a pack pass's
    // own switch
    private void PassSwitch(HudUi ui, (double X, double Y, double W, double H) at, PassEntry pass)
    {
        var menu = host.Controls;
        var cy = at.Y + at.H / 2;
        if (pass.Source == PassSource.Pack)
        {
            if (pass.When is { } when && menu is not null && host.Side.RowOf(when) is { } setting)
            {
                menu.PillOnly(at.X, cy, pass.On, menu.Text, true);
                host.Hits.Add(at.X, at.Y, at.W * 0.4, at.H, () => SetPackPass(setting, pass));
            }

            return;
        }

        if (!pass.HasOverride)
        {
            if (pass.GameFile is { } game && host.Project is not null)
            {
                var text = L("create-override");
                ui.Text(at.X, at.Y, at.H, ui.F.Small, text, HudUi.Link);
                host.Hits.Add(at.X, at.Y, ui.TextW(ui.F.Small, text), at.H, () => CreateOverride(pass, game));
            }

            return;
        }

        var x = at.X;
        if (menu is not null)
        {
            menu.PillOnly(x, cy, pass.OverrideOn, menu.Text, true);
            host.Hits.Add(x, at.Y, ui.Px(56), at.H, () => SetOverride(pass, !pass.OverrideOn));
            x += ui.Px(64);
        }

        if (pass.Failed)
        {
            ui.Text(x, at.Y, at.H, ui.F.Small, L("override-failed"), HudUi.Err);
            x += ui.TextW(ui.F.Small, L("override-failed")) + ui.Px(10);
        }

        Pick(ui, (x, at.Y, at.X + at.W - x, at.H), pass);
    }

    // Several active projects copy the pass: a click turns to the next one's copy
    private void Pick(HudUi ui, (double X, double Y, double W, double H) at, PassEntry pass)
    {
        if (pass.Candidates.Length < 2 || at.W <= 0) return;

        var current = pass.File is { } file ? StudioLink.ProjectOf(file) : null;
        var index = Math.Max(0, Array.IndexOf(pass.Candidates, current));
        var text = ui.Fit(ui.F.Small, pass.Candidates[index] + " ▾", at.W);
        ui.Text(at.X, at.Y, at.H, ui.F.Small, text, HudUi.Soft);
        var next = pass.Candidates[(index + 1) % pass.Candidates.Length];
        host.Hits.Add(at.X, at.Y, ui.TextW(ui.F.Small, text), at.H, () =>
        {
            host.Link.Pick(pass.Id, next);
            host.AddLog(pass.Name + " → " + next);
            host.Side.Refresh();
        });
    }

    private void SetOverride(PassEntry pass, bool on)
    {
        if (!host.Link.SetOverrides([pass.Id], on))
        {
            host.Toast(L("slot-refused") + (StudioLink.ModulesError.Length > 0 ? ": " + StudioLink.ModulesError : ""));
            return;
        }

        host.AddLog(pass.Name + ": " + L(on ? "override-on" : "override-off"));
        host.Side.Refresh();
    }

    private void CreateOverride(PassEntry pass, string game)
    {
        host.CopyIn(game);
        _ = host.Link.SetOverrides([pass.Id], true);
        host.Side.Refresh();
    }

    private void SetPackPass(OptionRow setting, PassEntry pass)
    {
        setting.Set(pass.On ? 0 : 1);
        host.AddLog(pass.Name + ": " + setting.ValueText(setting.Get()));
        host.Side.RebuildModel();
    }

    private bool Dearest(PassEntry pass)
    {
        if (pass.Ms is not { } ms || ms < TinyMs) return false;

        var dearer = 0;
        foreach (var other in host.Side.Model.Passes.Bounded(PassModel.MaxPasses))
        {
            dearer += other.Ms > ms ? 1 : 0;
        }

        return dearer < 3;
    }

    // The handle: a drag moves the pass within its stage (the engine's modules; post steps and pack passes stay)
    private void Handle(HudUi ui, double x, double y, double h, PassEntry pass)
    {
        if (pass.Source == PassSource.Pack || pass.Stage == ShaderModules.PostStage) return;

        for (var i = 0; i < 3; i++)
        {
            ui.Fill(x, y + h / 2 - ui.Px(4) + i * ui.Px(4), ui.Px(10), Math.Max(1, ui.Px(1)), HudUi.Faint);
        }

        var id = pass.Id;
        if (!_drags.TryGetValue(id, out var drag))
        {
            drag = new DragRegion((_, my) => Dragging(id, my), Dropped);
            _drags[id] = drag;
        }

        host.Hits.Region(x - ui.Px(4), y, ui.Px(18), h, drag);
    }

    private void Dragging(string id, double y)
    {
        if (_dragId != id) (_dragId, _dragFrom) = (id, y);

        _dragTo = y;
        host.Redraw();
    }

    // Dropped on another pass of the same stage: the steps between their orders, one Move each
    private void Dropped()
    {
        if (_dragId is not { } id || host.Side.Model.Find(id) is not { } pass || _passRowH <= 0)
        {
            _dragId = null;
            return;
        }

        _dragId = null;
        var index = _passFirst + (int)((_dragTo - _passTop) / _passRowH);
        if (index < 0 || index >= _passRows.Count || _passRows[index].Pass is not { } target || target.Stage != pass.Stage ||
            target.Id == id)
        {
            return;
        }

        var steps = target.Order - pass.Order;
        var direction = Math.Sign(steps);
        for (var i = 0; i < Math.Min(Math.Abs(steps), PassModel.MaxPasses); i++)
        {
            if (!host.Link.MoveModule(id, direction, host.Project))
            {
                host.Toast(L("order-refused"));
                break;
            }
        }

        host.AddLog(L("frame") + ": " + pass.Name + (direction < 0 ? " ↑" : " ↓"));
        host.Side.Refresh();
    }
}
