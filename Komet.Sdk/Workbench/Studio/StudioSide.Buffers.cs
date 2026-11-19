using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The preview's render targets: what it shows (a grouped list), the channel, the shadow contrast, the vanilla split, the
// enlarged view over the editor (Esc or a double-click closes). A click in the picture inspects (the Inspector tab opens,
// the pixel stays ringed), a double-click enlarges, a drag moves the split's divider.
internal sealed class PreviewRegion(ShaderIde host) : IIdeRegion
{
    private const long DoubleMs = 400;

    private (double X, double Y, double W, double H) _box;
    private double _downX, _lastX, _lastY;
    private bool _dragged;
    private long _upAt;

    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys)
    {
        (_downX, _lastX, _lastY, _dragged) = (x, x, y, false);
        return button == EnumMouseButton.Left;
    }

    public void Drag(double x, double y)
    {
        if (host.Model3D.On && !host.LiveModel)
        {
            _dragged |= Math.Abs(x - _downX) + Math.Abs(y - _lastY) > 3;
            host.Model3D.Orbit(_lastX - x, y - _lastY); // the shape follows the hand
            (_lastX, _lastY) = (x, y);
            return;
        }

        if (host.Buffers.Split >= 0 && Math.Abs(x - _downX) > 3)
        {
            _dragged = true;
            host.Buffers.Split = Math.Clamp((x - _box.X) / Math.Max(1, _box.W), 0.02, 0.98);
            host.Redraw();
        }
    }

    public bool Wheel(double x, int lines)
    {
        if (host.Model3D.On && !host.LiveModel && (host.Buffers.Large || host.CtrlDown))
        {
            host.Model3D.Zoom(lines); // enlarged, or with Ctrl in the column
            return true;
        }

        return !host.Buffers.Large && host.Side.Wheel(x, lines); // over the side picture the column scrolls
    }

    public void Up()
    {
        if (_dragged || _box.W <= 0) return;

        var now = Environment.TickCount64;
        if (now - _upAt < DoubleMs)
        {
            host.Buffers.Large = !host.Buffers.Large;
            _upAt = 0;
        }
        else
        {
            _upAt = now;
            if (host.Model3D.On)
            {
                return; // nothing to inspect: the picture is the view's own
            }

            host.Buffers.Ask((host.MouseX - _box.X) / _box.W, (host.MouseY - _box.Y) / _box.H);
            host.Panel.ShowInspector();
        }

        host.Redraw();
    }

    public void At((double X, double Y, double W, double H) box) => _box = box;
}

// Takes clicks so nothing under the enlarged preview gets them
internal sealed class SwallowRegion : IIdeRegion
{
    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys) => true;
}

internal sealed partial class StudioSide
{
    private static readonly string[] ChannelNames = ["All", "R", "G", "B", "A"];
    private readonly SwallowRegion _swallow = new();
    private PreviewRegion? _previewRegion;

    private Action? _shrink;

    private double Picture(HudUi ui, double y)
    {
        var aspect = host.Api.Render.FrameWidth > 0
            ? (double)host.Api.Render.FrameHeight / host.Api.Render.FrameWidth
            : 0.5625;
        var (bx, bw) = (_x + ui.Px(6), _w - ui.Px(12));
        var bh = bw * aspect;
        if (host.Buffers.Large) return LargeStrip(ui, bx, y, bw); // the picture is over the editor: no empty box here

        var whole = y >= _top && y + bh <= _bottom;
        var missing = PictureMissing();
        if (!host.Buffers.Large)
        {
            host.Buffers.DisplayWidth = bw;
            host.SetLive(whole && missing.Length == 0 ? (bx, y, bw, bh) : null);
            PictureHits(bx, y, bw, bh, whole);
        }

        ui.Fill(bx, y, bw, bh, HudUi.Track with { A = 0.04 });
        TextField.Frame(ui, bx, y, bw, bh, HudUi.Divider);
        var why = host.Buffers.Large ? "buffer-enlarged" : "live-preview-off";
        var off = host.Live is null ? L(why) : "";
        var note = missing.Length > 0 ? missing : off;
        if (note.Length > 0)
        {
            var text = ui.Fit(ui.F.Small, note, bw - ui.Px(20));
            ui.Text(bx + (bw - ui.TextW(ui.F.Small, text)) / 2, y + (bh - ui.F.SmallRow) / 2, ui.F.SmallRow, ui.F.Small,
                text,
                HudUi.Faint);
        }

        if (host.AutoModelShown)
        {
            var text = ui.Fit(ui.F.Small, L("model-auto-note"), bw - ui.Px(16));
            ui.Fill(bx, y + bh - ui.F.SmallRow - ui.Px(4), bw, ui.F.SmallRow + ui.Px(4), HudUi.Track with { A = 0.5 });
            ui.Text(bx + ui.Px(8), y + bh - ui.F.SmallRow - ui.Px(2), ui.F.SmallRow, ui.F.Small, text, HudUi.Soft);
        }

        return y + bh + ui.Px(8);
    }

    // While the preview is enlarged: one line saying so, a click brings it back into the column
    private double LargeStrip(HudUi ui, double x, double y, double w)
    {
        var h = ui.Px(30);
        var hot = host.Hot(x, y, w, h);
        ui.Fill(x, y, w, h, HudUi.Track with { A = hot ? 0.12 : 0.06 });
        TextField.Frame(ui, x, y, w, h, HudUi.Divider);
        ui.Text(x + ui.Px(10), y, h, ui.F.Small, ui.Fit(ui.F.Small, L("buffer-enlarged-back"), w - ui.Px(20)),
            HudUi.Soft);
        _shrink ??= () => host.Buffers.Large = false;
        Click(x, y, w, h, _shrink);
        return y + h + ui.Px(8);
    }

    // What the object's keys do, or the change going on now
    private string TransformHint()
    {
        var model = host.Model3D;
        if (!model.On || host.LiveModel) return "";

        return "  ·  " + model.Transforming switch
        {
            TransformMode.None => L("model-transform-keys"),
            var mode => L("model-transforming", L("model-" + mode.ToString().ToLowerInvariant()),
                model.Axis.Length > 0 ? model.Axis : L("model-axis-free"))
        };
    }

    private void PictureHits(double x, double y, double w, double h, bool whole)
    {
        if (!whole) return;

        _previewRegion ??= new PreviewRegion(host);
        _previewRegion.At((x, y, w, h));
        host.Hits.Region(x, y, w, h, _previewRegion);
        var tip = host.Model3D.On ? "tip-model-orbit" : "tip-buffer-inspect";
        host.Tip(x, y, w, h, L(host.LiveModel ? "tip-model-live" : tip));
    }

    // Why the picture's place is empty: the target's missing setting, or the 3D view's missing or failed shader
    private string PictureMissing()
    {
        if (host.LiveModel) return "";

        return host.Model3D.On ? host.Model3D.Note : BufferView.Missing(host.Buffers.Shown);
    }

    // The enlarged preview over the editor: the picture at the largest size the area holds, its own hits
    public void DrawLarge(HudUi ui, double x, double y, double w, double h)
    {
        if (!host.Buffers.Large) return;

        var aspect = host.Api.Render.FrameWidth > 0
            ? (double)host.Api.Render.FrameHeight / host.Api.Render.FrameWidth
            : 0.5625;
        var pw = Math.Min(w - ui.Px(24), (h - ui.Px(48)) / aspect);
        var ph = pw * aspect;
        var (px, py) = (x + (w - pw) / 2, y + ui.Px(36));
        ui.Fill(x, y, w, h, host.Ground);
        host.Hits.Region(x, y, w, h, _swallow); // the editor under it takes no click
        var modelTitle = host.LiveModel ? "model-live-scene" : "model-title";
        var title = host.Model3D.On ? L(modelTitle) : host.Buffers.Shown.Name;
        var hint = host.Model3D.On ? "model-esc-closes" : "buffer-esc-closes";
        ui.Text(x + ui.Px(12), y, ui.Px(32), ui.F.Body, title + "  ·  " + L(hint) + TransformHint(), HudUi.Soft);
        host.Buffers.DisplayWidth = pw;
        var missing = PictureMissing();
        host.SetLive(missing.Length == 0 ? (px, py, pw, ph) : null);
        if (missing.Length > 0)
        {
            ui.Text(px + ui.Px(12), py + ph / 2, ui.F.BodyRow, ui.F.Small, ui.Fit(ui.F.Small, missing, pw - ui.Px(24)),
                HudUi.Faint);
        }

        _previewRegion ??= new PreviewRegion(host);
        _previewRegion.At((px, py, pw, ph));
        host.Hits.Region(px, py, pw, ph, _previewRegion);
    }

    // The toolbar over the picture: the target (a grouped list), the channel (All R G B A), the shadow contrast while a
    // shadow map shows, the vanilla split and enlarge as icons
    private double Toolbar(HudUi ui, double y)
    {
        var view = host.Buffers;
        var h = ui.Px(26);
        var (x, right) = (_x + ui.Px(6), _x + _w - ui.Px(6));
        var b = ui.Px(24);
        right -= b;
        Tool(ui, right, y, b, h, "", view.Large, true, () => view.Large = !view.Large, L("tip-buffer-enlarge"));
        StudioIcons.Enlarge(ui, right + b / 2, y + h / 2, ui.Px(12), view.Large ? Rgba.White(1) : HudUi.Soft);
        var model = host.Model3D;
        var mw = ui.TextW(ui.F.Small, "3D") + ui.Px(12);
        right -= mw + ui.Px(2);
        Tool(ui, right, y, mw, h, "3D", model.On, true, host.ToggleModel, L("tip-model-3d"));
        if (model.On) return ModelBar(ui, x, right - ui.Px(8), y, h);

        right -= b + ui.Px(2);
        var split = view.Split >= 0 && view.Snapshot == view.Shown.Name;
        Tool(ui, right, y, b, h, "", split || host.TakingSnapshot, host.Project is not null, ToggleSplit,
            host.TakingSnapshot ? L("buffer-snapshot-taking") : L("tip-buffer-split"));
        StudioIcons.Split(ui, right + b / 2, y + h / 2, ui.Px(12), split ? Rgba.White(1) : HudUi.Soft, split);
        right -= ui.Px(8);
        if (view.Shown.Kind == BufferKind.Shadow)
        {
            var c = view.Contrast;
            var text = "×" + BufferView.Contrasts[c];
            var cw = ui.TextW(ui.F.Small, text) + ui.Px(12);
            right -= cw;
            Tool(ui, right, y, cw, h, text, false, true,
                () => view.Contrast = (c + 1) % BufferView.Contrasts.Length, L("tip-buffer-contrast"));
            right -= ui.Px(6);
        }

        right = Channels(ui, right, y, h);
        var dw = right - ui.Px(6) - x;
        if (dw < ui.Px(120))
        {
            (dw, y) = (_w - ui.Px(12), y + h + ui.Px(4)); // too narrow beside the buttons: the target gets its own row
        }

        var ty = y;
        Tool(ui, x, y, dw, h, "", false, true, () => host.Dropdown(x, ty + h, TargetList),
            view.Shown.Name + " – " + L("tip-buffer-target"));
        ui.Text(x + ui.Px(8), y, h, ui.F.Small, ui.Fit(ui.F.Small, view.Shown.Name, dw - ui.Px(26)), Rgba.White(1));
        StudioIcons.Triangle(ui, x + dw - ui.Px(10), y + h / 2, ui.Px(7), false, HudUi.Soft);
        return y + h + ui.Px(6);
    }

    // The 3D view's toolbar: the shape as a list (the target and channels are the game picture's)
    private double ModelBar(HudUi ui, double x, double right, double y, double h)
    {
        var dw = right - x;
        var sceneOnly = host is { LiveModel: false, Model3D.SceneOnly: true };
        var shape = sceneOnly ? 0 : Math.Clamp(host.Model3D.Shape, 0, ModelView.ShapeCount - 1);
        var name = L(host.LiveModel ? "model-live-scene" : "model-shape-" + shape);
        if (host.PreviewIncludeOwner is { } owner) name += " · " + owner;
        var tip = L(host.LiveModel ? "tip-model-live" : "tip-model-shape");
        var canChoose = !sceneOnly || host.HasEngineScene;
        Tool(ui, x, y, dw, h, "", false, canChoose, () => host.Dropdown(x, y + h, ShapeList), tip);
        ui.Text(x + ui.Px(8), y, h, ui.F.Small, ui.Fit(ui.F.Small, name, dw - ui.Px(26)), Rgba.White(1));
        if (canChoose) StudioIcons.Triangle(ui, x + dw - ui.Px(10), y + h / 2, ui.Px(7), false, HudUi.Soft);
        return y + h + ui.Px(6);
    }

    private void ShapeList(ContextMenu list)
    {
        var shapes = host.Model3D.SceneOnly ? 1 : ModelView.ShapeCount;
        for (var i = 0; i < Math.Min(shapes, ModelView.ShapeCount); i++)
        {
            var index = i;
            list.Add(L("model-shape-" + i), () =>
            {
                host.Model3D.Shape = index;
                host.Redraw();
            });
        }

        if (host.HasEngineScene)
        {
            list.Add(L("model-live-scene"), () =>
            {
                host.Model3D.Shape = ModelView.ShapeCount;
                host.Redraw();
            });
        }
    }

    // All R G B A as small toggles, right to left from right; the left edge they leave
    private double Channels(HudUi ui, double right, double y, double h)
    {
        var view = host.Buffers;
        for (var k = 0; k < Math.Min(ChannelNames.Length, 5); k++)
        {
            var i = ChannelNames.Length - 1 - k;
            var name = ChannelNames[i];
            var bw = ui.TextW(ui.F.Small, name) + ui.Px(10);
            right -= bw;
            var channel = i - 1;
            Tool(ui, right, y, bw, h, name, view.Channel == channel, true, () => view.Channel = channel,
                L(i == 0 ? "tip-buffer-channel-all" : "tip-buffer-channel"));
        }

        return right;
    }

    // A toolbar button: lit while on, a frame, its text centred (an icon is drawn by the caller)
    private void Tool(HudUi ui, double x, double y, double w, double h, string text, bool on, bool enabled,
        Action click,
        string tip)
    {
        var hot = enabled && host.Hot(x, y, w, h);
        var ground = HudUi.Track with { A = hot ? 0.12 : 0.04 };
        ui.Fill(x, y, w, h, on ? HudUi.Accent with { A = 0.45 } : ground, ui.Px(3));
        if (text.Length > 0)
        {
            var ink = enabled ? HudUi.Soft : HudUi.Faint;
            ink = on ? Rgba.White(1) : ink;
            ui.Text(x + (w - ui.TextW(ui.F.Small, text)) / 2, y, h, ui.F.Small, text, ink);
        }

        host.Tip(x, y, w, h, tip);
        if (enabled) Click(x, y, w, h, click);
    }

    private void ToggleSplit()
    {
        var view = host.Buffers;
        if (view.Split >= 0 && view.Snapshot == view.Shown.Name)
        {
            view.Split = -1;
            view.ForgetSnapshot();
        }
        else
        {
            host.CompareWithVanilla();
        }
    }

    private void TargetList(ContextMenu list)
    {
        string? group = null;
        for (var i = 0; i < Math.Min(BufferView.Targets.Length, BufferView.MaxTargets); i++)
        {
            var target = BufferView.Targets[i];
            if (target.Group != group)
            {
                group = target.Group;
                list.Header(Caps(group));
            }

            var index = i;
            var why = BufferView.Missing(target);
            list.Add(target.Name + (why.Length > 0 ? "  (" + L("buffer-not-available") + ")" : ""), () =>
            {
                host.Buffers.Selected = index;
                host.Redraw();
            });
        }
    }
}
