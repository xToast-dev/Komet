using Komet.Diagnostics;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// The frame card below the overlay: the frame-time graph, how the history is spread and its percentiles, the latest
// spikes; composed with the details per interval
internal sealed partial class OverlayCard
{
    private const int ShownSpikes = 5, Buckets = 4;
    private static readonly double[] Sorted = new double[2048];

    private static readonly Rgba Yellow = new(200 / 255.0, 190 / 255.0, 60 / 255.0, 1),
        Orange = new(230 / 255.0, 150 / 255.0, 50 / 255.0, 1);

    // The history's frame times sorted, how many
    private static int Sort(FrameStats frames)
    {
        var n = Math.Min(Math.Min(frames.Recorded, frames.HistoryLength), Sorted.Length);
        var first = frames.HistoryLength - n;
        for (var i = 0; i < Math.Min(n, Sorted.Length); i++)
        {
            Sorted[i] = frames.HistoryMs(first + i);
        }

        Array.Sort(Sorted, 0, n);
        return n;
    }

    private static double At(int n, double share) => n == 0 ? double.NaN : Sorted[Math.Clamp((int)(share * n), 0, n - 1)];

    private static double Spread(int n)
    {
        if (n < 2) return double.NaN;

        double sum = 0, squares = 0;
        for (var i = 0; i < Math.Min(n, Sorted.Length); i++)
        {
            (sum, squares) = (sum + Sorted[i], squares + Sorted[i] * Sorted[i]);
        }

        var mean = sum / n;
        return Math.Sqrt(Math.Max(0, squares / n - mean * mean));
    }

    private (string Label, string Value)[] FrameRows(int n)
    {
        var l = sampler.Live;
        return
        [
            (T("hud-f-fps"), N(l.Fps) + " / " + N(l.Low1) + " / " + N(l.Low01)),
            (T("hud-f-mid"), N(At(n, 0.5), 1) + " / " + N(At(n, 0.95), 1) + " ms"),
            (T("hud-f-high"), N(At(n, 0.99), 1) + " / " + N(At(n, 0.999), 1) + " / " + N(At(n, 1), 1) + " ms"),
            (T("hud-f-jitter"), N(Spread(n), 2) + " ms"),
            (T("hud-f-spikes"), N(sampler.Spikes.Count) + " · " + N(sampler.Frames.WorstGcMs, 1) + " ms")
        ];
    }

    private void ComposeFrames()
    {
        _framesCanvas ??= new HudCanvas(capi, true);
        var (f, px) = (fonts, scaled(fonts.Scale));
        var n = Sort(sampler.Frames);
        var rows = FrameRows(n);
        var w = OverlayWidth * px;
        var h = 6 * px + f.StrongRow + 4 * px + 90 * px + 6 * px + 12 * px + Buckets * f.SmallRow + 7 * px +
                rows.Length * f.SmallRow + 7 * px + f.SmallRow + ShownSpikes * f.SmallRow + 6 * px;
        if (!_framesCanvas.Blank(w, h)) return;

        HudUi.Ground(_framesCanvas, w, h, HudUi.Back with { A = 0.95 * settings.Opacity }, scaled(4));
        var ui = new HudUi(_framesCanvas, f, (8 * px, w - 16 * px, 0.0, h, 0.0), []) { Y = 6 * px };
        ui.Text(ui.X, ui.Y, f.StrongRow, f.Strong, T("hud-f-title"));
        ui.Y += f.StrongRow + 4 * px;
        Graph(ui, sampler.Frames, 90 * px, true);
        ui.Y += 6 * px;
        Distribution(ui, px);
        Hairline(ui, px);
        foreach (var (label, value) in rows.Bounded(MaxDetailRows))
        {
            ui.Row(ui.Fit(f.Small, label, ui.W * 0.5), value, HudUi.Dim, null, f.Small);
        }

        Hairline(ui, px);
        FrameSpikes(ui);
        _framesCanvas.End();
    }

    private static void Hairline(HudUi ui, double px)
    {
        ui.Fill(ui.X, ui.Y + 3 * px, ui.W, Math.Max(1, px), HudUi.Divider);
        ui.Y += 7 * px;
    }

    private void Distribution(HudUi ui, double px)
    {
        var l = sampler.Live;
        ui.Segments(ui.Y, 8 * px,
        [
            (l.Under8, HudUi.Ok), (l.Under12, Yellow), (l.Under25, Orange),
            (Math.Max(l.Over25, l.Over25 > 0 ? 0.3 : 0), HudUi.Err)
        ]);
        ui.Y += 12 * px;
        foreach (var (label, value, color) in (ReadOnlySpan<(string, double, Rgba)>)
                 [
                     ("< 8 ms", l.Under8, HudUi.Ok), ("8–12 ms", l.Under12, Yellow), ("12–25 ms", l.Under25, Orange),
                     ("> 25 ms", l.Over25, HudUi.Err)
                 ])
        {
            ui.Row(label, N(value, 1) + " %", color, null, ui.F.Small);
        }
    }

    private void FrameSpikes(HudUi ui)
    {
        var spikes = sampler.Spikes;
        var stored = Math.Min(spikes.Stored, ShownSpikes);
        ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small, T("hud-f-latest"), HudUi.Dim);
        ui.Y += ui.F.SmallRow;
        var now = capi.InWorldEllapsedMilliseconds / 1000.0;
        for (var age = 0; age < Math.Min(stored, ShownSpikes); age++)
        {
            var spike = spikes.Latest(age);
            var ago = Math.Max(0, now - spike.AtSeconds);
            var ms = N(spike.DtMs) + " ms";
            if (age > 0 || ago >= ToastShownSeconds)
            {
                ui.Row(ms, T("hud-v-ago", N(ago)), HudUi.Warn, HudUi.Dim, ui.F.Small);
                continue;
            }

            // the newest, a few seconds old: lit up with its cause, where the toast would have stood
            var right = T("hud-v-just-now");
            ui.Fill(ui.X - ui.Px(3), ui.Y, ui.W + ui.Px(6), ui.F.SmallRow, HudUi.Warn with { A = 0.22 }, ui.Px(2));
            var x = ui.X + ui.TextW(ui.F.Small, ms) + ui.Px(5);
            ui.Text(ui.X, ui.Y, ui.F.SmallRow, ui.F.Small, ms, HudUi.Warn);
            ui.Text(x, ui.Y, ui.F.SmallRow, ui.F.Small,
                ui.Fit(ui.F.Small, Describe(spike.Cause), ui.X + ui.W - x - ui.TextW(ui.F.Small, right) - ui.Px(6)));
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.SmallRow, ui.F.Small, right, HudUi.Dim);
            ui.Y += ui.F.SmallRow;
        }
    }
}
