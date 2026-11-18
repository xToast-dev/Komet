using Komet.Diagnostics;
using Vintagestory.Client;
using Komet.Ui.Hud;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// Overlay (F7), the details below it while the HUD window (K) is open, and the spike toast; composed per interval
// and on settings change, drawn each frame while shown.
internal sealed class OverlayCard(
    ICoreClientAPI capi,
    HudSettings settings,
    HudUiFonts fonts,
    HudSampler sampler,
    HudBuild build,
    DebugCapture capture) : IDisposable
{
    private const double OverlayWidth = 212, ToastEverySeconds = 3, ToastShownSeconds = 5;
    private const int MaxDetailRows = 64, MaxDialogs = 256;
    private double _detailTop; // below the game's HUD at the top right (minimap, coordinates)
    private const double DetailWidth = 212;
    private HudCanvas? _overlayCanvas, _toastCanvas, _detailCanvas;
    private bool _overlayDirty = true;
    private long _toastAt = long.MinValue / 2, _spikesSeen;

    public void Dispose() => Owned.DropAll([_overlayCanvas, _toastCanvas, _detailCanvas]);

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
            (T("hud-renderer"), Komet.Options.VulkanMode.Renderer()),
            (T("hud-d-gpu"), N(l.GpuMs, 2) + " ms · " + N(draws)),
            (T("hud-d-tris"), N(l.DrawnTriangles / 1e6, 1) + " / " + N(l.Triangles / 1e6, 1) + " M"),
            (T("hud-v-culled"), double.IsFinite(l.CulledPercent) ? N(l.CulledPercent) + " %" : "–"),
            ("", ""),
            (T("hud-d-cpu"), N(r.CpuPercent) + " / " + N(r.SystemCpuPercent) + " %"),
            (T("hud-v-mods-total"), N(l.ModsMs, 2) + " ms"),
            ("", ""),
            (T("hud-d-ram"), Gb(r.UsedRamMb) + " / " + Gb(r.TotalRamMb) + " GB"),
            (T("hud-d-process"), Gb(r.WorkingSetMb) + " / " + Gb(r.ManagedUsedMb) + " GB"),
            (T("hud-d-vram"), double.IsFinite(g.VramUsedMb) ? Gb(g.VramUsedMb) + " / " + Gb(g.VramTotalMb) + " GB" : "–"),
            (T("hud-d-gc"), N(r.GcPausePercent, 1) + " % · " + N(r.AllocatedMbPerSec) + " MB/s"),
            ("", ""),
            (T("hud-d-chunks"), N(RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded) + " · " +
                                N(RuntimeStats.chunksAwaitingTesselation) + " / " + N(RuntimeStats.chunksAwaitingPooling)),
            (T("hud-d-entities"), N(RuntimeStats.renderedEntities) + " / " + N(capi.World?.LoadedEntities?.Count ?? 0))
        ];
    }

    private static string Gb(double mb) => double.IsFinite(mb) ? N(mb / 1024, 1) : "–";

    // The lowest edge of the game's HUD at the top right (the minimap, the coordinates): the card goes below it, at the
    // top when they are closed. Per interval, as the card is composed; Komet's own dialogs do not count.
    private double BelowTopRight(double width, double height)
    {
        var below = 0.0;
        if (capi.Gui?.OpenedGuis is not { } gui) return below;

        for (var i = 0; i < Math.Min(gui.Count, MaxDialogs); i++)
        {
            if (gui[i] is not { DialogType: EnumDialogType.HUD } dialog || dialog is HudDialog) continue;

            foreach (var (_, composer) in dialog.Composers.Bounded(MaxDialogs))
            {
                if (composer?.Bounds is not { } b || !double.IsFinite(b.renderY) || b.OuterHeight <= 0) continue;

                // reaching into the right quarter, starting in the top third
                if (b.renderX + b.OuterWidth > width * 0.75 && b.renderY < height / 3)
                {
                    below = Math.Max(below, b.renderY + b.OuterHeight);
                }
            }
        }

        return below;
    }

    private void ComposeDetails()
    {
        _detailTop = BelowTopRight(capi.Render.FrameWidth, capi.Render.FrameHeight);
        _detailCanvas ??= new HudCanvas(capi, true);
        var (f, px) = (fonts, scaled(fonts.Scale));
        var rows = Details();
        var lines = Array.FindAll(rows, row => row.Label.Length == 0).Length;
        var w = DetailWidth * px;
        var h = 12 * px + f.StrongRow + 2 * px + (rows.Length - lines) * f.SmallRow + lines * 7 * px;
        if (!_detailCanvas.Blank(w, h)) return;

        HudUi.Ground(_detailCanvas, w, h, HudUi.Back with { A = 0.95 * settings.Opacity }, scaled(4));
        var ui = new HudUi(_detailCanvas, f, (8 * px, w - 16 * px, 0.0, h, 0.0), []) { Y = 6 * px };
        ui.Text(ui.X, ui.Y, f.StrongRow, f.Strong, T("hud-v-details"));
        ui.Y += f.StrongRow + 2 * px;
        foreach (var (label, value) in rows.Bounded(MaxDetailRows))
        {
            if (label.Length > 0)
            {
                ui.Row(ui.Fit(f.Small, label, ui.W * 0.5), value, HudUi.Dim, null, f.Small);
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

    // card: the F7 overlay is on; details: the HUD window is open, its numbers go below the card
    public void Draw(bool card, bool details)
    {
        double w = capi.Render.FrameWidth, h = capi.Render.FrameHeight, m = scaled(12);
        if (!Assert(w > 0 && h > 0) || !(card || details)) return;

        if (_overlayDirty || _overlayCanvas is null || (details && _detailCanvas is null))
        {
            if (card || _overlayCanvas is null) ComposeOverlay();

            if (details) ComposeDetails();

            _overlayDirty = false;
        }

        var right = settings.Corner is HudCorner.TopRight or HudCorner.BottomRight;
        var bottom = settings.Corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        double oh = 0;
        if (card && _overlayCanvas is { Ready: true } overlay)
        {
            overlay.Draw(right ? w - m - overlay.Width : m, bottom ? h - m - overlay.Height : m);
            oh = overlay.Height + scaled(8);
        }

        // the details on the side the overlay leaves free: on the right, below the game's minimap and coordinates
        if (details && _detailCanvas is { Ready: true } detail)
        {
            var top = Math.Min(m + _detailTop, Math.Max(m, h - m - detail.Height)); // never past the bottom
            detail.Draw(right ? m : w - m - detail.Width, right ? m : top);
        }

        if (!card) return; // the toast belongs to the overlay

        var age = Environment.TickCount64 - _toastAt;
        if (_toastCanvas is not { Ready: true } toast || age > ToastShownSeconds * 1000) return;

        var t = Math.Clamp(Math.Min(age / 180.0, (ToastShownSeconds * 1000 - age) / 500.0), 0, 1);
        var shift = (1 - t) * (1 - t) * scaled(16) * (right ? 1 : -1);
        toast.Fade((right ? w - m - toast.Width : m) + shift, bottom ? h - m - oh - toast.Height : m + oh, 50, t, 1);
    }
}
