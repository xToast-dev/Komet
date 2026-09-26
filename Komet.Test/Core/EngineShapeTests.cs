using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Test.Core;

// The engine-seam guard itself: a fingerprint is stable for a body and tells bodies apart, pins many bodies at once, and the patch
// check counts exactly the patches of the kinds asked that are not the caller's own. The features pin their own fingerprints and test
// their own stand-down.
public sealed class EngineShapeTests
{
    [Test]
    public void FingerprintIsStableAndTellsMethodsApart()
    {
        var visible = VisibleFaces.Target();
        var fluids = AccessTools.DeclaredMethod(typeof(ChunkTesselator),
            nameof(ChunkTesselator.CalculateVisibleFaces_Fluids));
        var emit = AccessTools.DeclaredMethod(typeof(Block), nameof(Block.DoEmitSideAo));
        var byFlag = AccessTools.DeclaredMethod(typeof(Block), nameof(Block.DoEmitSideAoByFlag));
        Assert.Multiple(() =>
        {
            var first = EngineShape.Of(visible);
            Assert.That(EngineShape.Of(visible), Is.Not.Zero.And.EqualTo(first), "the same body, the same fingerprint");
            Assert.That(EngineShape.Of(visible), Is.Not.EqualTo(EngineShape.Of(fluids)),
                "the fluid twin differs in a few instructions");
            Assert.That(EngineShape.Of(emit), Is.Not.EqualTo(EngineShape.Of(byFlag)), "two near-identical one-liners");
            Assert.That(EngineShape.Of((MethodBase?)null), Is.Zero);
            Assert.That(EngineShape.Of(AccessTools.Method(typeof(IGeometryTester),
                nameof(IGeometryTester.GetCurrentBlockEntityOnSide),
                [typeof(BlockFacing)])), Is.Zero, "no body");
            Assert.That(EngineShape.Of([visible, null]), Is.Zero, "a missing method");
            Assert.That(EngineShape.Of([visible, fluids]), Is.Not.EqualTo(EngineShape.Of([fluids, visible])),
                "in order");
        });
    }

    private static bool RunOriginal()
    {
        return true;
    }

    // The guard's patch check on the sweep's method, counted as each copy it replaces counts: a prefix of the same class is its own
    // (VisibleFaces, FaceLight, ExtendedRows), one of another class under the same id is not; under an own id no patch is
    // (ColumnNoiseScratch, ShapeInitMemo, InitOnce); prefixes and postfixes leave the body (AnimationFrames' and OccludedChunks' seam
    // 0); with nobody own, any patch counts (FaceLight's AO methods and ForFluidsLayer overrides); a missing seam is foreign
    [Test]
    public void ForeignCountsOtherPatchesOfTheKindsAsked()
    {
        var target = VisibleFaces.Target();
        Assert.That(Harmony.GetPatchInfo(target!)?.Owners, Is.Null.Or.Empty, "a patch another test left behind");
        var (own, other) = (new Harmony("komet-test-foreign-own"), new Harmony("komet-test-foreign-other"));
        const EngineShape.Kinds replacing = EngineShape.Kinds.Replacing,
            all = EngineShape.Kinds.All,
            body = EngineShape.Kinds.Body;
        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(EngineShape.Foreign([target], all, null), Is.False, "no patch");
                Assert.That(EngineShape.Foreign([target, null], all, null), Is.True, "a missing seam");
            });
            _ = own.Patch(target, new HarmonyMethod(typeof(EngineShapeTests), nameof(RunOriginal)));
            Assert.Multiple(() =>
            {
                Assert.That(EngineShape.Foreign([target], replacing, null, typeof(EngineShapeTests)), Is.False,
                    "its own class");
                Assert.That(EngineShape.Foreign([target], replacing, null, typeof(VisibleFaces)), Is.True,
                    "another class, the same id");
                Assert.That(EngineShape.Foreign([target], all, own.Id), Is.False, "its own id");
                Assert.That(EngineShape.Foreign([target], body, null), Is.False, "a prefix leaves the body");
                Assert.That(EngineShape.Foreign([target], all, null), Is.True, "any patch");
            });
            _ = other.Patch(target, postfix: new HarmonyMethod(typeof(Other), nameof(Other.After)));
            Assert.Multiple(() =>
            {
                Assert.That(EngineShape.Foreign([target], replacing, own.Id), Is.False,
                    "a postfix sees the same result");
                Assert.That(EngineShape.Foreign([target], all, own.Id), Is.True);
                Assert.That(EngineShape.Foreign([target], all, null, typeof(EngineShapeTests), typeof(Other)), Is.False,
                    "both classes own");
            });
            _ = other.Patch(target, transpiler: new HarmonyMethod(typeof(Other), nameof(Other.Same)));
            Assert.Multiple(() =>
            {
                Assert.That(EngineShape.Foreign([target], body, null), Is.True, "a rewritten body");
                Assert.That(EngineShape.Foreign([target], body, other.Id), Is.False);
            });
        }
        finally
        {
            own.UnpatchAll(own.Id);
            other.UnpatchAll(other.Id);
        }
    }

    // A replacement that reproduces many bodies, constructors among them, pins them all in one fingerprint
    [Test]
    public void ManyBodiesAndConstructorsHaveAFingerprint()
    {
        var constructor = AccessTools.Constructor(typeof(PlayerHeadController),
            [typeof(IAnimationManager), typeof(EntityPlayer), typeof(Shape)]);
        MethodBase?[] bodies =
        [
            .. AccessTools.GetDeclaredMethods(typeof(Block)).Where(m => m.GetMethodBody() is not null)
                .Take(EngineShape.MaxMethods - 1),
            constructor
        ];
        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Length.EqualTo(EngineShape.MaxMethods));
            Assert.That(EngineShape.Of(constructor), Is.Not.Zero);
            Assert.That(EngineShape.Of(bodies), Is.Not.Zero.And.Not.EqualTo(EngineShape.Of(bodies.AsSpan(1))));
        });
    }

    private static class Other
    {
        public static void After()
        {
            // only its presence on the method counts
        }

        public static IEnumerable<CodeInstruction> Same(IEnumerable<CodeInstruction> instructions)
        {
            return instructions;
        }
    }
}
