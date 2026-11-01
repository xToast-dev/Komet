using Komet.Api;
using Komet.Core;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Shapes;

// Shapes and entities: AnimationFrames, ShapeInitMemo, HeldItemsLast, BushShapes, InventoryShapes
// and IconBudget.
// In install order; Core's Features places them among the other modules'.
internal static class ShapeFeatures
{
    // What the probes look up by reflection, once per world's install: the probe runs every 250 ms
    private static readonly ProbeTarget BushBuilder = new(BushShapes.Builder),
        CreatureMeshes = new(static () => InventoryShapes.Method("Vintagestory.GameContent.ItemCreature", "CreateOverlaidMeshRef"));

    internal static readonly Feature[] All =
    [
        new("AnimationFrames")
        {
            Install = c => AnimationFrames.Install(c.Harmony, c.Logger),
            Knobs = [Switch("AnimationFrames", "misc", "entities", () => AnimationFrames.Enabled,
                on => AnimationFrames.Enabled = on)],
            Recheck = AnimationFrames.Recheck,
            Stop = _ => AnimationFrames.Clear(),
            Probe = () => Found(AnimationFrames.Matched, AnimationFrames.Blocked),
            Shapes = [new EngineBodies(AnimationFrames.Shaped, AnimationFrames.Fingerprint)]
        },
        new("ShapeInitMemo")
        {
            Install = c => ShapeInitMemo.Install(c.Harmony, c.Logger),
            Knobs = [Switch("ShapeInitMemo", "misc", "entities", () => ShapeInitMemo.Enabled,
                on => ShapeInitMemo.Enabled = on)],
            Recheck = ShapeInitMemo.Recheck,
            Probe = () => Found(ShapeInitMemo.Matched, ShapeInitMemo.Blocked),
            Shapes = [new EngineBodies(ShapeInitMemo.Shaped, ShapeInitMemo.Fingerprint)]
        },
        new("HeldItemsLast")
        {
            Replaces = () => HeldItemsLast.Replaced,
            Rivals = PatchKinds.Body,
            Install = c => HeldItemsLast.Install(c.Harmony, c.Logger),
            Knobs = [Switch("HeldItemsLast", "misc", "entities", () => HeldItemsLast.Enabled, on => HeldItemsLast.Enabled = on)],
            Probe = () => HeldItemsLast.Complete ? Active : EngineChanged
        },
        new("BushShapes")
        {
            Replaces = () => [BushShapes.Builder()],
            Rivals = PatchKinds.Body,
            Install = c => BushShapes.Install(c.Harmony),
            Knobs = [Switch("BushShapes", "chunks", "tessellation", () => BushShapes.Enabled,
                on => BushShapes.Enabled = on)],
            Stop = _ => BushShapes.Clear(),
            Probe = () => BushBuilder.Missing ? NotApplicable : Found(BushShapes.Rewritten, false)
        },
        new("InventoryShapes")
        {
            Replaces = () => InventoryShapes.Replaced,
            Rivals = PatchKinds.Body,
            Install = c => InventoryShapes.Install(c.Harmony),
            Knobs = [Switch("InventoryShapes", "misc", "inventory", () => InventoryShapes.Enabled,
                on => InventoryShapes.Enabled = on)],
            Probe = () => CreatureMeshes.Missing
                ? NotApplicable
                : Found(InventoryShapes.Creatures && InventoryShapes.Wearables, false)
        },
        new("IconBudget")
        {
            Replaces = () => [IconBudget.Target()],
            Install = c => IconBudget.Install(c.Harmony),
            Knobs = [Switch("IconBudget", "misc", "inventory", () => IconBudget.Enabled, on => IconBudget.Enabled = on)
                with { Weighed = true }],
            Probe = () => Found(IconBudget.Patched, false)
        }
    ];
}
