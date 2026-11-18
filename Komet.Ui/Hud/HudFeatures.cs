using Komet.Core;
using static Komet.Core.FeatureKit;

namespace Komet.Hud;

// The HUD itself (its overlay owns the settings and windows; building it loads komet.json, so every feature ordered
// after it installs with the player's knob values) and the interaction-help recompose skip.
internal static class HudFeatures
{
    private const int StageCount = 3;

    private static readonly EnumRenderStage[] Stages =
        [EnumRenderStage.Before, EnumRenderStage.Ortho, EnumRenderStage.Done];

    internal static readonly Feature[] All =
    [
        // after the probes its settings switch on
        new("Hud")
        {
            Order = 30,
            After = ["ModTimes", "RenderCost"],
            Install = c => Start(c.Api),
            Stop = c => End(c?.Api)
        },
        new("HelpCompose")
        {
            Order = 300,
            Install = c => HelpCompose.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("HelpCompose", "render", "drawing", () => HelpCompose.Enabled, on => HelpCompose.Enabled = on)
            ],
            Recheck = HelpCompose.Recheck,
            Probe = () => Found(HelpCompose.Matched, HelpCompose.Blocked),
            Shapes = [new EngineBodies(HelpCompose.Seams, HelpCompose.Fingerprint)]
        }
    ];

    // The world's overlay from the Hud feature's install to its stop: what the features of this module after it (the
    // settings menu) build on
    internal static HudOverlay? Overlay { get; private set; }

    // RegisterRenderer throws when another mod took the HUD's order: the install fails, Komet stands down
    private static void Start(ICoreClientAPI api)
    {
        Overlay = new HudOverlay(api);
        foreach (var stage in Stages.Bounded(StageCount))
        {
            api.Event.RegisterRenderer(Overlay, stage, "komet-hud");
        }
    }

    private static void End(ICoreClientAPI? api)
    {
        var overlay = Overlay;
        Overlay = null;
        if (overlay is null) return;

        if (api is not null) Detach(api, overlay);

        overlay.Dispose();
    }

    private static void Detach(ICoreClientAPI api, HudOverlay overlay)
    {
        foreach (var stage in Stages.Bounded(StageCount))
        {
            Features.Cleanup("Hud", "detach HUD renderer", () => api.Event.UnregisterRenderer(overlay, stage));
        }
    }
}
