using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Api.KapiFeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Diagnostics;

// Diagnostics' features: the mod and render-cost probes the HUD reads, entity and frame clocks, the hang watch.
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class DiagnosticsFeatures
{
    internal static readonly Feature[] All =
    [
        // before the HUD, whose settings may switch the mod times on
        new("ModTimes")
        {
            Order = 10,
            Install = c => ModTimes.Install(c.Harmony, c.Api.ModLoader, c.Logger),
            Probe = () => ModTimes.Replays ? Active : EngineChanged,
            Recheck = ModTimes.Joined,
            Shapes =
            [
                new EngineBodies(() =>
                [
                    AccessTools.Method(typeof(ClientEventManager),
                        nameof(ClientEventManager.TriggerRenderStage))
                ], ModTimes.Shape)
            ]
        },
        // render cost panel probes; before the HUD, whose settings may enable them
        new("RenderCost")
        {
            Order = 20, Install = c => RenderCost.Install(c.Harmony, c.Logger), Stop = _ => RenderCost.Clear()
        },
        new("EntityTimes")
        {
            Order = 490,
            Install = c => EntityTimes.Install(c.Harmony),
            Probe = () => EntityTimes.Installed ? Active : EngineChanged
        },
        new("ProfilerPool")
        {
            Order = 980,
            Replaces = () =>
            [
                AccessTools.DeclaredMethod(typeof(FrameProfilerUtil), nameof(FrameProfilerUtil.Mark),
                    [typeof(string), typeof(object)])
            ],
            Install = c => ProfilerPool.Install(c.Harmony),
            Knobs =
            [
                Switch("ProfilerPool", "misc", "garbage", () => ProfilerPool.Enabled, on => ProfilerPool.Enabled = on)
            ],
            Probe = () => Found(ProfilerPool.Patched, false)
        },
        new("FrameClock") { Order = 1040, Install = c => FrameClock.Install(c.Harmony) },
        new("HangWatch") { Order = 1050, Install = c => HangWatch.Start(c.Logger), Stop = _ => HangWatch.Stop() }
    ];
}
