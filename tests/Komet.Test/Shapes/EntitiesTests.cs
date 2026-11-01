using Komet.Shapes;
using Vintagestory.GameContent;

namespace Komet.Test.Shapes;

// Both DoRender3DOpaque (the base and the player's) call RenderHeldItem twice; each call must become HeldItemsLast's, or
// held items would draw twice or not at all
[NonParallelizable]
public sealed class HeldItemsLastTests
{
    [Test]
    public void BothOpaquePassesHandTheirHeldItemsOver()
    {
        using var harmony = new TestHarmony("komet-test-helditemslast");
        HeldItemsLast.Install(harmony, new QuietLogger());
        Assert.That(HeldItemsLast.Complete, Is.True, "EntityShapeRenderer or EntityPlayerShapeRenderer.DoRender3DOpaque changed");
        foreach (var type in new[] { typeof(EntityShapeRenderer), typeof(EntityPlayerShapeRenderer) })
        {
            var body = PatchProcessor.GetCurrentInstructions(AccessTools.DeclaredMethod(type, "DoRender3DOpaque"));
            Assert.That(body.Count(c => c.operand is MethodInfo { Name: "RenderHeldItem" }), Is.Zero, type.Name);
            Assert.That(body.Count(c => c.Calls(AccessTools.Method(typeof(HeldItemsLast), nameof(HeldItemsLast.Drawn)))),
                Is.EqualTo(2), type.Name);
        }
    }
}
