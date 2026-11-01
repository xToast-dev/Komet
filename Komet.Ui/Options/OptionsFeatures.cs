using HarmonyLib;
using Komet.Core;
using Komet.Hud;
using Vintagestory.Client.NoObf;
using static Komet.Core.FeatureKit;

namespace Komet.Options;

// The rebuilt settings menu and its backdrop blur.
// In install order; Core's Features places them among the other modules'.
internal static class OptionsFeatures
{
    internal static readonly Feature[] All =
    [
        new("GraphicsMenu")
        {
            Replaces = () =>
            [
                GraphicsMenu.Shaped()[0],
                AccessTools.Method(typeof(HudDebugScreen), nameof(HudDebugScreen.OnRenderGUI), [typeof(float)]),
                AccessTools.DeclaredMethod(typeof(KeyCombination), nameof(KeyCombination.ToString), [])
            ],
            Install = c =>
            {
                if (c.Overlay is HudOverlay overlay) GraphicsMenu.Install(c.Harmony, c.Api, overlay.Options, c.Logger);
            },
            Requires = ["Hud"],
            Knobs = [Switch("GraphicsMenu", "misc", "menu", () => GraphicsMenu.Enabled,
                on => GraphicsMenu.Enabled = on) with { General = true }],
            Stop = _ => GraphicsMenu.Clear(),
            Probe = () => Found(GraphicsMenu.Installed, GraphicsMenu.StoodDown),
            Shapes = [new EngineBodies(GraphicsMenu.Shaped, GraphicsMenu.Fingerprint)]
        },
        new("MenuBlur")
        {
            Knobs = [Switch("MenuBlur", "misc", "menu", () => Backdrop.Enabled, on => Backdrop.Enabled = on)
                with { General = true }]
        }
    ];
}
