using Komet.Diagnostics;

namespace Komet.Hud;

// Overlay (F7) and spike toast, composed per interval and on settings change.
internal sealed partial class HudOverlay
{
    private const double OverlayWidth = 212, ToastEverySeconds = 3, ToastShownSeconds = 5;
    private readonly HudUiFonts _uiFonts = new();
    private HudCanvas? _overlayCanvas, _toastCanvas;
    private long _toastAt = long.MinValue / 2, _spikesSeen;
    private bool _overlayDirty = true;

    private void ComposeOverlay()
    {
        _overlayDirty = false;
        _uiFonts.Update(_settings.FontScale);
        _overlayCanvas ??= new HudCanvas(_capi, background: true);
        var (s, f) = (_settings, _uiFonts);
        var px = scaled(f.Scale);
        var w = OverlayWidth * px;
        var numbers = s.ShowFps || s.ShowLows || s.ShowFrametime;
        var band = HudUi.Framed ? 4 * px : 0; // a tabs style sets the head on a band of its own
        var h = 12 * px + band + f.StrongRow + (numbers ? 6 * px + f.HugeRow + f.SmallRow : 0) + (s.ShowGraph ? 34 * px : 0) +
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
            Graph(ui, 30 * px, false);
            ui.Space(2);
        }

        if (s.ShowMods) OverlayMods(ui);
        ui.Space(3);
        ui.Line(T("hud-v-overlay-footer"), HudUi.Dim, f.Small);
        _overlayCanvas.End();
    }

    private void OverlayHead(HudUi ui)
    {
        // the name letter-spaced in a tabs style as the menu's header has it; the edition as a short chip, outlined there
        double name;
        if (HudUi.Framed) name = ui.Spaced(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "KOMET", ui.Px(1.5));
        else
        {
            ui.Text(ui.X, ui.Y, ui.F.StrongRow, ui.F.Strong, "Komet");
            name = ui.TextW(ui.F.Strong, "Komet");
        }

        var (_, fill, color) = EditionBadge();
        var badge = T("hud-v-edition-short-" + Edition(), _build.Version);
        var x = ui.X + name + ui.Px(6);
        var (by, bh) = (ui.Y + (ui.F.StrongRow - ui.F.SmallRow) / 2, ui.F.SmallRow);
        var bw = Math.Min(ui.TextW(ui.F.Small, badge) + ui.Px(10), ui.X + ui.W - x - ui.Px(50));
        if (HudUi.Framed)
        {
            ui.Fill(x, by, bw, bh, color with { A = 0.12 });
            foreach (var (ex, ey, ew, eh) in (ReadOnlySpan<(double, double, double, double)>)[(x, by, bw, 1), (x, by + bh - 1, bw, 1), (x, by, 1, bh), (x + bw - 1, by, 1, bh)])
                ui.Fill(ex, ey, ew, eh, color with { A = 0.7 });
        }
        else ui.Fill(x, by, bw, bh, fill, ui.Px(3));

        ui.Text(x + ui.Px(5), by, bh, ui.F.Small, ui.Fit(ui.F.Small, badge, bw - ui.Px(10)), color);
        if (_debug is not null)
        {
            var armed = _debugMode == "spike" && _debugTrigger.Length == 0;
            var rec = armed ? T("hud-v-armed-short") : "REC " + N(Math.Max(0, _debug.Now));
            var recColor = armed ? HudUi.Warn : HudUi.Err;
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.StrongRow, ui.F.Small, rec, recColor);
            _ = ui.Dot(ui.X + ui.W - ui.TextW(ui.F.Small, rec) - ui.Px(11), ui.Y, ui.F.StrongRow, recColor);
        }

        ui.Y += ui.F.StrongRow;
    }

    private void OverlayNumbers(HudUi ui)
    {
        var (s, l) = (_settings, _live);
        ui.Space(4);
        var x = ui.X;
        foreach (var (on, value, label) in (ReadOnlySpan<(bool, string, string)>)[(s.ShowFps, N(l.Fps), "FPS"),
                     (s.ShowLows, N(l.Low1), "1% Low"), (s.ShowFrametime, N(l.AvgMs, 1), "ms")])
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
        ui.Row(T("hud-v-mods-total"), N(_live.ModsMs, 2) + " ms");
    }

    private void Toast()
    {
        var (count, seen) = (_spikes.Count, _spikesSeen);
        _spikesSeen = count;
        // only the overlay draws the toast: hidden, a composition would paint on the main thread for nothing
        if (count == seen || !_settings.Visible || _settings.ToastMs <= 0 || _spikes.Stored == 0) return;
        var spike = _spikes.Latest(0);
        var now = Environment.TickCount64;
        if (spike.DtMs < _settings.ToastMs || now - _toastAt < ToastEverySeconds * 1000) return;
        _toastAt = now;
        ComposeToast(spike, count);
    }

    private void ComposeToast(Spike spike, long count)
    {
        _uiFonts.Update(_settings.FontScale);
        _toastCanvas ??= new HudCanvas(_capi, background: true);
        var (f, px) = (_uiFonts, scaled(_uiFonts.Scale));
        var (w, h) = (OverlayWidth * px, 10 * px + f.StrongRow + f.BodyRow + f.SmallRow);
        if (!_toastCanvas.Blank(w, h)) return;
        HudUi.Ground(_toastCanvas, w, h, HudUi.Back with { A = 0.95 * _settings.Opacity }, 0);
        _toastCanvas.Fill(0, 0, 3 * px, h, HudUi.Warn);
        var ui = new HudUi(_toastCanvas, f, (11 * px, w - 19 * px, 0.0, h, 0.0), []) { Y = 5 * px };
        ui.Row(T("hud-v-toast-title", N(spike.DtMs)), T("hud-v-just-now"), HudUi.Warn, HudUi.Dim, f.Strong);
        ui.Line(Describe(spike.Cause));
        ui.Line(T("hud-v-toast-more", count), HudUi.Dim, f.Small);
        _toastCanvas.End();
    }

    private void DrawOverlay()
    {
        double w = _capi.Render.FrameWidth, h = _capi.Render.FrameHeight, m = scaled(12);
        if (!Assert(w > 0 && h > 0)) return;
        if (_overlayDirty || _overlayCanvas is null) ComposeOverlay();
        var right = _settings.Corner is HudCorner.TopRight or HudCorner.BottomRight;
        var bottom = _settings.Corner is HudCorner.BottomLeft or HudCorner.BottomRight;
        double oh = 0;
        if (_overlayCanvas is { Ready: true } overlay)
        {
            overlay.Draw(right ? w - m - overlay.Width : m, bottom ? h - m - overlay.Height : m);
            oh = overlay.Height + scaled(8);
        }

        var age = Environment.TickCount64 - _toastAt;
        if (_toastCanvas is not { Ready: true } toast || age > ToastShownSeconds * 1000) return;
        var t = Math.Clamp(Math.Min(age / 180.0, (ToastShownSeconds * 1000 - age) / 500.0), 0, 1);
        var shift = (1 - t) * (1 - t) * scaled(16) * (right ? 1 : -1);
        toast.Fade((right ? w - m - toast.Width : m) + shift, bottom ? h - m - oh - toast.Height : m + oh, 50, t, 1);
    }
}
