using HarmonyLib;
using Komet.Core;
using Komet.Hud;
using Vintagestory.Client.NoObf;
using static Komet.Core.FeatureKit;

namespace Komet.Options;

// The rebuilt settings menu and its backdrop blur.
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class OptionsFeatures
{
    internal static readonly Feature[] All =
    [
        new("GraphicsMenu")
        {
            Order = 40,
            Replaces = () =>
            [
                GraphicsMenu.Shaped()[0],
                AccessTools.Method(typeof(HudDebugScreen), nameof(HudDebugScreen.OnRenderGUI), [typeof(float)]),
                AccessTools.DeclaredMethod(typeof(KeyCombination), nameof(KeyCombination.ToString), [])
            ],
            Install = c =>
            {
                if (HudFeatures.Overlay is { } overlay)
                {
                    GraphicsMenu.Install(c.Harmony, c.Api, overlay.Options, c.Logger);
                }
            },
            Requires = ["Hud"], // its overlay's options screen
            Knobs =
            [
                Switch("GraphicsMenu", "misc", "menu", () => GraphicsMenu.Enabled,
                        on => GraphicsMenu.Enabled = on) with
                    {
                        General = true
                    }
            ],
            Stop = _ => GraphicsMenu.Clear(),
            Probe = () => Found(GraphicsMenu.Installed, GraphicsMenu.StoodDown),
            Shapes = [new EngineBodies(GraphicsMenu.Shaped, GraphicsMenu.Fingerprint)]
        },
        new("MenuBlur")
        {
            Order = 50,
            Knobs =
            [
                Switch("MenuBlur", "misc", "menu", () => Backdrop.Enabled, on => Backdrop.Enabled = on)
                    with
                    {
                        General = true
                    }
            ]
        }
    ];
}
