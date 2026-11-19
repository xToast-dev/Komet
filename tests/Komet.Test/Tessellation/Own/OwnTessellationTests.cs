using Komet.Core;
using Komet.Tessellation;
using Vintagestory.GameContent;

namespace Komet.Test.Tessellation;

// The engine-shape guards at install: a fingerprint other than the engine's keeps the replacements out
public sealed class OwnTessellationTests
{
    [TearDown]
    public void Reset() => OwnTessellation.Installed = false;

    [Test]
    public void CrossPatchLeavesTheOtherTessellatorsAvailable()
    {
        using var harmony = new TestHarmony("komet-test-own-cross-base");
        using var other = new TestHarmony("komet-test-own-cross-foreign");
        OwnTessellation.Install(harmony);
        var method = AccessTools.DeclaredMethod(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross));
        _ = other.Patch(method, postfix: new HarmonyMethod(typeof(OwnTessellationTests), nameof(AfterCross)));
        OwnTessellation.Recheck();
        Assert.That(OwnTessellation.Active, Is.True);
        Assert.That(OwnTessellation.CrossStoodDown, Is.True);
        other.UnpatchAll(other.Id);
        OwnTessellation.Recheck();
        Assert.That(OwnTessellation.CrossStoodDown, Is.False);
    }

    private static void AfterCross()
    {
        /* Compatibility probe only. */
    }

    private static void ColorCorners(TCTCache __instance) =>
        __instance.CurrentLightRGBByCorner.AsSpan(0, 4).Fill(12345);

}

// Komet's tesselators (OwnTessellation) against the engine's through NowProcessChunk on random worlds: the Cube slot's
// BleedingCubeTesselator, the layers' and Transparent's CubeTesselator, the TopsoilTesselator, the CrossTesselator, the
// JsonTesselator and the JsonAndSnowLayerTesselator, each world tessellated once with the game's slots and once, on fresh pools, with
// them swapped. Every part's meshes, the topsoil overlay shorts and the bounds (TessParallelTests.Digest) must be identical, with
// smooth shadows on (FaceLight's corners) and off, alternates on a cube, a layer, the grass, a cross and a JSON block, a randomly
// turned cross, a random draw offset, a colour map and snow above the topsoil. The JSON blocks get random models (faces with and
// without facing, on the boundary and inside, DisableRandoms and Liquid faces, wind bits): one with alternates, a lod 2 model and
// normal wind, one with snow under it, a lod 0 model where not surrounded, leaves wind and its own pass; and two whose overrides only
// rewrite the flags (TessSafety's FlagWriters), a BlockWithLeavesMotion with leaves wind and alternates and a BlockPlant with normal
// wind, each run meshing its own copy of the models (the overrides leave their flags in the shared mesh) under sunlight that differs
// by world.
[NonParallelizable]
public sealed class OwnTessellationGoldenTests
{
    private const int Worlds = 8,
        Soil = ChunkRig.Count,
        Grass = Soil + 1,
        SnowCube = Soil + 2,
        Layer = Soil + 3,
        Json = Soil + 4,
        JsonSnow = Soil + 5,
        Leaves = Soil + 6,
        Plant = Soil + 7,
        Cross = Soil + 8,
        Turned = Soil + 9,
        Extra = 10,
        Deep = 40;

    private static readonly TessBlocks Ids = new(Soil, Grass, SnowCube, Layer, Json, JsonSnow, Leaves, Cross, Turned, Plant);

    [TearDown]
    public void Reset() => (OwnTessellation.Enabled, OwnTessellation.Installed, Counting.Hud) = (true, false, false);

    [Test]
    public void CubesTopsoilAndJsonBlocksMatchTheEngine()
    {
        var r = new Random(20261007);
        using var harmony = new TestHarmony("komet-test-owntessellation");
        FaceLight.Install(harmony);
        OwnTessellation.Install(harmony);
        Assert.That((FaceLight.Installed, OwnTessellation.Installed, OwnTessellation.JsonInstalled),
            Is.EqualTo((true, true, true)));
        Counting.Hud = true;
        var (vertices, before, json) = (0, OwnTessellation.Blocks, OwnTessellation.JsonBlocks);
        for (var world = 0; world < Worlds; world++)
        {
            using var rig = new ChunkRig(Extra);
            Writers(rig);
            for (var c = 0; c < 27; c++)
            {
                var (cx, cy, cz) = (c % 3, c / 9, c / 3 % 3);
                rig.Put(cx, cy, cz, (x, y, z) => Pick(r, cx * 32 + x, cy * 32 + y, cz * 32 + z)).Lighting
                    .FillWithSunlight((ushort)((c * 7 + world * 5) % 25));
            }

            var (ao, seed) = (world % 2 == 0, r.Next());
            var engine = Run(Prepare(rig, ao, seed));
            var ours = Prepare(rig, ao, seed); // fresh models: the overrides wrote into the first run's
            Assert.That(OwnTessellation.Swap(ours.Tesselator), Is.EqualTo(13),
                "slots 1-8, 10-12, 14 and 17");
            Assert.That(Run(rig), Is.EqualTo(engine), $"world {world}");
            vertices += engine.Vertices;
        }

        Assert.Multiple(() =>
        {
            Assert.That(vertices, Is.GreaterThan(Worlds * 1000), "cubes and topsoil were drawn");
            Assert.That(OwnTessellation.Blocks - before, Is.GreaterThan(Worlds * 500), "by Komet's tesselators");
            Assert.That(OwnTessellation.JsonBlocks - json, Is.GreaterThan(Worlds * 100), "JSON blocks among them");
        });
    }

    private static TessParallelTests.Digest Run(ChunkRig rig)
    {
        var tess = rig.Tess(1, 1, 1);
        _ = rig.Tesselator.NowProcessChunk(1, 1, 1, tess, false);
        return TessParallelTests.Digest.Of(tess);
    }

    // Ground at y 40..47 (chunk row 1): topsoil under snow, a layer, flags writers or air, below it mixed cubes, layers, JSON blocks and
    // caves; leaves from y 40 on, whose override looks down seven cells and leaves the halo below it (to the world's accessor)
    private static int Pick(Random r, int x, int y, int z)
    {
        var ground = 40 + TessMix.Hash(x, 0, z) % 8;
        if (y > ground)
        {
            return y != ground + 1
                ? ChunkRig.Air
                : (TessMix.Hash(x, 1, z) % 9) switch
                {
                    0 => SnowCube,
                    1 => Layer,
                    2 => JsonSnow,
                    3 => Leaves,
                    4 => Plant,
                    5 => Cross,
                    6 => Turned,
                    _ => ChunkRig.Air
                };
        }

        if (y == ground) return r.Next(2) == 0 ? Soil : Grass;

        return r.Next(14) switch
        {
            0 => ChunkRig.Air, 1 => ChunkRig.Glass, 2 => ChunkRig.Slab, 3 => ChunkRig.Merge, 4 => ChunkRig.Granite,
            5 => Layer,
            6 => Json, 7 => JsonSnow, 8 when y >= Deep => Leaves, 9 => Plant, 10 => Cross, _ => ChunkRig.Stone
        };
    }

    private static ChunkRig Prepare(ChunkRig rig, bool ao, int seed) =>
        TessWorld.Prepare(rig, Ids, ao, TessWorld.Shapes(new Random(seed), rig.Blocks, Ids));

    // The flags writers in place of two of the rig's cubes, as see-through JSON blocks of their own material
    private static void Writers(ChunkRig rig)
    {
        Block[] writers = [new BlockWithLeavesMotion(), new BlockPlant()];
        for (var i = 0; i < writers.Length; i++)
        {
            var (id, block) = (Leaves + i, writers[i]);
            (block.BlockId, block.Code, block.DrawType, block.FaceCullMode, block.AllSidesOpaque) =
                (id, new AssetLocation("komet", "writer" + i), EnumDrawType.JSON, EnumFaceCullMode.Default, false);
            block.BlockMaterial = i == 0 ? EnumBlockMaterial.Leaves : EnumBlockMaterial.Plant;
            (rig.Blocks[id], rig.Game.Blocks[id]) = (block, block);
        }
    }
}
