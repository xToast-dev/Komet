using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.Test.World;

// The rewrite runs against the game's real IL: installing it makes Harmony compile the replacement, so a malformed rewrite fails here
// instead of in a world, and a game update that changes the method's shape turns the feature off here first.
public sealed class CloudTileScratchTests
{
    [OneTimeSetUp]
    public void LoadEssentials()
    {
        // Komet finds the cloud renderer by name, which only works once the game has loaded VSEssentials; here the test does it
        var essentials = Assembly.Load("VSEssentials");
        Assert.That(AccessTools.TypeByName("FluffyClouds.CloudRendererMap"), Is.Not.Null, essentials.FullName);
    }

    [Test]
    public void Installs()
    {
        var harmony = new Harmony("komet-test-cloudtilescratch");
        try
        {
            CloudTileScratch.Install(harmony);
            Assert.That(CloudTileScratch.Rewritten, Is.True,
                "UpdateCloudTilesOffThread no longer builds exactly one Vec3d");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    // A vector the method could keep - stored in a static, or handed to anything but its fields and the weather readers - leaves the
    // engine's IL untouched
    [Test]
    public void AVectorThatCouldEscapeIsLeftAlone()
    {
        var (target, ctor) = (CloudTileScratch.Target()!, CloudTileScratch.Constructor());
        var store = PatchProcessor.GetOriginalInstructions(target)
            .FindIndex(c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor)) + 1;

        List<CodeInstruction> Variant(params CodeInstruction[] inserted)
        {
            var code = PatchProcessor.GetOriginalInstructions(target);
            code.InsertRange(store + 1, [new CodeInstruction(OpCodes.Ldloc, code[store].operand), .. inserted]);
            return code;
        }

        var stored =
            Variant(new CodeInstruction(OpCodes.Stsfld, AccessTools.Field(typeof(CloudTileScratch), "_scratch")));
        var cloned = Variant(
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Vec3d), nameof(Vec3d.Clone))),
            new CodeInstruction(OpCodes.Pop));
        Assert.Multiple(() =>
        {
            foreach (var (name, code) in new[] { ("stored", stored), ("handed on", cloned) })
            {
                var result = CloudTileScratch.Substitute(code);
                Assert.That(CloudTileScratch.Rewritten, Is.False, name);
                Assert.That(result.Count(c => c.opcode == OpCodes.Newobj && Equals(c.operand, ctor)), Is.EqualTo(1),
                    name);
            }
        });
    }

    [Test]
    public void ScratchIsReusedPerThread()
    {
        var first = CloudTileScratch.At(1, 2, 3);
        var second = CloudTileScratch.At(4, 5, 6);
        Vec3d? other = null;
        var thread = new Thread(() => other = CloudTileScratch.At(7, 8, 9));
        thread.Start();
        thread.Join();
        Assert.Multiple(() =>
        {
            Assert.That(second, Is.SameAs(first));
            Assert.That((second.X, second.Y, second.Z), Is.EqualTo((4d, 5d, 6d)));
            Assert.That(other, Is.Not.SameAs(first), "the main thread and the cloud thread must not share the vector");
        });
    }
}
