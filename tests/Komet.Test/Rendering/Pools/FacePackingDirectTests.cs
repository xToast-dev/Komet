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
        {
            mesh.AddVertexWithFlags(random.Next(-500, 500) + (float)random.NextDouble(), random.Next(0, 256),
                random.Next(-500, 500) + (float)random.NextDouble(), (float)random.NextDouble(),
                (float)random.NextDouble(),
                random.Next(), random.Next());
        }

        return mesh;
    }

    [TearDown]
    public void Restore() => (FacePacking.Through, FacePacking.Reserve, FacePacking.Commit) = (null, null, null);

    // The upload once the usual way (its faces taken by Through) and once packed straight into reserved staging
    [Test]
    public unsafe void StraightIsTheUsualPacking()
    {
        const int faces = 3000;
        var mesh = Quads(faces, 3);
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var vao = new VAO { xyzVboId = 11, rgbaVboId = 12, customDataIntVboId = 13 };
        // marked disposed up front: no GL to delete buffers it never made, and its finalizer wants no platform
        _ = AccessTools.PropertySetter(typeof(MeshRef), nameof(MeshRef.Disposed)).Invoke(vao, [true]);
        byte[]? expected = null;
        FacePacking.Through = (_, buffer, _, bytes) =>
        {
            if (buffer == vao.xyzVboId) expected = bytes.ToArray();
            return true;
        };
        Assert.That(FacePacking.Uploading(platform, vao, mesh), Is.False, "the usual way");
        Assert.That(expected, Is.Not.Null);

        var target = new byte[expected!.Length];
        fixed (byte* into = target)
        {
            var at = (IntPtr)into;
            FacePacking.Reserve = (_, _, _, bytes, out pointer) =>
            {
                pointer = at;
                return bytes == target.Length;
            };
            FacePacking.Commit = () => true;
            Assert.That(FacePacking.Uploading(platform, vao, mesh), Is.False, "straight");
        }

        Assert.That(target, Is.EqualTo(expected));
    }
}
