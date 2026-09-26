using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Komet.Test.Chunks;

// The rewrite runs against the game's real IL: installing it makes Harmony compile the replacement, so a malformed rewrite fails here
// instead of in a world, and a game update that changes the method's shape turns the feature off here first.
public sealed class LoadGarbageTests
{
    [Test]
    public void ChunkThreadClosureInstalls()
    {
        var harmony = new Harmony("komet-test-chunkthreadclosure");
        try
        {
            ChunkThreadClosure.Install(harmony);
            Assert.That(ChunkThreadClosure.Rewritten, Is.True, "the closure prologue or its uses no longer match");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
        }
    }

    // The closure is built only where the delegate needs it, and nothing reads chunkRequest back out of it any more
    [Test]
    public void ChunkThreadClosureIsBuiltOnlyForTheDelegate()
    {
        var target = ChunkThreadClosure.Target();
        Assert.That(target, Is.Not.Null);
        var code = ChunkThreadClosure.Rewrite(PatchProcessor.GetOriginalInstructions(target));
        var builds = code.Select((instruction, index) => (instruction, index))
            .Where(pair => pair.instruction.opcode == OpCodes.Newobj &&
                           pair.instruction.operand is ConstructorInfo { DeclaringType.Name: var name } &&
                           name.Contains("DisplayClass", StringComparison.Ordinal))
            .ToList();
        Assert.Multiple(() =>
        {
            Assert.That(ChunkThreadClosure.Rewritten, Is.True);
            Assert.That(builds, Has.Count.EqualTo(1));
            Assert.That(builds[0].index, Is.GreaterThan(100), "the closure is still built in the prologue");
            Assert.That(code[builds[0].index + 7].opcode, Is.EqualTo(OpCodes.Ldftn),
                "the fresh closure must go straight into the delegate");
            Assert.That(
                code.Count(instruction => instruction.operand is FieldInfo { Name: "chunkRequest" } &&
                                          instruction.opcode == OpCodes.Ldfld), Is.Zero);
        });
    }
}
