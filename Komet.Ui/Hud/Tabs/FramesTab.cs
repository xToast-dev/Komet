using static Komet.Hud.HudParts;

namespace Komet.Hud;

// Frame times: the big graph, the distribution of the history and the latest spikes
internal sealed class FramesTab(ICoreClientAPI capi, HudSampler sampler) : IHudTab
{
    private const int ShownSpikes = 5;

    private static readonly Rgba Yellow = new(200 / 255.0, 190 / 255.0, 60 / 255.0, 1),
        Orange = new(230 / 255.0, 150 / 255.0, 50 / 255.0, 1);

    public string Id => "frames";

    public void Draw(HudUi ui, HudBody body)
    {
        var l = sampler.Live;
        ui.Space(6);
        ui.Grid(5, 5, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "FPS", N(l.Fps)),
            1 => ui.Stat(x, y, w, T("hud-v-avg-ms"), N(l.AvgMs, 1)),
            2 => ui.Stat(x, y, w, T("hud-v-p99-ms"), N(l.P99, 1)),
            3 => ui.Stat(x, y, w, "1% Low", N(l.Low1)),
            _ => ui.Stat(x, y, w, N(0.1, 1) + "% Low", N(l.Low01))
        });
        ui.Label(T("hud-v-graph-big", Math.Min(sampler.Frames.Recorded, HudCanvas.GraphFrames)));
        Graph(ui, sampler.Frames, ui.Px(110), true);
        ui.Label(T("hud-v-distribution"));
        ui.Segments(ui.Y, ui.Px(10),
        [
            (l.Under8, HudUi.Ok), (l.Under12, Yellow), (l.Under25, Orange),
            (Math.Max(l.Over25, l.Over25 > 0 ? 0.3 : 0), HudUi.Err)
        ]);
        ui.Y += ui.Px(12);
        ui.Grid(4, 4, 4, (i, x, y, _) =>
        {
            var (text, color) = i switch
            {
                0 => ("<8 ms " + N(l.Under8) + " %", HudUi.Dim),
                1 => ("8-12 ms " + N(l.Under12) + " %", HudUi.Dim),
                2 => ("12-25 ms " + N(l.Under25, 1) + " %", HudUi.Dim),
                _ => (">25 ms " + N(l.Over25, 1) + " %", HudUi.Err)
            };
            ui.Text(x, y, ui.F.SmallRow, ui.F.Small, text, color);
            return ui.F.SmallRow;
        });
        SpikeList(ui);
    }

    private void SpikeList(HudUi ui)
    {
        var spikes = sampler.Spikes;
        var stored = Math.Min(spikes.Stored, ShownSpikes);
        ui.Label(spikes.Count > 0 ? T("hud-v-spikes-count", spikes.Count) : T("hud-v-spikes"));
        if (stored == 0) ui.Line(T("hud-v-spikes-none"), HudUi.Dim);

        var now = capi.InWorldEllapsedMilliseconds / 1000.0;
        for (var age = 0; age < Math.Min(stored, ShownSpikes); age++)
        {
            var spike = spikes.Latest(age);
            var ms = N(spike.DtMs) + " ms";
            var right = T("hud-v-ago", N(Math.Max(0, now - spike.AtSeconds)));
            ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ms, HudUi.Warn);
            var x = ui.X + ui.TextW(ui.F.Body, ms) + ui.Px(5);
            ui.Text(x, ui.Y, ui.F.BodyRow, ui.F.Body,
                ui.Fit(ui.F.Body, Describe(spike.Cause), ui.X + ui.W - x - ui.TextW(ui.F.Body, right) - ui.Px(8)));
            ui.TextRight(ui.X + ui.W, ui.Y, ui.F.BodyRow, ui.F.Body, right, HudUi.Dim);
            ui.Y += ui.F.BodyRow;
        }
    }
}
