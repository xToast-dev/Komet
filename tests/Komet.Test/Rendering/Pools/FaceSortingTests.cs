using Komet.Rendering;

namespace Komet.Test.Rendering;

// FaceSorting on the engine's own cube (CubeMeshUtil.GetCube, the 2x2x2 cube the tessellator's faces are cut from): every face
// lands in its direction, front sides outwards - the +X face's plane is x = 1 and it faces +X - each direction's nearest plane
// is its face's, and every array of the mesh moves with its face. Faces the shader moves or pushes in depth go last, in order.
// A mesh not laid out as quads with the quad index pattern stays as it was.
public sealed class FaceSortingTests
{
    private static readonly int[] OnePerDirection = [1, 1, 1, 1, 1, 1];
    private static readonly float[] CubePlanes = [-1f, 1f, -1f, 1f, -1f, 1f];

    // The cube with a tag per face in every per-vertex and per-face array: face f's values are f (uv, flags' glow bits, rgba,
    // custom ints) so that where a face went its data must have gone too
    private static MeshData Cube(int faces = 6)
    {
        var mesh = CubeMeshUtil.GetCube();
        mesh.Indices = (int[])mesh.Indices.Clone(); // GetCube hands out the engine's static CubeVertexIndices
        mesh.Flags = new int[4 * faces];
        mesh.CustomInts = new CustomMeshDataPartInt(4 * faces) { Count = 4 * faces };
        mesh.ClimateColorMapIds = new byte[faces];
        mesh.SeasonColorMapIds = new byte[faces];
        mesh.ColorMapIdsCount = faces;
        for (var f = 0; f < faces; f++)
        {
            (mesh.ClimateColorMapIds[f], mesh.SeasonColorMapIds[f]) = ((byte)f, (byte)(10 + f));
            for (var k = 0; k < 4; k++)
            {
                var v = 4 * f + k;
                (mesh.Flags[v], mesh.CustomInts.Values[v], mesh.Rgba[4 * v]) = (f, 100 + f, (byte)(20 + f));
                mesh.Uv[2 * v] = f;
            }
        }

        return mesh;
    }

    [Test]
    public void TheCubesFacesGoToTheirDirectionsFrontOutwards()
    {
        var mesh = Cube();
        var original = (float[])mesh.xyz.Clone();
        var sorted = FaceSorting.Sorting(mesh);
        Assert.That(sorted, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(sorted!.Counts, Is.EqualTo(OnePerDirection), "one face per direction");
            Assert.That(Enumerable.Range(0, 6).Select(d => sorted.Low[4 * d + 3]), Is.EqualTo(CubePlanes),
                "one face in each direction's last slab: -X at x = -1, +X at x = 1, ...");
            for (var d = 0; d < 6; d++)
            {
                var (axis, plane) = (d / 2, d % 2 == 0 ? -1f : 1f);
                for (var k = 0; k < 4; k++)
                {
                    Assert.That(mesh.xyz[3 * (4 * d + k) + axis], Is.EqualTo(plane), $"face {d}");
                }
            }

            Assert.That(mesh.Indices[..36], Is.EqualTo(CubeMeshUtil.CubeVertexIndices), "the quad pattern stays");
            Assert.That(Moved(mesh, original), Is.True, "every array moved with its face");
        });
    }

    [Test]
    public void AMeshNotLaidOutAsQuadsStaysAsItWas()
    {
        var mesh = Cube();
        (mesh.Indices[1], mesh.Indices[2]) = (mesh.Indices[2], mesh.Indices[1]);
        var (xyz, ints) = ((float[])mesh.xyz.Clone(), (int[])mesh.CustomInts.Values.Clone());
        Assert.Multiple(() =>
        {
            Assert.That(FaceSorting.Sorting(mesh), Is.Null);
            Assert.That(mesh.xyz, Is.EqualTo(xyz));
            Assert.That(mesh.CustomInts.Values, Is.EqualTo(ints));
        });
    }

    private static bool Moved(MeshData mesh, float[] original)
    {
        for (var f = 0; f < 6; f++)
        {
            var was = mesh.CustomInts.Values[4 * f] - 100;
            if (mesh.ClimateColorMapIds[f] != was || mesh.SeasonColorMapIds[f] != 10 + was) return false;

            for (var k = 0; k < 4; k++)
            {
                var v = 4 * f + k;
                if ((mesh.Flags[v] & 0xFF) != was || mesh.Rgba[4 * v] != 20 + was || (int)mesh.Uv[2 * v] != was ||
                    mesh.CustomInts.Values[v] != 100 + was)
                {
                    return false;
                }

                for (var c = 0; c < 3; c++)
                {
                    if (BitConverter.SingleToInt32Bits(mesh.xyz[3 * v + c]) !=
                        BitConverter.SingleToInt32Bits(original[3 * (4 * was + k) + c]))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}

// A mesh reference the engine reports as uploaded, for pools that never draw
internal sealed class NoMesh : MeshRef
{
    public override bool Initialized => true;
}
