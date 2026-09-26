using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Test.Tessellation;

// What TessWorkers rely on: every TessSafety patch finds its sites in the engine as installed, and two tesselators on two threads
// decode their chunks as each would alone - the palette table the engine keeps in one static field is one per thread now
public sealed class TessSafetyTests
{
    private const int Rounds = 300;

    [Test]
    public void EveryPatchFindsItsSites()
    {
        var harmony = new Harmony("komet-test-tesssafety-sites");
        try
        {
            TessSafety.Install(harmony);
            TessSchedule.Install(harmony);
            TessWorkers.Install(harmony);
            Assert.Multiple(() =>
            {
                Assert.That(TessSafety.Installed, Is.True, "an engine method no longer looks as TessSafety expects");
                Assert.That(TessWorkers.Installed, Is.True,
                    "TesselateChunk no longer reads game.TerrainChunkTesselator once");
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TessSafety.Clear();
            TessSchedule.Clear();
            TessWorkers.Stop();
        }
    }

    // A failed install leaves nothing behind: with one transpiler finding another count of sites, every patch made so far goes again
    [Test]
    public void AnIncompleteInstallUnpatchesEverything()
    {
        var harmony = new Harmony("komet-test-tesssafety-partial");
        var blocker = new Harmony("komet-test-tesssafety-blocker");
        var cross = AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross));
        try
        {
            _ = blocker.Patch(cross, transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreLoad)));
            TessSafety.Install(harmony);
            var decoder = AccessTools.DeclaredMethod(typeof(BlockChunkDataLayer), "getBlockOne");
            Assert.Multiple(() =>
            {
                Assert.That(TessSafety.Installed, Is.False);
                Assert.That(Harmony.GetPatchInfo(decoder)?.Owners ?? [], Does.Not.Contain(harmony.Id),
                    "the decoders read the engine's table");
            });
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            blocker.UnpatchAll(blocker.Id);
            TessSafety.Clear();
        }
    }

    // Another mod's transpiler arriving later makes Harmony run TessSafety's again: a count that no longer fits keeps the workers off
    [Test]
    public void ALaterForeignTranspilerBreaksTheWorkers()
    {
        var harmony = new Harmony("komet-test-tesssafety-late");
        var blocker = new Harmony("komet-test-tesssafety-late-blocker");
        try
        {
            TessSafety.Install(harmony);
            Assert.That((TessSafety.Installed, TessSafety.Broken), Is.EqualTo((true, false)));
            _ = blocker.Patch(AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross)),
                transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreLoad))
                { priority = Priority.First });
            Assert.That(TessSafety.Broken, Is.True);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            blocker.UnpatchAll(blocker.Id);
            TessSafety.Clear();
        }
    }

    // Another mod's transpiler that reads CrossTesselator.startRot once more
    private static IEnumerable<CodeInstruction> OneMoreLoad(IEnumerable<CodeInstruction> instructions)
    {
        var start = AccessTools.DeclaredField(typeof(CrossTesselator), "startRot");
        return new[] { new CodeInstruction(OpCodes.Ldsfld, start), new CodeInstruction(OpCodes.Pop) }
            .Concat(instructions);
    }

    // Two chunks whose palettes hold stone and granite in opposite order: with the engine's one static table, a thread decoding its
    // chunk while the other built the table for its own reads the other palette, and stone becomes granite
    [Test]
    public void TwoThreadsDecodeTheirOwnPalettes()
    {
        var harmony = new Harmony("komet-test-tesssafety-palette");
        using var a = new ChunkRig();
        using var b = new ChunkRig();
        try
        {
            TessSafety.Install(harmony);
            Assert.That(TessSafety.Installed, Is.True);
            var chunkA = a.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Stone : ChunkRig.Granite);
            var chunkB = b.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Granite : ChunkRig.Stone);
            var expectedA = Extended(a, chunkA);
            var expectedB = Extended(b, chunkB);
            Assert.That(expectedA, Is.Not.EqualTo(expectedB),
                "the two chunks must differ for the test to mean anything");
            var mismatches = 0;
            Parallel.Invoke(
                () => Interlocked.Add(ref mismatches, Repeat(a, chunkA, expectedA)),
                () => Interlocked.Add(ref mismatches, Repeat(b, chunkB, expectedB)));
            Assert.That(mismatches, Is.Zero);
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            TessSafety.Clear();
        }
    }

    private static int Repeat(ChunkRig rig, ClientChunk chunk, string[] expected)
    {
        var wrong = 0;
        for (var i = 0; i < Rounds; i++)
            if (!Extended(rig, chunk).SequenceEqual(expected))
                wrong++;
        return wrong;
    }

    // The centre of the extended block array after the engine's BuildExtendedChunkData, as block codes
    private static string[] Extended(ChunkRig rig, ClientChunk chunk)
    {
        BuildExtended(rig.Tesselator, chunk, 1, 1, 1, false, false);
        var cells = new string[32 * 32 * 32];
        for (var i = 0; i < cells.Length; i++)
        {
            int x = i % 32, z = i / 32 % 32, y = i / 1024;
            cells[i] = rig.BlocksExt[((y + 1) * 34 + z + 1) * 34 + x + 1]?.Code?.Path ?? "null";
        }

        return cells;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "BuildExtendedChunkData")]
    private static extern void BuildExtended(ChunkTesselator tesselator, ClientChunk chunk, int x, int y, int z,
        bool atMapEdge,
        bool skipChunkCenter);
}
