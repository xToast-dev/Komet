using Komet.Rendering;

namespace Komet.Test.Rendering;

// Faces packed straight into staging read as the usual way packs them: as FaceData
[NonParallelizable]
public sealed class FacePackingDirectTests
{
    private static MeshData Quads(int faces, int seed)
    {
        var random = new Random(seed);
        var mesh = new MeshData(4 * faces, 6 * faces);
        for (var i = 0; i < 4 * faces; i++)
            mesh.AddVertexWithFlags(random.Next(-500, 500) + (float)random.NextDouble(), random.Next(0, 256),
                random.Next(-500, 500) + (float)random.NextDouble(), (float)random.NextDouble(), (float)random.NextDouble(),
                random.Next(), random.Next());
        return mesh;
    }

    [Test]
    public unsafe void StraightIsTheUsualPacking()
    {
        const int faces = 3000;
        var mesh = Quads(faces, 3);
        Assert.That(FacePacking.Pack(mesh, faces), Is.True);
        var expected = FacePacking.Packed(faces).ToArray();
        var target = new byte[expected.Length];
        fixed (byte* into = target) Assert.That(FacePacking.PackInto(mesh, faces, into), Is.True);
        Assert.That(target, Is.EqualTo(expected));
    }
}
