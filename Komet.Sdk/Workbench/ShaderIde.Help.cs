using Komet.Hud;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// What the studio explains without docs: one tooltip at a time for icons and short chips (registered while drawing,
// drawn last over everything), the override state of a file as the backend sees it, and the start surface without a tab
internal sealed partial class ShaderIde
{
    private Action? _startOverride, _startPack, _startPasses;
    private string _tip = "";

    // While a scrolled band draws: tips only inside it (a row scrolled out of it has none)
    public (double Top, double Bottom) TipBand { get; set; } = (double.MinValue, double.MaxValue);

    // The live picture's place this frame: only while nothing the canvas draws on top could be covered by it
    public (double X, double Y, double W, double H)? Live { get; private set; }

    public void SetLive((double X, double Y, double W, double H)? rect)
    {
        var covered = Context.IsOpen || Palette.Open || Modal.Open || _historyOpen || Peeking;
        Live = covered ? null : rect;
    }

    private void ModelChanged()
    {
        Tree.ClearTags();
        NoticesStale();
        _nextFull = 0; // the problem list reads what the game sets: with the next slow refresh, not in 30 s
        Redraw();
    }

    // Registers a tooltip for a rectangle; the one under the mouse shows after the frame
    public void Tip(double x, double y, double w, double h, string text)
    {
        if (Hot(x, y, w, h) && MouseY >= TipBand.Top && MouseY < TipBand.Bottom) _tip = text;
    }

    private void DrawTip(HudUi ui)
    {
        var text = _tip;
        _tip = "";
        if (text.Length == 0) return;

        var width = Math.Min(ui.Px(420), ui.TextW(ui.F.Small, text) + ui.Px(16));
        var x = Math.Clamp(MouseX + ui.Px(14), ui.X, ui.X + ui.W - width);
        var y = Math.Clamp(MouseY + ui.Px(18), ui.Y, Height - ui.F.SmallRow - ui.Px(8));
        ui.Panel(x, y, width, ui.F.SmallRow + ui.Px(6), HudUi.PopupBack);
        ui.Text(x + ui.Px(8), y + ui.Px(3), ui.F.SmallRow, ui.F.Small, ui.Fit(ui.F.Small, text, width - ui.Px(16)),
            HudUi.Soft);
    }

    // A vanilla shader the active project has its own version of (one entry per file: it opens as that version)
    public bool HasCopy(string path) => CopyOf(path) is not null;

    public string? CopyOf(string path) =>
        StudioLink.IsGameShader(path) && Project is { } project && StudioLink.CopyPath(project, path) is { } copy &&
        File.Exists(copy)
            ? copy
            : null;

    // A project copy's override: (true, on) while its project is active and the program is not on its vanilla list
    public (bool Override, bool On) OverrideState(string path)
    {
        if (StudioLink.GameFileOf(path) is not { } game || StudioLink.ProjectOf(path) is not { } project ||
            StudioLink.Relative(game) is not { } relative)
        {
            return (false, false);
        }

        var active = ProjectInfo(project) is { Active: true };
        return (true, active && !ShaderModules.KeepsGame(relative, project));
    }

    // A vanilla file's head: the lock and "Vanilla · read-only", then the way to change it; its left x
    private double VanillaHead(HudUi ui, double right, double y, double h, ShaderDocument document)
    {
        var copy = Project is null ? null : StudioLink.CopyPath(Project, document.Path);
        var exists = copy is not null && File.Exists(copy);
        var label = exists ? L("edit-my-version") : L("create-override");
        var bw = ui.TextW(ui.F.Small, label) + ui.Px(24);
        var (bx, by, bh) = (right - bw, y + (h - ui.Px(24)) / 2, ui.Px(24));
        ui.Fill(bx, by, bw, bh, Hot(bx, by, bw, bh) ? HudUi.Accent : HudUi.Accent with { A = 0.35 });
        ui.Text(bx + ui.Px(12), by, bh, ui.F.Small, label, Rgba.White(1));
        var path = document.Path;
        Hits.Add(bx, by, bw, bh, exists ? () => Open(copy!) : () => CopyIn(path));
        Tip(bx, by, bw, bh, L(exists ? "tip-edit-my-version" : "tip-create-override"));
        var engine = Caps(L("engine"));
        var ew = ui.TextW(ui.F.Small, engine) + ui.Px(1.5) * engine.Length + ui.Px(20);
        var ex = bx - ui.Px(8) - ew;
        ui.Fill(ex, by, ew, bh, Mode == EditorMode.Engine ? HudUi.SelectionBack : HudUi.Track with { A = 0.06 });
        _ = ui.Spaced(ex + ui.Px(10), by, bh, ui.F.Small, engine, ui.Px(1.5),
            Mode == EditorMode.Engine ? Rgba.White(1) : HudUi.Soft);
        Hits.Add(ex, by, ew, bh, _modeEngine!);
        Tip(ex, by, ew, bh, L("tip-engine"));
        bx = ex;
        var chip = Caps(L("vanilla-readonly"));
        var cw = ui.TextW(ui.F.Small, chip) + ui.Px(1.5) * chip.Length + ui.Px(30);
        var cx = bx - ui.Px(10) - cw;
        ui.Fill(cx, by, cw, bh, StudioColors.Gold with { A = 0.14 });
        StudioIcons.Lock(ui, cx + ui.Px(12), y + h / 2, ui.Px(9), StudioColors.Gold);
        _ = ui.Spaced(cx + ui.Px(24), by, bh, ui.F.Small, chip, ui.Px(1.5), StudioColors.Gold);
        Tip(cx, by, cw, bh, L("tip-vanilla-readonly"));
        return cx;
    }

    // No tab: three ways in
    private void Start(HudUi ui, double x, double y, double w, double bottom)
    {
        _startOverride ??= Palette.Show;
        _startPack ??= OpenPack;
        _startPasses ??= ShowPasses;
        var title = L("start-title");
        var mid = y + (bottom - y) / 2 - ui.Px(80);
        ui.Text(x + (w - ui.TextW(ui.F.Strong, title)) / 2, mid, ui.F.StrongRow, ui.F.Strong, title, HudUi.Soft);
        var bw = Math.Min(ui.Px(320), w - ui.Px(40));
        var bx = x + (w - bw) / 2;
        var at = mid + ui.F.StrongRow + ui.Px(16);
        foreach (var (label, hint, run) in (ReadOnlySpan<(string, string, Action)>)
                 [
                     (L("start-override"), L("start-override-hint"), _startOverride),
                     (L("start-pack"), L("start-pack-hint"), _startPack),
                     (L("start-passes"), L("start-passes-hint"), _startPasses)
                 ])
        {
            var bh = ui.Px(34);
            ui.Fill(bx, at, bw, bh,
                Hot(bx, at, bw, bh) ? HudUi.Track with { A = 0.12 } : HudUi.Track with { A = 0.05 });
            TextField.Frame(ui, bx, at, bw, bh, HudUi.Divider);
            ui.Text(bx + ui.Px(14), at, bh, ui.F.Body, label, Rgba.White(1));
            Hits.Add(bx, at, bw, bh, run);
            Tip(bx, at, bw, bh, hint);
            at += bh + ui.Px(10);
        }
    }

    // The active pack's manifest, else its folder in the explorer
    private void OpenPack()
    {
        if (Project is not { } project)
        {
            Toast(L("no-project"));
            return;
        }

        var manifest = Path.Join(StudioLink.ProjectsFolder, project, "komet-shader.json");
        if (File.Exists(manifest))
        {
            Open(manifest);
            return;
        }

        Tree.Reveal(Path.Join(StudioLink.ProjectsFolder, project));
    }
}
