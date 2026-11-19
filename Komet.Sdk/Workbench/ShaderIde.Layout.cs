using System.Globalization;
using Komet.Hud;
using Komet.Options;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// What the left column shows, picked on the activity strip at the screen's left edge
internal enum LeftView
{
    Files,
    Search,
    Passes
}

// A region that follows a press: the splits between the columns and over the bottom panel
internal sealed class DragRegion(Action<double, double> drag, Action? up = null) : IIdeRegion
{
    public bool Down(double x, double y, EnumMouseButton button, IdeKeys keys) => button == EnumMouseButton.Left;

    public void Drag(double x, double y) => drag(x, y);

    public void Up() => up?.Invoke();
}

// The studio's layout over the whole screen: the top bar, the activity strip | the left view (files, search, passes) |
// editor (with the bottom panel) | right column, then the popups over everything (context menu, history, palette, modal, toast)
internal sealed partial class ShaderIde
{
    private const double TopHeight = 46, LeftWidth = 280, StripWidth = 44, MinRight = 240, MaxRight = 560;
    private DragRegion? _rightSplit;
    private Rgba _statusColor;
    private string _statusText = "";

    private (LeftView View, string Tip, Action Click)[]? _strip;

    private HudUi? _ui;
    private double _width;
    public double Height { get; private set; }

    // The menu's colours as the studio's grounds
    public Rgba Ground => Darker(BandGround);

    public Rgba PanelGround => StudioColors.Opaque(Controls?.Style["panel"] ?? HudUi.PanelBack);
    public Rgba BandGround => StudioColors.Opaque(Controls?.Style["band"] ?? HudUi.Band);

    public void Draw(HudUi ui)
    {
        _ui = ui;
        (_width, Height) = (ui.W, ui.Bottom);
        Hits.Clear();
        Diagnostics.Show(Active);
        (_statusText, _statusColor) = State();
        Live = null; // the side column sets it again when the preview is on screen
        ui.Fill(ui.X, 0, ui.W, Height, Ground);
        var top = ui.Px(TopHeight);
        TopBar(ui, top);
        var strip = ui.Px(StripWidth);
        var left = strip + (Prefs.LeftShown ? Math.Min(ui.Px(LeftWidth + LeftExtra()), ui.W * 0.3) : 0);
        var right = Math.Clamp(ui.Px(Prefs.Right), ui.Px(MinRight), Math.Min(ui.Px(MaxRight), ui.W * 0.4));
        Strip(ui, ui.X, top, strip, Height - top);
        if (Prefs.LeftShown) LeftPane(ui, ui.X + strip, top, left - strip, Height - top);
        var centerX = ui.X + left;
        var centerW = ui.W - left - right;
        EditorArea(ui, centerX, top, centerW, Height - top);
        Side.Draw(ui, ui.X + ui.W - right, top, right, Height - top);
        Side.DrawLarge(ui, centerX, top, centerW, Height - top - PanelHeight);
        _rightSplit ??= new DragRegion((x, _) => Prefs.Right = Math.Clamp((_width - x) / Math.Max(0.1, ui.Px(1)),
            MinRight, MaxRight));
        Hits.Region(ui.X + ui.W - right - ui.Px(3), top, ui.Px(6), Height - top, _rightSplit);
        Overlays(ui);
    }

    private static Rgba Darker(Rgba color) => new(color.R * 0.8, color.G * 0.8, color.B * 0.8, 1);

    // The activity strip: a button a view; the lit one's click folds the left column away, a click brings it back
    private void Strip(HudUi ui, double x, double y, double w, double h)
    {
        _strip ??=
        [
            (LeftView.Files, "tip-view-files", () => PickLeft(LeftView.Files)),
            (LeftView.Search, "tip-view-search", () => Search(null)),
            (LeftView.Passes, "tip-view-passes", () => PickLeft(LeftView.Passes))
        ];
        ui.Fill(x, y, w, h, Darker(Ground));
        ui.Fill(x + w - 1, y, 1, h, HudUi.Divider);
        var size = ui.Px(StripWidth);
        foreach (var (view, tip, click) in _strip.Bounded(3))
        {
            var on = Prefs.LeftShown && Prefs.Left == view;
            var color = Hot(x, y, w, size) ? HudUi.Soft : HudUi.Faint;
            color = on ? Rgba.White(1) : color;
            if (on) ui.Fill(x, y + ui.Px(6), ui.Px(3), size - ui.Px(12), HudUi.Link);

            StripIcon(ui, view, x + w / 2, y + size / 2, ui.Px(18), color);
            Hits.Add(x, y, w, size, click);
            Tip(x, y, w, size, L(tip));
            y += size;
        }
    }

    private static void StripIcon(HudUi ui, LeftView view, double cx, double cy, double s, Rgba color)
    {
        switch (view)
        {
            case LeftView.Files:
                StudioIcons.Folder(ui, cx - s / 2, cy, s, color);
                break;
            case LeftView.Search:
                StudioIcons.Magnifier(ui, cx - s / 2, cy, s, color);
                break;
            default: // stacked bars: the frame's passes
                for (var i = 0; i < 3; i++)
                {
                    ui.Fill(cx - s / 2, cy - s / 2 + i * s * 0.38, s * (1 - i * 0.25), s * 0.24, color);
                }

                break;
        }
    }

    private void PickLeft(LeftView view)
    {
        if (Prefs.LeftShown && Prefs.Left == view)
        {
            Prefs.LeftShown = false;
            return;
        }

        if (view == LeftView.Passes)
        {
            ShowPasses();
            return;
        }

        ShowLeft(view);
    }

    // Passes carry a switch, ms and a delta a row: they want more room than the tree
    private double LeftExtra() => Prefs.Left switch
    {
        LeftView.Passes => 200,
        LeftView.Search => 80,
        _ => 0
    };

    // The left column shows a view (the docked panel calls it when it is asked for search or passes)
    public void ShowLeft(LeftView view) => (Prefs.Left, Prefs.LeftShown) = (view, true);

    private void LeftPane(HudUi ui, double x, double y, double w, double h)
    {
        if (Prefs.Left == LeftView.Files)
        {
            Tree.Draw(ui, x, y, w, h);
            return;
        }

        Left.Draw(ui, x, y, w, h);
    }

    private void Overlays(HudUi ui)
    {
        History(ui);
        if (Context.IsOpen)
        {
            Hits.Region(ui.X, 0, ui.W, Height, Context);
            Context.Draw(ui.Column(ui.X, ui.W, 0), Height);
        }

        Palette.Draw(ui);
        Modal.Draw(ui);
        DrawToast(ui);
        Editor.DrawHover(ui.Column(ui.X, ui.W, 0), Height);
        DrawTip(ui);
    }

    // KOMET, the search field (Strg P), Auto with its pill, Testen with the state
    private void TopBar(HudUi ui, double h)
    {
        ui.Fill(ui.X, 0, ui.W, h, BandGround);
        ui.Fill(ui.X, h - 1, ui.W, 1, HudUi.Divider);
        var pad = ui.Px(16);
        _ = ui.Spaced(ui.X + pad + ui.Px(8), 0, h, ui.F.Strong, "KOMET", ui.Px(5), HudUi.Link);
        var searchW = Math.Min(ui.Px(360), ui.W * 0.4);
        var sx = ui.X + (ui.W - searchW) / 2;
        SearchBox(ui, sx, (h - ui.Px(28)) / 2, searchW, ui.Px(28));
        var right = ui.X + ui.W - pad;
        right = TestButton(ui, right, h);
        AutoSwitch(ui, right - ui.Px(16), h);
    }

    private void SearchBox(HudUi ui, double x, double y, double w, double h)
    {
        var hot = Hot(x, y, w, h);
        if (hot) ui.Fill(x, y, w, h, HudUi.Track with { A = 0.045 });

        TextField.Frame(ui, x, y, w, h, HudUi.Divider);
        StudioIcons.Magnifier(ui, x + ui.Px(10), y + h / 2, ui.Px(13), HudUi.Faint);
        ui.Text(x + ui.Px(32), y, h, ui.F.Body, L("search"), hot ? HudUi.Soft : HudUi.Faint);
        ui.TextRight(x + w - ui.Px(10), y, h, ui.F.Small, L("key-ctrl") + " P", HudUi.Faint);
        Hits.Add(x, y, w, h, _openPalette);
    }

    // Testen: a dot and the state, a hairline, a play mark and TESTEN, framed in the accent; its left x
    private double TestButton(HudUi ui, double right, double h)
    {
        var caps = Caps(L("test"));
        var tracking = ui.Px(2);
        var capsW = ui.TextW(ui.F.Small, caps) + tracking * Math.Max(0, caps.Length - 1);
        var stateW = ui.TextW(ui.F.Body, _statusText);
        var w = ui.Px(16) + ui.Px(7) + ui.Px(8) + stateW + ui.Px(10) + ui.Px(1) + ui.Px(10) + ui.Px(10) + ui.Px(8) +
                capsW + ui.Px(16);
        var (x, y, bh) = (right - w, (h - ui.Px(32)) / 2, ui.Px(32));
        var hot = Hot(x, y, w, bh);
        ui.Fill(x, y, w, bh, hot ? HudUi.Accent : HudUi.Accent with { A = 0.0 });
        TextField.Frame(ui, x, y, w, bh, HudUi.Accent with { A = 0.8 });
        var at = x + ui.Px(16);
        ui.Disc(at + ui.Px(3.5), y + bh / 2, ui.Px(3.5), _busy ? HudUi.Link : _statusColor);
        at += ui.Px(7) + ui.Px(8);
        ui.Text(at, y, bh, ui.F.Body, _statusText, HudUi.Soft);
        at += stateW + ui.Px(10);
        ui.Fill(at, y + bh / 2 - ui.Px(7), Math.Max(1, ui.Px(1)), ui.Px(14), HudUi.Divider);
        at += ui.Px(11);
        at += ui.Caret(at, y, bh, false, HudUi.Soft) + ui.Px(10);
        _ = ui.Spaced(at, y, bh, ui.F.Small, caps, tracking, Rgba.White(1));
        Hits.Add(x, y, w, bh, _buildNow);
        Tip(x, y, w, bh, L("tip-test") + " " + StateTip());
        return x;
    }

    private void AutoSwitch(HudUi ui, double right, double h)
    {
        if (Controls is not { } menu) return;

        var pw = MenuControls.S(MenuControls.PillWidth);
        var px = right - pw;
        menu.PillOnly(px, ui.At(h / 2), Prefs.Auto, menu.Text, true);
        var caps = Caps(L("auto"));
        var tw = ui.TextW(ui.F.Small, caps) + ui.Px(2) * Math.Max(0, caps.Length - 1);
        _ = ui.Spaced(px - ui.Px(10) - tw, 0, h, ui.F.Small, caps, ui.Px(2), HudUi.Faint);
        Hits.Add(px - ui.Px(10) - tw, (h - ui.Px(28)) / 2, tw + ui.Px(10) + pw, ui.Px(28), _toggleAuto);
        Tip(px - ui.Px(10) - tw, (h - ui.Px(28)) / 2, tw + ui.Px(10) + pw, ui.Px(28),
            L(Prefs.Auto ? "tip-auto-on" : "tip-auto-off"));
    }

    // The TEST chip, first that holds: Building, Not tested, Pack off, n errors, the open override's state, Running
    private (string Text, Rgba Color) State()
    {
        if (_busy) return (L("state-building"), HudUi.Link);

        if (_unbuilt) return (L("state-untested"), StudioColors.Pending);

        var project = ProjectInfo(Project);
        if (project is null || !project.Active) return (L("state-off"), HudUi.Faint);

        var errors = ProjectErrors();
        if (errors > 0) return (errors.ToString(CultureInfo.InvariantCulture) + " " + L("errors-word"), HudUi.Err);

        return StatusOf(Current) is { } status
            ? (StateWord(status.State), StateInk(status.State))
            : (L("state-running"), HudUi.Ok);
    }

    // The state chip's reason and next step, for its tooltip
    private string StateTip()
    {
        if (_busy) return L("tip-state-building");

        if (_unbuilt) return L("tip-state-untested");

        if (ProjectInfo(Project) is not { Active: true }) return L("tip-state-off");

        if (ProjectErrors() > 0) return L("tip-state-errors");

        return StatusOf(Current) is { } status
            ? StateWord(status.State) + ": " + status.Reason
            : L("tip-state-running");
    }

    private int ProjectErrors() => Panel.Errors;
}
