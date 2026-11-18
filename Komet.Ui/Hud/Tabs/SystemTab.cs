using System.Runtime;
using Komet.Core;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Hud.HudParts;

namespace Komet.Hud;

// CPU, memory, GC, world counts and the core threads' meters
internal sealed class SystemTab(ICoreClientAPI capi, HudSampler sampler) : IHudTab
{
    public string Id => "system";
    public HudNeeds Needs => HudNeeds.Threads;

    public void Draw(HudUi ui, HudBody body)
    {
        var (r, gpu) = (sampler.Resources, sampler.Gpu);

        static double Gb(double mb) => mb / 1024;

        ui.Space(6);
        ui.Grid(2, 6, 5, (i, x, y, w) => i switch
        {
            0 => ui.Meter(x, y, w, T("hud-v-cpu-process"), N(r.CpuPercent) + " %", r.CpuPercent),
            1 => ui.Meter(x, y, w, T("hud-v-cpu-system"), N(r.SystemCpuPercent) + " %", r.SystemCpuPercent),
            2 => ui.Meter(x, y, w, T("hud-v-ram"), N(Gb(r.UsedRamMb), 1) + " / " + N(Gb(r.TotalRamMb), 1) + " GB",
                r.PercentOfRam(r.UsedRamMb)),
            3 => ui.Meter(x, y, w, "VRAM", N(Gb(gpu.VramUsedMb), 1) + " / " + N(Gb(gpu.VramTotalMb)) + " GB",
                100 * gpu.VramUsedMb / gpu.VramTotalMb),
            4 => ui.Meter(x, y, w, T("hud-v-working-set"), N(Gb(r.WorkingSetMb), 1) + " GB",
                r.PercentOfRam(r.WorkingSetMb)),
            _ => ui.Meter(x, y, w, T("hud-v-gc-pause"), N(r.GcPausePercent, 1) + " %", r.GcPausePercent * 10)
        });
        ui.Label(T("hud-v-memory"));
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "Managed Heap", N(Gb(r.ManagedUsedMb), 1) + " GB",
                T("hud-v-reserved", N(Gb(r.ManagedCommittedMb), 1))),
            1 => ui.Stat(x, y, w, T("hud-v-alloc"), N(r.AllocatedMbPerSec) + " MB/s",
                T("hud-v-per-frame-mb", N(r.AllocatedMbPerSec / Math.Max(1, sampler.Live.Fps), 1))),
            _ => ui.Stat(x, y, w, "GC", "Gen0 " + N(r.Gen0PerSec, 1) + "/s", "Gen2 " + N(r.Gen2PerSec, 2) + "/s · " +
                                                                             GCSettings.LatencyMode)
        });
        World(ui);
        ThreadMeters(ui);
    }

    private void World(HudUi ui)
    {
        ui.Label(T("hud-v-world"));
        var loaded = RuntimeStats.chunksReceived - RuntimeStats.chunksUnloaded;
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Stat(x, y, w, "Chunks", N(loaded),
                T("hud-v-chunks-sub", N(RuntimeStats.chunksAwaitingTesselation),
                    N(RuntimeStats.chunksAwaitingPooling))),
            1 => ui.Stat(x, y, w, "Entities", N(capi.World.LoadedEntities.Count),
                T("hud-v-visible", N(RuntimeStats.renderedEntities))),
            _ => ui.Stat(x, y, w, "Server", T(KometMod.LocalServer ? "hud-v-local" : "hud-v-remote"),
                T("hud-v-viewdistance", ClientSettings.ViewDistance))
        });
    }

    private void ThreadMeters(HudUi ui)
    {
        ui.Label(T("hud-v-threads-core"));
        var l = sampler.Live;
        var (main, gl) = (l.KindBusy[0], l.KindBusy[1]); // RenderCost's kinds: main, glthread
        ui.Grid(3, 3, 5, (i, x, y, w) => i switch
        {
            0 => ui.Meter(x, y, w, "Main", N(main) + " %", main),
            1 => ui.Meter(x, y, w, T("hud-v-tess-workers", WorkerPool.Running), N(l.WorkerTess) + " %", l.WorkerTess),
            _ => ui.Meter(x, y, w, "glthread", N(gl) + " %", gl)
        });
    }
}
