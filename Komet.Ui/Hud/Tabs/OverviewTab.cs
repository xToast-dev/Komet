using Komet.Core;
using Komet.Diagnostics;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// The first tab: the big numbers, the graph and hints. A hint names what stands out and fixes it on click; it goes
// once its cause has.
internal sealed class OverviewTab(
    ICoreClientAPI capi,
    HudSettings settings,
    HudSampler sampler,
    HudBuild build,
    DebugCapture capture,
    Action<string> show) : IHudTab
{
    private const int MaxViewHints = 8, MaxModHints = 3;
    private const double ModHintMs = 2, GcHintPercent = 5, LowHintFps = 30, VramHintPercent = 90;
    private HarmonyAudit? _audit; // walked once per opening of the overview, for its conflicts hint
    private Task? _auditing;

    public string Id => "overview";

    public void Shown()
    {
        if (_auditing is { IsCompleted: false } || !NotNull(capi.ModLoader)) return;

        var loader = capi.ModLoader;
        _auditing = Task.Run(() => Volatile.Write(ref _audit, HarmonyAudit.Walk(loader)));
    }

    public void Draw(HudUi ui, HudBody body)
    {
        var (l, spikes) = (sampler.Live, sampler.Spikes);
        ui.Space(5);
        ui.Grid(4, 4, 5, (i, x, y, w) => i switch
        {
            0 => ui.Tile(x, y, w, N(l.Fps), "FPS"),
            1 => ui.Tile(x, y, w, N(l.Low1), "1% Low"),
            2 => ui.Tile(x, y, w, N(l.AvgMs, 1), T("hud-v-frametime-ms")),
            _ => ui.Tile(x, y, w, N(l.GpuMs, 1), T("hud-v-gpu-ms"))
        });
        ui.Label(T("hud-v-graph", Math.Min(sampler.Frames.Recorded, HudCanvas.GraphFrames)));
        Graph(ui, sampler.Frames, ui.Px(48), false);
        ui.Label(T("hud-v-hints"));
        if (spikes.Stored > 0)
        {
            var spike = spikes.Latest(0);
            var text = T("hud-v-spikes-last", spikes.Count, Describe(spike.Cause), N(spike.DtMs));
            var w = ui.TextW(ui.F.Body, text) + ui.Px(6);
            ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, text, ui.W - ui.Px(60)), HudUi.Warn);
            _ = ui.LinkText(Math.Min(ui.X + w, ui.X + ui.W - ui.Px(56)), ui.Y, T("hud-v-look"), () => show("frames"));
            ui.Space(0);
            ui.Y += ui.F.BodyRow;
        }

        var hints = Hints();
        foreach (var hint in hints.Bounded(MaxViewHints))
        {
            HintRow(ui, hint);
        }

        if (hints.Length == 0) ui.Line(T("hud-v-fine"), HudUi.Ok);

        ui.Space(4);
        _ = ui.LinkText(ui.X, ui.Y, T("hud-v-make-protocol"), () => show("protocol"));
        ui.Y += ui.F.BodyRow;
    }

    private static void HintRow(HudUi ui, Hint hint)
    {
        var chipW = hint.Act is null ? 0 : ui.TextW(ui.F.Small, hint.Chip) + ui.Px(14);
        var textW = ui.W - chipW - ui.Px(8);
        var top = ui.Y + ui.Px(2);
        ui.Y = top;
        ui.Text(ui.X, ui.Y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, hint.Title, textW), hint.Color);
        if (hint.Act is { } act) _ = ui.Chip(ui.X + ui.W - chipW, top + ui.Px(1), hint.Chip, false, act);

        ui.Y += ui.F.BodyRow;
        if (hint.Sub.Length > 0) ui.Line(ui.Fit(ui.F.Small, hint.Sub, textW), HudUi.Dim, ui.F.Small);

        ui.Space(2);
    }

    private Hint[] Hints()
    {
        var hints = new List<Hint>(MaxViewHints);
        var latency = Knobs.Find("GcLatency");
        if (latency >= 0 && settings.Knobs.Knob(latency) == 0)
        {
            hints.Add(new Hint(HudUi.Err, T("hud-v-hint-gclatency"), T("hud-v-hint-gclatency-sub"), T("hud-v-apply"),
                () => settings.Knobs.SetKnob(latency, 1)));
        }

        var resources = sampler.Resources;
        if (resources.GcPausePercent >= GcHintPercent)
        {
            hints.Add(new Hint(HudUi.Err, T("hud-v-hint-gc", N(resources.GcPausePercent, 1)), T("hud-v-hint-gc-sub"),
                T("hud-v-record"), () => capture.Capture("record", 10)));
        }

        TessHint(hints);
        // the protocol lists every patched method with its chain and risk; the hint names the first
        if (_audit is { } audit && audit.Methods.Count(static m => m.Risk == PatchRisk.High) is var risky and > 0)
        {
            hints.Add(new Hint(HudUi.Warn, T("hud-v-hint-conflicts", risky), RiskySample(audit), "", null));
        }

        ModHints(hints);
        StateHints(hints);
        // errors first, then warnings, each in the order above, so a cut drops the mildest
        return
        [
            .. hints.OrderBy(static h => h.Color != HudUi.Err).ThenBy(static h => h.Color != HudUi.Warn)
                .Take(MaxViewHints)
        ];
    }

    private void StateHints(List<Hint> hints)
    {
        var gpu = sampler.Gpu;
        if (100 * gpu.VramUsedMb / gpu.VramTotalMb >= VramHintPercent)
        {
            hints.Add(new Hint(HudUi.Err, T("hud-v-hint-vram", N(100 * gpu.VramUsedMb / gpu.VramTotalMb)),
                T("hud-v-hint-vram-sub"), "", null));
        }

        if (sampler.Live.Low1 < LowHintFps)
        {
            hints.Add(new Hint(HudUi.Warn, T("hud-v-hint-low", N(sampler.Live.Low1)), T("hud-v-hint-low-sub"),
                T("hud-v-arm"), SpikeCapture));
        }

        if (Features.Troubled > 0)
        {
            // the window shows the last protocol; only a fresh one has the Features section with what stood down
            hints.Add(new Hint(HudUi.Warn, T("hud-v-hint-features", Features.Troubled), Features.FirstTroubled(),
                T("hud-v-look"), () =>
                {
                    show("protocol");
                    capture.Capture("now", 0);
                }));
        }

        if (HudBuild.DebugBuild) hints.Add(new Hint(HudUi.Warn, T("hud-hint-debugbuild"), "", "", null));

        if (build.Update?.Report is { State: UpdateState.Outdated or UpdateState.Mismatch } report)
        {
            hints.Add(new Hint(report.State == UpdateState.Mismatch ? HudUi.Err : HudUi.Warn,
                T(report.State == UpdateState.Mismatch ? "hud-v-hint-mismatch" : "hud-v-hint-outdated", report.Newest),
                T("hud-v-hint-version-sub"), T("hud-v-look"), () => show("version")));
        }
    }

    // Armed at the overlay's threshold, with the window open on its progress
    private void SpikeCapture()
    {
        capture.Capture("spike", 0);
        show("protocol");
    }

    private void TessHint(List<Hint> hints)
    {
        if (!Assert(hints.Count <= MaxViewHints)) return;

        var priority = Knobs.Find("TessPriority");
        var runQueue = sampler.Live.RunQueueMs;
        if (priority < 0 || double.IsNaN(runQueue) || runQueue < 0.5 || settings.Knobs.Knob(priority) <= 25) return;

        var now = settings.Knobs.Knob(priority);
        hints.Add(new Hint(HudUi.Warn, T("hud-v-hint-runqueue", N(runQueue, 1)), T("hud-v-hint-runqueue-sub", now),
            T("hud-v-apply"), () => settings.Knobs.SetKnob(priority, 25)));
    }

    private void ModHints(List<Hint> hints)
    {
        if (!Assert(hints.Count <= MaxViewHints)) return;

        var added = 0;
        for (var i = 0; i < ModTimes.MaxMods && added < MaxModHints; i++)
        {
            var (mod, ms) = (sampler.Timings.ModName(i), sampler.Timings.ModMs(i));
            if (mod.Length == 0 || mod is "game" or KometMod.Id || double.IsNaN(ms) || ms < ModHintMs) continue;

            hints.Add(new Hint(Rgba.White(0.85), T("hud-v-hint-mod", ModName(mod), N(ms, 2)), T("hud-v-hint-mod-sub"),
                T("hud-v-record"), () => capture.Capture("record", 10)));
            added++;
        }
    }

    private string ModName(string id) =>
        NotNull(capi.ModLoader) && capi.ModLoader.GetMod(id)?.Info is { Name.Length: > 0 } info
            ? info.Name
            : id;

    private static string Owners(PatchedMethod method) =>
        string.Join(" × ", method.Chain.Select(static l => l.Owner).Distinct());

    private static string RiskySample(HarmonyAudit audit) =>
        audit.Methods.FirstOrDefault(static m => m.Risk == PatchRisk.High) is { } m
            ? Describe(m.Name) + ": " + Owners(m)
            : "";

    private readonly record struct Hint(Rgba Color, string Title, string Sub, string Chip, Action? Act);
}
