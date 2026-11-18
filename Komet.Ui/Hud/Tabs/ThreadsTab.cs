using Komet.Core;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// Every timed thread's busy share and ms per frame, the workers, the thread pool and lock contention
internal sealed class ThreadsTab(HudSettings settings, HudSampler sampler) : IHudTab
{
    public string Id => "threads";
    public HudNeeds Needs => HudNeeds.Threads;

    public void Draw(HudUi ui, HudBody body)
    {
        var l = sampler.Live;
        var main = l.KindBusy[0];
        var tess = Knobs.Find("TessPriority") is var k and >= 0 ? settings.Knobs.Knob(k) : 0;
        ui.Space(6);
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Meter(x, y, w, T("hud-v-main-thread"), N(main) + " %", main),
            1 => ui.Stat(x, y, w, T("hud-v-waits-core"), N(l.RunQueueMs, 2) + " ms", T("hud-v-per-frame")),
            _ => ui.Stat(x, y, w, T("hud-v-tess-worker-stat"), N(WorkerPool.Running), T("hud-v-priority", tess))
        });
        ui.Label(T("hud-v-all-threads"));
        Row(ui, T("hud-v-thread"), double.NaN, T("hud-v-busy"), "ms/F", true);
        for (var i = 0; i < Math.Min(l.ThreadCount, HudLive.MaxThreads); i++)
        {
            Row(ui, l.Threads[i].Name, l.Threads[i].Busy, N(l.Threads[i].Busy) + " %", N(l.Threads[i].MsPerFrame, 2),
                false);
        }

        Row(ui, T("hud-v-workers-frame", WorkerPool.Running), l.WorkerFrame, N(l.WorkerFrame) + " %", "", false);
        Row(ui, T("hud-v-workers-tess", WorkerPool.Running), l.WorkerTess, N(l.WorkerTess) + " %", "", false);
        if (l.ThreadCount == 0) ui.Line(T("hud-rc-nothreads"), HudUi.Dim, ui.F.Small);

        ui.Label(T("hud-v-pool-locks"));
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "Thread-Pool", N(ThreadPool.ThreadCount),
                T("hud-v-waiting", N(ThreadPool.PendingWorkItemCount))),
            1 => ui.Stat(x, y, w, T("hud-v-locks"), N(l.Locks) + "/s"),
            _ => ui.Stat(x, y, w, T("hud-v-gc-pause"), N(sampler.Resources.GcPausePercent, 1) + " %")
        });
    }

    private static void Row(HudUi ui, string name, double busy, string busyText, string ms, bool head)
    {
        if (!NotNull(name)) return;

        var (h, f) = (head ? ui.F.SmallRow : ui.F.BodyRow, head ? ui.F.Small : ui.F.Body);
        var barX = ui.X + ui.W - ui.Px(110 + 54 + 46 + 16);
        ui.Text(ui.X, ui.Y, h, f, ui.Fit(f, name, barX - ui.X - ui.Px(8)), head ? HudUi.Dim : null);
        if (!head) ui.Bar(barX, ui.Y, ui.Px(62), h, busy);

        ui.Text(barX + (head ? 0 : ui.Px(68)), ui.Y, h, f, busyText, head ? HudUi.Dim : null);
        ui.TextRight(ui.X + ui.W, ui.Y, h, f, ms, head ? HudUi.Dim : null);
        ui.Fill(ui.X, ui.Y + h, ui.W, Math.Max(1, ui.Px(1)), Rgba.White(0.05));
        ui.Y += h + ui.Px(2);
    }
}
