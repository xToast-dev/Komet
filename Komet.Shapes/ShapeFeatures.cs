using Komet.Api;
using Komet.Core;
using static Komet.Api.KapiFeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Shapes;

// Shapes and entities: AnimationFrames, ShapeInitMemo, KeyframeShare, HeldItemsLast, BushShapes, InventoryShapes
// and IconBudget.
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class ShapeFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget BushBuilder = new(BushShapes.Builder),
        CreatureMeshes = new(static () =>
            InventoryShapes.Method("Vintagestory.GameContent.ItemCreature", "CreateOverlaidMeshRef"));

    internal static readonly Feature[] All =
    [
        new("AnimationFrames")
        {
            Order = 450,
            Install = c => AnimationFrames.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("AnimationFrames", "misc", "entities", () => AnimationFrames.Enabled,
                    on => AnimationFrames.Enabled = on)
            ],
            Recheck = AnimationFrames.Recheck,
            Stop = _ => AnimationFrames.Clear(),
            Probe = () => Found(AnimationFrames.Matched, AnimationFrames.Blocked),
            Shapes = [new EngineBodies(AnimationFrames.Shaped, AnimationFrames.Fingerprint)]
        },
        new("ShapeInitMemo")
        {
            Order = 460,
            Install = c => ShapeInitMemo.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("ShapeInitMemo", "misc", "entities", () => ShapeInitMemo.Enabled,
                    on => ShapeInitMemo.Enabled = on)
            ],
            Recheck = ShapeInitMemo.Recheck,
            Probe = () => Found(ShapeInitMemo.Matched, ShapeInitMemo.Blocked),
            Shapes = [new EngineBodies(ShapeInitMemo.Shaped, ShapeInitMemo.Fingerprint)]
        },
        // equal keyframe elements of every shape loaded from now on are one object
        new("KeyframeShare")
        {
            Order = 470,
            Install = c => KeyframeShare.Install(c.Harmony),
            Knobs =
            [
                Switch("KeyframeShare", "misc", "entities", () => KeyframeShare.Enabled,
                    on => KeyframeShare.Enabled = on)
            ],
            Stop = _ => KeyframeShare.Stop(),
            Probe = () => KeyframeShare.Installed ? Active : EngineChanged
        },
        new("HeldItemsLast")
        {
            Order = 480,
            Replaces = () => HeldItemsLast.Replaced,
            Rivals = KapiPatchKinds.Body,
            Install = c => HeldItemsLast.Install(c.Harmony, c.Logger),
            Knobs =
            [
                Switch("HeldItemsLast", "misc", "entities", () => HeldItemsLast.Enabled,
                    on => HeldItemsLast.Enabled = on)
            ],
            Probe = () => HeldItemsLast.Complete ? Active : EngineChanged
        },
        new("BushShapes")
        {
            Order = 640,
            Replaces = () => [BushShapes.Builder()],
            Rivals = KapiPatchKinds.Body,
            Install = c => BushShapes.Install(c.Harmony),
            Knobs =
            [
                Switch("BushShapes", "chunks", "tessellation", () => BushShapes.Enabled,
                    on => BushShapes.Enabled = on)
            ],
            Stop = _ => BushShapes.Clear(),
            Probe = () => BushBuilder.Missing ? NotApplicable : Found(BushShapes.Rewritten, false)
        },
        new("InventoryShapes")
        {
            Order = 900,
            Replaces = () => InventoryShapes.Replaced,
            Rivals = KapiPatchKinds.Body,
            Install = c => InventoryShapes.Install(c.Harmony),
            Knobs =
            [
                Switch("InventoryShapes", "misc", "inventory", () => InventoryShapes.Enabled,
                    on => InventoryShapes.Enabled = on)
            ],
            Probe = () => CreatureMeshes.Missing
                ? NotApplicable
                : Found(InventoryShapes.Creatures && InventoryShapes.Wearables, false)
        },
        new("IconBudget")
        {
            Order = 920,
            Replaces = () => [IconBudget.Target()],
            Install = c => IconBudget.Install(c.Harmony),
            Knobs =
            [
                Switch("IconBudget", "misc", "inventory", () => IconBudget.Enabled, on => IconBudget.Enabled = on)
                    with
                    {
                        Weighed = true
                    }
            ],
            Probe = () => Found(IconBudget.Patched, false)
        }
    ];
}
