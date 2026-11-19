using Komet.Diagnostics;
using Komet.Options;
using Komet.Ui.Hud;
using Vintagestory.Client;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// Overlay (F7), the details below it while the HUD window (K) is open, and the spike toast; composed per interval
// and on settings change, drawn each frame while shown.
internal sealed partial class OverlayCard(
    ICoreClientAPI capi,
    HudSettings settings,
    HudUiFonts fonts,
    HudSampler sampler,
    HudBuild build,
    DebugCapture capture,
    SnapGrid snap) : IDisposable
{
    private const double OverlayWidth = 212, ToastEverySeconds = 3, ToastShownSeconds = 5;
    private const int MaxDetailRows = 64, MaxDialogs = 256;
    private const double DetailWidth = 300;
    private readonly List<(double X, double Y, double W, double H)> _hud = [];
    private HudCanvas? _overlayCanvas, _toastCanvas, _detailCanvas, _framesCanvas;
    private bool _overlayDirty = true;
    private long _toastAt = long.MinValue / 2, _spikesSeen;

    public void Dispose() => Owned.DropAll([_overlayCanvas, _toastCanvas, _detailCanvas, _framesCanvas]);

    // New numbers or settings: composed again at the next draw
    public void Invalidate() => _overlayDirty = true;

    private void ComposeOverlay()
    {
        _overlayDirty = false;
        fonts.Update(settings.FontScale);
        _overlayCanvas ??= new HudCanvas(capi, true);
        var (s, f) = (settings, fonts);
        var px = scaled(f.Scale);
        var w = OverlayWidth * px;
        var numbers = s.ShowFps || s.ShowLows || s.ShowFrametime;
        var band = HudUi.Framed ? 4 * px : 0; // a tabs style sets the head on a band of its own
        var h = 12 * px + band + f.StrongRow + (numbers ? 6 * px + f.HugeRow + f.SmallRow : 0) +
                (s.ShowGraph ? 34 * px : 0) +
                (s.ShowMods ? 9 * px + f.BodyRow : 0) + 3 * px + f.SmallRow;
        if (!_overlayCanvas.Blank(w, h)) return;

        HudUi.Ground(_overlayCanvas, w, h, HudUi.Back with { A = 0.95 * s.Opacity }, scaled(4));
        var ui = new HudUi(_overlayCanvas, f, (8 * px, w - 16 * px, 0.0, h, 0.0), []) { Y = 6 * px };
        if (HudUi.Framed)
        {
            _overlayCanvas.Fill(1, 1, w - 2, 6 * px + f.StrongRow + band - 1, HudUi.HeadBack);
            _overlayCanvas.Fill(1, 6 * px + f.StrongRow + band, w - 2, 1, HudUi.Divider);
        }

        OverlayHead(ui);
        ui.Y += band;
        if (numbers) OverlayNumbers(ui);

        if (s.ShowGraph)
        {
            ui.Space(2);
            Graph(ui, sampler.Frames, 30 * px, false);
            ui.Space(2);
        }

        if (s.ShowMods) OverlayMods(ui);

        ui.Space(3);
        ui.Line(T("hud-v-overlay-footer", HudSettings.KeyOf(capi, "toggle"), HudSettings.KeyOf(capi, "window")),
            HudUi.Dim, f.Small);
        _overlayCanvas.End();
    }

    // What the window's tabs measure, at a glance beside the game: one row per subject, related numbers side by side,
    // groups (frames, rendering, CPU, memory, collections, world) apart by a hairline (a row with no label)
    private (string Label, string Value)[] Details()
    {
        var (l, r, g, frames) = (sampler.Live, sampler.Resources, sampler.Gpu, sampler.Frames);
        var draws = l.Vulkan.Draws > 0 ? l.Vulkan.Draws : l.DrawCalls;
        return
        [
            (T("hud-d-frametime"), N(l.AvgMs, 1) + " / " + N(l.P99, 1) + " / " + N(frames.WorstMs, 1) + " ms"),
            (T("hud-d-lows"), N(l.Low01) + " FPS · " + N(sampler.Spikes.Count)),
            ("", ""),
            (T("hud-renderer"), VulkanMode.Renderer()),
            (T("hud-d-gpu"), N(l.GpuMs, 2) + " ms · " + N(draws)),
            (T("hud-d-tris"), N(l.DrawnTriangles / 1e6, 1) + " / " + N(l.Triangles / 1e6, 1) + " M"),
            (T("hud-v-culled"), double.IsFinite(l.CulledPercent) ? N(l.CulledPercent) + " %" : "–"),
            ("", ""),
            (T("hud-d-cpu"), N(r.CpuPercent) + " / " + N(r.SystemCpuPercent) + " %"),
            (T("hud-v-mods-total"), N(l.ModsMs, 2) + " ms"),
            ("", ""),
            (T("hud-d-ram"), Gb(r.UsedRamMb) + " / " + Gb(r.TotalRamMb) + " GB"),
            (T("hud-d-process"), Gb(r.WorkingSetMb) + " / " + Gb(r.ManagedUsedMb) + " GB"),
            (T("hud-d-vram"),
                double.IsFinite(g.VramUsedMb) ? Gb(g.VramUsedMb) + " / " + Gb(g.VramTotalMb) + " GB" : "–"),
            (T("hud-d-gc"), N(r.GcPausePercent, 1) + " % · " + N(r.AllocatedMbPerSec) + " MB/s"),
            ("", ""),
            (T("hud-d-chunks"), N(RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded) + " · " +
                                N(RuntimeStats.chunksAwaitingTesselation) + " / " +
                                N(RuntimeStats.chunksAwaitingPooling)),
            (T("hud-d-entities"), N(RuntimeStats.renderedEntities) + " / " + N(capi.World?.LoadedEntities?.Count ?? 0))
        ];
    }

    private static string Gb(double mb) => double.IsFinite(mb) ? N(mb / 1024, 1) : "–";

    // Where the game's HUD is (minimap, coordinates, hotbar, wherever the player put them): the card takes a place free
    // of it. Per interval, as the card is composed; Komet's own dialogs and full-screen bounds do not count.
    private void FindHud(double width, double height)
    {
        _hud.Clear();
        if (capi.Gui?.OpenedGuis is not { } gui) return;

        for (var i = 0; i < Math.Min(gui.Count, MaxDialogs); i++)
        {
            if (gui[i] is not { DialogType: EnumDialogType.HUD } dialog || dialog is HudDialog) continue;

            foreach (var (_, composer) in dialog.Composers.Bounded(MaxDialogs))
            {
                if (composer?.Bounds is not { } b || !double.IsFinite(b.renderX) ||
                    !double.IsFinite(b.renderY)) continue;

                if (b.OuterWidth <= 0 || b.OuterHeight <= 0 ||
                    b.OuterWidth * b.OuterHeight > width * height / 4) continue;

                if (_hud.Count < MaxDialogs) _hud.Add((b.renderX, b.renderY, b.OuterWidth, b.OuterHeight));
            }
        }
    }

    // By default beside the overlay, edge to edge on the snap grid and lined up with its top, in its corner while it
    // is off. Where that does not fit, the first place free of the game's HUD.
    private (double X, double Y) Beside(double cw, double ch, double w, double h, double m, bool right, bool bottom,
        (double X, double Y, double W, double H) overlay)
    {
        var gap = scaled(HudSettings.SnapStep);
        var x = right ? w - m - cw : m;
        if (overlay.W > 0) x = right ? overlay.X - gap - cw : overlay.X + overlay.W + gap;

        var y = bottom ? h - m - ch : m;
        if (x >= 0 && x + cw <= w && FirstOverlap(x, y, cw, ch, overlay) is null) return (x, y);

        return FreeSpot(cw, ch, w, h, m, right, overlay);
    }

    // The first free place from the top, at the right edge, else the left one (the overlay's side last)
    private (double X, double Y) FreeSpot(double cw, double ch, double w, double h, double m, bool right,
        (double X, double Y, double W, double H) overlay)
    {
        var sides = right ? (m, w - m - cw) : (w - m - cw, m);
        foreach (var x in (double[])[sides.Item1, sides.Item2])
        {
            var y = m;
            for (var tries = 0; tries < Math.Min(_hud.Count + 2, MaxDialogs + 2) && y + ch <= h - m; tries++)
            {
                var hit = FirstOverlap(x, y, cw, ch, overlay);
                if (hit is null) return (x, y);

                y = hit.Value + m;
            }
        }

        return (sides.Item1, m);
    }

    // The bottom of something the place overlaps, null when it is free
    private double? FirstOverlap(double x, double y, double cw, double ch, (double X, double Y, double W, double H) o)
    {
        double? bottom = null;
        foreach (var r in _hud.Append(o).Bounded(MaxDialogs + 1))
        {
            if (r.W > 0 && x < r.X + r.W && r.X < x + cw && y < r.Y + r.H && r.Y < y + ch)
            {
                bottom = Math.Max(bottom ?? 0, r.Y + r.H);
            }
        }

        return bottom;
    }

    private void ComposeDetails()
    {
        FindHud(capi.Render.FrameWidth, capi.Render.FrameHeight);
        _detailCanvas ??= new HudCanvas(capi, true);
        var (f, px) = (fonts, scaled(fonts.Scale));
        var rows = Details();
        var lines = Array.FindAll(rows, row => row.Label.Length == 0).Length;
        var w = DetailWidth * px;
        var h = 12 * px + f.StrongRow + 2 * px + (rows.Length - lines) * f.SmallRow + lines * (7 * px);
        if (!_detailCanvas.Blank(w, h)) return;

        HudUi.Ground(_detailCanvas, w, h, HudUi.Back with { A = 0.95 * settings.Opacity }, scaled(4));
        var ui = new HudUi(_detailCanvas, f, (8 * px, w - 16 * px, 0.0, h, 0.0), []) { Y = 6 * px };
        ui.Text(ui.X, ui.Y, f.StrongRow, f.Strong, T("hud-v-details"));
        ui.Y += f.StrongRow + 2 * px;
        foreach (var (label, value) in rows.Bounded(MaxDetailRows))
        {
            if (label.Length > 0)
            {
                ui.Row(ui.Fit(f.Small, label, ui.W * 0.6), value, HudUi.Dim, null, f.Small);
                continue;
            }

            ui.Fill(ui.X, ui.Y + 3 * px, ui.W, Math.Max(1, px), HudUi.Divider);
            ui.Y += 7 * px;
        }

        _detailCanvas.End();
    }

    private void OverlayHead(HudUi ui)
    {
        // the name letter-spaced in a tabs style as the menu's header has it; the edition as a short chip, outlined there
        double name;
        if (HudUi.Framed)
        {
            name = ui.Spaced(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "KOMET", ui.Px(1.5));
        }
        else
        {
            ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "Komet");
            name = ui.TextW(ui.F.Strong, "Komet");
        }

        var (_, fill, color) = build.EditionBadge();
        var badge = T("hud-v-edition-short-" + HudBuild.Edition(), build.Version);
        var x = ui.X + name + ui.Px(6);
        var (by, bh) = (ui.Y + (ui.F.StrongRow - ui.F.SmallRow) / 2, ui.F.SmallRow);
        var bw = Math.Min(ui.TextW(ui.F.Small, badge) + ui.Px(10), ui.X + ui.W - x - ui.Px(50));
        if (HudUi.Framed)
        {
            ui.Fill(x, by, bw, bh, color with { A = 0.12 });
            foreach (var (ex, ey, ew, eh) in (ReadOnlySpan<(double, double, double, double)>)
                     [(x, by, bw, 1), (x, by + bh - 1, bw, 1), (x, by, 1, bh), (x + bw - 1, by, 1, bh)])
            {
                ui.Fill(ex, ey, ew, eh, color with { A = 0.7 });
            }
        }
        else
        {
            ui.Fill(x, by, bw, bh, fill, ui.Px(3));
        }

        ui.Text(x + ui.Px(5), by, bh, ui.F.Small, ui.Fit(ui.F.Small, badge, bw - ui.Px(10)), color);
        if (capture.Recording)
        {
            var progress = capture.Progress;
            var armed = progress.Armed;
            var rec = armed ? T("hud-v-armed-short") : "REC " + N(Math.Max(0, progress.Elapsed));
            var recColor = armed ? HudUi.Warn : HudUi.Err;
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.StrongRow, ui.F.Small, rec, recColor);
            _ = ui.Dot(ui.X + ui.W - ui.TextW(ui.F.Small, rec) - ui.Px(11), ui.Y, ui.F.StrongRow, recColor);
        }

        ui.Y += ui.F.StrongRow;
    }

    private void OverlayNumbers(HudUi ui)
    {
        var (s, l) = (settings, sampler.Live);
        ui.Space(4);
        var x = ui.X;
        foreach (var (on, value, label) in (ReadOnlySpan<(bool, string, string)>)
                 [
                     (s.ShowFps, N(l.Fps), "FPS"),
                     (s.ShowLows, N(l.Low1), "1% Low"), (s.ShowFrametime, N(l.AvgMs, 1), "ms")
                 ])
        {
            if (!on) continue;

            ui.Text(x, ui.Y, ui.F.HugeRow, ui.F.Huge, value);
            ui.Text(x, ui.Y + ui.F.HugeRow, ui.F.SmallRow, ui.F.Small, label, HudUi.Dim);
            x += Math.Max(ui.TextW(ui.F.Huge, value), ui.TextW(ui.F.Small, label)) + ui.Px(14);
        }

        ui.Y += ui.F.HugeRow + ui.F.SmallRow + ui.Px(2);
    }

    private void OverlayMods(HudUi ui)
    {
        ui.Space(5);
        ui.Fill(ui.X, ui.Y, ui.W, Math.Max(1, ui.Px(1)), HudUi.Divider);
        ui.Space(3);
        ui.Row(T("hud-v-mods-total"), N(sampler.Live.ModsMs, 2) + " ms");
    }

    // Per frame, overlay shown or not: a new spike over the threshold composes the toast
    public void Toast()
    {
        var spikes = sampler.Spikes;
        var (count, seen) = (spikes.Count, _spikesSeen);
        _spikesSeen = count;
        // only the overlay draws the toast: hidden, a composition would paint on the main thread for nothing
        if (count == seen || !settings.Visible || settings.ToastMs <= 0 || spikes.Stored == 0) return;

        var spike = spikes.Latest(0);
        var now = Environment.TickCount64;
        if (spike.DtMs < settings.ToastMs || now - _toastAt < ToastEverySeconds * 1000) return;

        _toastAt = now;
        ComposeToast(spike, count);
    }

    private void ComposeToast(Spike spike, long count)
    {
        fonts.Update(settings.FontScale);
        _toastCanvas ??= new HudCanvas(capi, true);
        var (f, px) = (fonts, scaled(fonts.Scale));
        var (w, h) = (OverlayWidth * px, 10 * px + f.StrongRow + f.BodyRow + f.SmallRow);
        if (!_toastCanvas.Blank(w, h)) return;

        HudUi.Ground(_toastCanvas, w, h, HudUi.Back with { A = 0.95 * settings.Opacity }, 0);
        _toastCanvas.Fill(0, 0, 3 * px, h, HudUi.Warn);
        var ui = new HudUi(_toastCanvas, f, (11 * px, w - 19 * px, 0.0, h, 0.0), []) { Y = 5 * px };
        ui.Row(T("hud-v-toast-title", N(spike.DtMs)), T("hud-v-just-now"), HudUi.Warn, HudUi.Dim, f.Strong);
        ui.Line(Describe(spike.Cause));
        ui.Line(T("hud-v-toast-more", count, HudSettings.KeyOf(capi, "window")), HudUi.Dim, f.Small);
        _toastCanvas.End();
    }

    // card: the F7 overlay is on; details: K or the HUD window, the frame and details cards beside it
    public void Draw(bool card, bool details)
    {
        double w = capi.Render.FrameWidth, h = capi.Render.FrameHeight, m = scaled(12);
        if (!Assert(w > 0 && h > 0) || !(card || details)) return;

        if (_overlayDirty || _overlayCanvas is null || (details && _detailCanvas is null))
        {
            if (card || _overlayCanvas is null) ComposeOverlay();

            if (details)
            {
                ComposeDetails();
                ComposeFrames();
            }

            _overlayDirty = false;
        }

        if (_dragging >= 0) snap.Draw();

        var corner = DrawCards(card, details, w, h, m);
        // with the frame card open its spike list lights the newest up instead
        if (card && _shown[Frames].W <= 0) DrawToast(corner.Right, corner.Bottom, _shown[Overlay]);
    }

    private (bool Right, bool Bottom) DrawCards(bool card, bool details, double w, double h, double m)
    {
        var right = settings.Corner is HudCorner.TopRight or HudCorner.BottomRight;
        var bottom = settings.Corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        Array.Clear(_shown);
        var gap = scaled(HudSettings.SnapStep);
        var home = (X: right ? w - m - OverlayWidth * scaled(fonts.Scale) : m, Y: m);
        if (card && _overlayCanvas is { Ready: true } overlay)
        {
            home = (right ? w - m - overlay.Width : m, bottom ? h - m - overlay.Height : m);
            Show(Overlay, overlay, home, w, h);
        }

        if (!details) return (right, bottom);

        var o = _shown[Overlay];
        if (_framesCanvas is { Ready: true } frames)
        {
            var below = bottom ? o.Y - gap - frames.Height : o.Y + o.H + gap;
            Show(Frames, frames, o.W > 0 ? (o.X, below) : home, w, h);
        }

        if (_detailCanvas is { Ready: true } detail)
        {
            Show(DetailCard, detail, Beside(detail.Width, detail.Height, w, h, m, right, bottom, o), w, h);
        }

        return (right, bottom);
    }

    private void DrawToast(bool right, bool bottom, (double X, double Y, double W, double H) o)
    {
        var age = Environment.TickCount64 - _toastAt;
        if (o.W <= 0 || _toastCanvas is not { Ready: true } toast || age > ToastShownSeconds * 1000) return;

        var t = Math.Clamp(Math.Min(age / 180.0, (ToastShownSeconds * 1000 - age) / 500.0), 0, 1);
        var shift = (1 - t) * (1 - t) * scaled(16) * (right ? 1 : -1);
        var gap = scaled(8);
        toast.Fade(o.X + shift, bottom ? o.Y - gap - toast.Height : o.Y + o.H + gap, 50, t, 1);
    }
}
