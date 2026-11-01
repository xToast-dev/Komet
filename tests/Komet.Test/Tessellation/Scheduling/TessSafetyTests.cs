using Komet.Tessellation;

namespace Komet.Test.Tessellation;

// What TessWorkers rely on: every TessSafety patch finds its sites in the engine as installed, and two tesselators on two threads
// decode their chunks as each would alone - the palette table the engine keeps in one static field is one per thread now
public sealed class TessSafetyTests
{
    private const int Rounds = 300;

    [TearDown]
    public void Clear()
    {
        TessSafety.Clear();
        TessSchedule.Clear();
        TessWorkers.Stop();
    }

    [Test]
    public void EveryPatchFindsItsSites()
    {
        using var harmony = new TestHarmony("komet-test-tesssafety-sites");
        TessSafety.Install(harmony);
        TessSchedule.Install(harmony);
        TessWorkers.Install(harmony);
        Assert.Multiple(() =>
        {
            Assert.That(TessSafety.Installed, Is.True, "an engine method no longer looks as TessSafety expects");
            Assert.That(TessWorkers.Installed, Is.True, "TesselateChunk no longer reads game.TerrainChunkTesselator once");
        });
    }

    [Test]
    public void AnIncompleteInstallUnpatchesEverything()
    {
        using var harmony = new TestHarmony("komet-test-tesssafety-partial");
        using var blocker = new TestHarmony("komet-test-tesssafety-blocker");
        _ = blocker.Patch(AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross)),
            transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreLoad)));
        TessSafety.Install(harmony);
        var decoder = AccessTools.DeclaredMethod(typeof(BlockChunkDataLayer), "getBlockOne");
        Assert.Multiple(() =>
        {
            Assert.That(TessSafety.Installed, Is.False);
            Assert.That(Harmony.GetPatchInfo(decoder)?.Owners ?? [], Does.Not.Contain(harmony.Id),
                "the decoders read the engine's table");
        });
    }

    // Another mod's transpiler arriving later makes Harmony run TessSafety's again: a count that no longer fits keeps the workers off
    [Test]
    public void ALaterForeignTranspilerBreaksTheWorkers()
    {
        using var harmony = new TestHarmony("komet-test-tesssafety-late");
        using var blocker = new TestHarmony("komet-test-tesssafety-late-blocker");
        TessSafety.Install(harmony);
        Assert.That((TessSafety.Installed, TessSafety.Broken), Is.EqualTo((true, false)));
        _ = blocker.Patch(AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross)),
            transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreLoad)) { priority = Priority.First });
        Assert.That(TessSafety.Broken, Is.True);
    }

    // A later foreign transpiler on the decoders or the palette build undoes that rewrite (Broken): the build writes the engine's table
    // as well and the decoders read that one then, so a chunk still decodes with its own palette
    [TestCase(false)]
    [TestCase(true)]
    public void AnUndoneRewriteStillDecodesItsOwnPalette(bool build)
    {
        using var harmony = new TestHarmony("komet-test-tesssafety-undone");
        using var blocker = new TestHarmony("komet-test-tesssafety-undone-blocker");
        using var a = new ChunkRig();
        using var b = new ChunkRig();
        TessSafety.Install(harmony);
        var chunkA = a.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Stone : ChunkRig.Granite);
        var chunkB = b.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Granite : ChunkRig.Stone);
        var (expectedA, expectedB) = (Extended(a, chunkA), Extended(b, chunkB));
        string[] decoders = ["getBlockOne", "getBlockTwo", "getBlockThree", "getBlockFour", "getBlockFive", "getBlockGeneralCase"];
        MethodInfo[] targets = build
            ? [AccessTools.DeclaredMethod(TessSeams.ClientData, "BuildFastBlockAccessArray")]
            : [.. decoders.Select(name => AccessTools.DeclaredMethod(typeof(BlockChunkDataLayer), name))];
        foreach (var target in targets)
            _ = blocker.Patch(target,
                transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMorePaletteLoad)) { priority = Priority.First });
        Assert.That(TessSafety.Broken, Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(Extended(a, chunkA), Is.EqualTo(expectedA));
            Assert.That(Extended(b, chunkB), Is.EqualTo(expectedB));
        });
    }

    // Another mod's transpiler on TesselateChunk arriving later makes Harmony run TessWorkers' again: a second read of the engine's
    // tesselator leaves the IL reading it on every thread, so the workers stay off
    [Test]
    public void ALaterForeignTranspilerKeepsTheWorkerTesselatorsOff()
    {
        using var harmony = new TestHarmony("komet-test-tessworkers-late");
        using var blocker = new TestHarmony("komet-test-tessworkers-late-blocker");
        TessSafety.Install(harmony);
        TessSchedule.Install(harmony);
        TessWorkers.Install(harmony);
        Assert.That((TessWorkers.Installed, TessWorkers.Broken), Is.EqualTo((true, false)));
        var (i, b) = (typeof(int), typeof(bool));
        _ = blocker.Patch(AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.TesselateChunk),
                [i, i, i, b, b, b.MakeByRefType()]),
            transpiler: new HarmonyMethod(typeof(TessSafetyTests), nameof(OneMoreTesselatorLoad)) { priority = Priority.First });
        Assert.That(TessWorkers.Broken, Is.True);
    }

    // Another mod's patch on Block's own OnJsonTesselation locks only the flag writers whose override calls it (BlockFernTree's), not
    // BlockPlant, whose override never reaches it (player log: every plant meshed one thread at a time)
    [Test]
    public void APatchOnBlocksOwnHookLocksOnlyItsCallers()
    {
        using var other = new TestHarmony("komet-test-tesssafety-jsonhook");
        _ = other.Patch(AccessTools.DeclaredMethod(typeof(Block), nameof(Block.OnJsonTesselation), TessSafety.JsonArgs),
            prefix: new HarmonyMethod(typeof(TessSafetyTests), nameof(Nothing)));
        var plant = (Block)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.GameContent.BlockPlant));
        var fernTree = (Block)RuntimeHelpers.GetUninitializedObject(typeof(Vintagestory.GameContent.BlockFernTree));
        (plant.BlockId, fernTree.BlockId) = (1, 2);
        Assert.That((TessSafety.WritesFlags(plant), TessSafety.WritesFlags(fernTree)), Is.EqualTo((true, false)));
    }

    private static void Nothing()
    {
        // another mod's patch: only being there counts
    }

    // Another mod's transpiler that reads CrossTesselator.startRot once more
    private static IEnumerable<CodeInstruction> OneMoreLoad(IEnumerable<CodeInstruction> instructions)
    {
        var start = AccessTools.DeclaredField(typeof(CrossTesselator), "startRot");
        return new[] { new CodeInstruction(OpCodes.Ldsfld, start), new CodeInstruction(OpCodes.Pop) }
            .Concat(instructions);
    }

    // Another mod's transpiler that reads the engine's palette table once more
    private static IEnumerable<CodeInstruction> OneMorePaletteLoad(IEnumerable<CodeInstruction> instructions)
    {
        var table = AccessTools.DeclaredField(typeof(BlockChunkDataLayer), "blocksByPaletteIndex");
        return new[] { new CodeInstruction(OpCodes.Ldsfld, table), new CodeInstruction(OpCodes.Pop) }.Concat(instructions);
    }

    // Another mod's transpiler that reads game.TerrainChunkTesselator once more
    private static IEnumerable<CodeInstruction> OneMoreTesselatorLoad(IEnumerable<CodeInstruction> instructions)
    {
        var game = AccessTools.Field(typeof(ChunkTesselatorManager), "game");
        var tesselator = AccessTools.DeclaredField(typeof(ClientMain), nameof(ClientMain.TerrainChunkTesselator));
        return new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, game),
            new CodeInstruction(OpCodes.Ldfld, tesselator), new CodeInstruction(OpCodes.Pop)
        }.Concat(instructions);
    }

    // Two chunks whose palettes hold stone and granite in opposite order: with the engine's one static table, a thread decoding its
    // chunk while the other built the table for its own reads the other palette, and stone becomes granite
    [Test]
    [Category("Slow")]
    public void TwoThreadsDecodeTheirOwnPalettes()
    {
        using var harmony = new TestHarmony("komet-test-tesssafety-palette");
        using var a = new ChunkRig();
        using var b = new ChunkRig();
        TessSafety.Install(harmony);
        Assert.That(TessSafety.Installed, Is.True);
        var chunkA = a.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Stone : ChunkRig.Granite);
        var chunkB = b.Put(1, 1, 1, (x, y, z) => ((x + y + z) & 1) == 0 ? ChunkRig.Granite : ChunkRig.Stone);
        var (expectedA, expectedB) = (Extended(a, chunkA), Extended(b, chunkB));
        Assert.That(expectedA, Is.Not.EqualTo(expectedB), "the two chunks must differ for the test to mean anything");
        var mismatches = 0;
        Parallel.Invoke(() => Interlocked.Add(ref mismatches, Repeat(a, chunkA, expectedA)),
            () => Interlocked.Add(ref mismatches, Repeat(b, chunkB, expectedB)));
        Assert.That(mismatches, Is.Zero);
    }

    private static int Repeat(ChunkRig rig, ClientChunk chunk, string[] expected)
    {
        var wrong = 0;
        for (var i = 0; i < Rounds; i++)
            if (!Extended(rig, chunk).SequenceEqual(expected))
                wrong++;
        return wrong;
    }

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
        bool atMapEdge, bool skipChunkCenter);
}
