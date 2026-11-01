using Komet.Core;
using Vintagestory.GameContent;

namespace Komet.Test.Synth;

// Rendering, CPU side: IdleAnimators, MeshRecycle, FrameScratch
[Category(Synth.Category)]
public sealed class RenderingSynth
{
    // One frame's render stages in a base: 1000 animatable block entities, all idle (each a util in Opaque and its renderer in
    // Opaque, OIT and ShadowFar), and 14 working renderers
    [Test]
    public void IdleAnimators()
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var events = (ClientEventManager)RuntimeHelpers.GetUninitializedObject(typeof(ClientEventManager));
        var stages = new List<RenderHandler>[Enum.GetValues<EnumRenderStage>().Length];
        for (var i = 0; i < stages.Length; i++) stages[i] = [];
        ChunkRig.Set(events, "game", game);
        ChunkRig.Set(events, "renderersByStage", stages);
        var busy = new Busy();
        EnumRenderStage[] drawn = [EnumRenderStage.Opaque, EnumRenderStage.OIT, EnumRenderStage.ShadowFar];
        for (var i = 0; i < 1000; i++)
        {
            var renderer = (AnimatableRenderer)RuntimeHelpers.GetUninitializedObject(typeof(AnimatableRenderer));
            var util = (BlockEntityAnimationUtil)RuntimeHelpers.GetUninitializedObject(typeof(BlockEntityAnimationUtil));
            util.animator = (ClientAnimator)RuntimeHelpers.GetUninitializedObject(typeof(ClientAnimator));
            util.renderer = renderer;
            ChunkRig.Set(util, "activeAnimationsByAnimCode", new Dictionary<string, AnimationMetaData>());
            stages[(int)EnumRenderStage.Opaque].Add(new RenderHandler { Renderer = util, ProfilingName = "beanim" });
            foreach (var stage in drawn)
                stages[(int)stage].Add(new RenderHandler { Renderer = renderer, ProfilingName = "animatable" });
            if (i % 70 == 0)
                foreach (var stage in drawn) stages[(int)stage].Add(new RenderHandler { Renderer = busy, ProfilingName = "busy" });
        }

        var result = Synth.Run("IdleAnimators", "1000-idle", ops =>
        {
            busy.Calls = 0;
            for (var i = 0; i < ops; i++)
            {
                SynthCalls.Trigger(events, EnumRenderStage.Before);
                foreach (var stage in drawn) SynthCalls.Trigger(events, stage);
            }
        }, 20, () => busy.Calls);
        using (new FeatureRig("IdleAnimators"))
        {
            Counting.Hud = true;
            var skipped = Komet.Rendering.IdleAnimators.Skipped;
            SynthCalls.Trigger(events, EnumRenderStage.Before);
            foreach (var stage in drawn) SynthCalls.Trigger(events, stage);
            Counting.Hud = false;
            Assert.That(Komet.Rendering.IdleAnimators.Skipped - skipped, Is.EqualTo(4000), "every idle entry left out");
        }
        Assert.That(result.Feature, Is.EqualTo("IdleAnimators"));
    }

    [Test]
    public void MeshRecycle()
    {
        var saved = MeshData.Recycler;
        try
        {
            MeshData.Recycler = new MeshDataRecycler(Answers.Of<IClientWorldAccessor>(new() { ["get_ElapsedMilliseconds"] = _ => 0L }));
            var source = Terrain(1200, 7);
            using var regen = new Regen(AccessTools.Method(typeof(MeshData), nameof(MeshData.CloneUsingRecycler)),
                AccessTools.Method(typeof(MeshData), nameof(MeshData.Dispose)));
            long sum = 0;
            var result = Synth.Run("MeshRecycle", "terrain-1200", ops =>
            {
                sum = 0;
                for (var i = 0; i < ops; i++)
                {
                    var clone = SynthCalls.Recycled(source);
                    sum += clone.CustomInts.Values[5] + clone.Normals[3];
                    SynthCalls.Dispose(clone);
                    MeshData.Recycler.DoRecycling();
                }
            }, 2000, () => sum, regen.Again);
            Assert.That(result.Feature, Is.EqualTo("MeshRecycle"));
        }
        finally
        {
            MeshData.Recycler = saved;
        }
    }

    private static MeshData Terrain(int vertices, int seed)
    {
        var r = new Random(seed);
        var mesh = new MeshData(vertices, vertices * 3 / 2);
        for (var i = 0; i < vertices / 4; i++)
        {
            for (var k = 0; k < 4; k++) mesh.AddVertexWithFlags(r.Next(32), r.Next(32), r.Next(32), 0.5f, 0.5f, r.Next(), r.Next());
            mesh.AddQuadIndices(4 * i);
        }

        var q = vertices / 4;
        (mesh.Normals, mesh.NormalsCount) = ([.. Enumerable.Range(0, vertices).Select(_ => r.Next())], vertices);
        byte[] Bytes(int below) => [.. Enumerable.Range(0, q).Select(_ => (byte)r.Next(below))];
        (mesh.XyzFaces, mesh.XyzFacesCount, mesh.TextureIndices, mesh.TextureIndicesCount) = (Bytes(6), q, Bytes(4), q);
        mesh.TextureIds = [1, 2];
        (mesh.ClimateColorMapIds, mesh.SeasonColorMapIds, mesh.ColorMapIdsCount) = (Bytes(8), Bytes(8), q);
        (mesh.RenderPassesAndExtraBits, mesh.RenderPassCount) = ([.. Enumerable.Range(0, q).Select(_ => (short)r.Next(6))], q);
        mesh.CustomInts = new CustomMeshDataPartInt(4 * vertices)
            { Count = 2 * vertices, InterleaveStride = 8, InterleaveSizes = [2], InterleaveOffsets = [0] };
        for (var i = 0; i < mesh.CustomInts.Values.Length; i++) mesh.CustomInts.Values[i] = r.Next();
        mesh.CustomFloats = new CustomMeshDataPartFloat(2 * vertices)
            { Count = vertices, InterleaveStride = 4, InterleaveSizes = [1], InterleaveOffsets = [0] };
        return mesh;
    }

    // A 2D texture drawn: GlTranslate there and back on the projection stack (the hotbar, status bars, map)
    [Test]
    public void FrameScratch()
    {
        var main = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        (main.MvMatrix, main.PMatrix) = (new StackMatrix4(4), new StackMatrix4(4));
        double[] start = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 10, 11, 12, 1];
        main.MvMatrix.Push(start);
        main.PMatrix.Push(start);
        main.CurrentMatrixModeProjection = true;
        var result = Synth.Run("FrameScratch", "gltranslate", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                SynthCalls.Translate(main, 0.5, -2, 3);
                SynthCalls.Translate(main, -0.5, 2, -3);
            }
        }, 10000, () => main.PMatrix.Top[12] + main.PMatrix.Top[13] + main.PMatrix.Top[14]);
        using (new FeatureRig("FrameScratch")) Assert.That(Komet.Rendering.FrameScratch.Patched, Is.EqualTo(2));
        Assert.That(result.Feature, Is.EqualTo("FrameScratch"));
    }

    // A renderer at work: called every time
    private sealed class Busy : IRenderer
    {
        public int Calls { get; set; }
        public double RenderOrder => 0.5;
        public int RenderRange => 24;

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage) => Calls++;

        public void Dispose()
        {
            // nothing held
        }
    }
}
