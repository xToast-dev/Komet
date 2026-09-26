using Vintagestory.API.Client;

namespace Komet.Test.Rendering;

// Golden test: a mesh whose extra buffers are reused has to end up with exactly what the engine's own clone produces, including after
// the mesh has been through dispose and is filled from a different source.
public sealed class MeshRecycleTests
{
    private static MeshData Source(int seed, bool withCustomInts = true, bool withNormals = true)
    {
        var r = new Random(seed);
        var vertices = r.Next(8, 400);
        var mesh = new MeshData(vertices);
        if (withNormals)
        {
            mesh.Normals = [.. Enumerable.Range(0, vertices).Select(_ => r.Next())];
            mesh.NormalsCount = vertices;
        }

        mesh.XyzFaces = [.. Enumerable.Range(0, vertices / 4 + 1).Select(_ => (byte)r.Next(6))];
        mesh.XyzFacesCount = vertices / 4 + 1;
        mesh.TextureIndices = [.. Enumerable.Range(0, vertices / 4 + 1).Select(_ => (byte)r.Next(4))];
        mesh.TextureIndicesCount = vertices / 4 + 1;
        mesh.TextureIds = [.. Enumerable.Range(0, r.Next(1, 5)).Select(_ => r.Next())];
        mesh.ClimateColorMapIds = [.. Enumerable.Range(0, vertices / 4 + 1).Select(_ => (byte)r.Next(8))];
        mesh.SeasonColorMapIds = [.. Enumerable.Range(0, vertices / 4 + 1).Select(_ => (byte)r.Next(8))];
        mesh.ColorMapIdsCount = vertices / 4 + 1;
        mesh.RenderPassesAndExtraBits = [.. Enumerable.Range(0, vertices / 4).Select(_ => (short)r.Next(6))];
        mesh.RenderPassCount = vertices / 4;
        if (withCustomInts)
        {
            // the tesselator's buffer is grown far past what this mesh uses, which is what the engine's SetFrom copies in full
            mesh.CustomInts = new CustomMeshDataPartInt(4 * vertices)
            { Count = 2 * vertices, InterleaveStride = 8, InterleaveSizes = [2], InterleaveOffsets = [0] };
            for (var i = 0; i < mesh.CustomInts.Values.Length; i++) mesh.CustomInts.Values[i] = r.Next();
        }

        mesh.CustomFloats = new CustomMeshDataPartFloat(2 * vertices)
        { Count = vertices, InterleaveStride = 4, InterleaveSizes = [1], InterleaveOffsets = [0] };
        for (var i = 0; i < mesh.CustomFloats.Values.Length; i++) mesh.CustomFloats.Values[i] = r.NextSingle();
        return mesh;
    }

    private static void AssertMatchesEngineClone(MeshData source, MeshData mine)
    {
        var engine = source.Clone();
        Assert.Multiple(() =>
        {
            Assert.That(mine.Normals?.Take(source.NormalsCount), Is.EqualTo(engine.Normals?.Take(source.NormalsCount)),
                "normals");
            Assert.That(mine.XyzFaces?.Take(source.XyzFacesCount),
                Is.EqualTo(engine.XyzFaces?.Take(source.XyzFacesCount)), "xyz faces");
            Assert.That(mine.XyzFacesCount, Is.EqualTo(engine.XyzFacesCount));
            Assert.That(mine.TextureIndices?.Take(source.TextureIndicesCount),
                Is.EqualTo(engine.TextureIndices?.Take(source.TextureIndicesCount)), "texture indices");
            Assert.That(mine.TextureIds, Is.EqualTo(engine.TextureIds), "texture ids");
            Assert.That(mine.ClimateColorMapIds?.Take(source.ColorMapIdsCount),
                Is.EqualTo(engine.ClimateColorMapIds?.Take(source.ColorMapIdsCount)), "climate map");
            Assert.That(mine.SeasonColorMapIds?.Take(source.ColorMapIdsCount),
                Is.EqualTo(engine.SeasonColorMapIds?.Take(source.ColorMapIdsCount)), "season map");
            Assert.That(mine.ColorMapIdsCount, Is.EqualTo(engine.ColorMapIdsCount));
            Assert.That(mine.RenderPassesAndExtraBits?.Take(source.RenderPassCount),
                Is.EqualTo(engine.RenderPassesAndExtraBits?.Take(source.RenderPassCount)), "render passes");
            Assert.That(mine.RenderPassCount, Is.EqualTo(engine.RenderPassCount));
            AssertPart(mine.CustomInts, engine.CustomInts, source.CustomInts?.Count ?? 0);
            AssertPart(mine.CustomFloats, engine.CustomFloats, source.CustomFloats?.Count ?? 0);
        });
    }

    private static void AssertPart<T>(CustomMeshDataPart<T>? mine, CustomMeshDataPart<T>? engine, int count)
    {
        if (engine is null)
        {
            Assert.That(mine, Is.Null, "part should be gone");
            return;
        }

        Assert.That(mine, Is.Not.Null);
        Assert.That(mine!.Values?.Take(count), Is.EqualTo(engine.Values?.Take(count)), "part values");
        Assert.That(mine.Count, Is.EqualTo(engine.Count));
        Assert.That(mine.InterleaveStride, Is.EqualTo(engine.InterleaveStride));
        Assert.That(mine.InterleaveSizes, Is.EqualTo(engine.InterleaveSizes));
        Assert.That(mine.InterleaveOffsets, Is.EqualTo(engine.InterleaveOffsets));
        Assert.That(mine.AllocationSize, Is.EqualTo(engine.AllocationSize), "allocation size");
        Assert.That(mine.Values?.Length ?? 0, Is.GreaterThanOrEqualTo(engine.Values is null ? 0 : count), "capacity");
    }

    [Test]
    public void FirstCloneMatchesTheEngine([Range(1, 6)] int seed)
    {
        var source = Source(seed);
        var dest = new MeshData(16) { Recyclable = true };
        Assert.That(MeshRecycle.CloneExtraData(source, dest), Is.False, "the engine method must be skipped");
        AssertMatchesEngineClone(source, dest);
    }

    // The mesh goes back to the recycler and is handed out again for a different chunk: buffers are reused, values must still match
    [Test]
    public void ReusedBuffersMatchTheEngine()
    {
        var dest = new MeshData(16) { Recyclable = true };
        for (var round = 1; round <= 12; round++)
        {
            var source = Source(round);
            _ = MeshRecycle.CloneExtraData(source, dest);
            AssertMatchesEngineClone(source, dest);
            Assert.That(MeshRecycle.DisposeExtraData(dest), Is.False, "a recyclable mesh keeps its buffers");
        }
    }

    // A later source without custom ints or normals must leave no trace of the earlier one
    [Test]
    public void FieldsTheSourceLacksAreCleared()
    {
        var dest = new MeshData(16) { Recyclable = true };
        _ = MeshRecycle.CloneExtraData(Source(3), dest);
        _ = MeshRecycle.DisposeExtraData(dest);
        var plain = Source(4, false, false);
        _ = MeshRecycle.CloneExtraData(plain, dest);
        Assert.Multiple(() =>
        {
            Assert.That(dest.CustomInts, Is.Null);
            Assert.That(dest.Normals, Is.Null);
        });
        AssertMatchesEngineClone(plain, dest);
    }

    // CustomMeshDataPartByte.Clone keeps Conversion and the Short clone resets it to the class default. SetFrom copies the allocation
    // size, custom or not, so it survives a later Count change (rounds 1, 2) and a kept part does not carry it into the next round. A
    // part without values is the engine's (round 4), and TextureIds go only along with TextureIndices (round 3).
    [Test]
    public void CustomBytesAndShortsMatchTheEngineIncludingConversion()
    {
        var dest = new MeshData(16) { Recyclable = true };
        for (var round = 0; round < 6; round++)
        {
            var (source, r) = (Source(round), new Random(round));
            source.CustomBytes = new CustomMeshDataPartByte(96)
            {
                Count = 40 + 4 * round, InterleaveStride = 4, InterleaveSizes = [4], InterleaveOffsets = [0],
                Conversion = round % 2 == 0 ? DataConversion.Integer : DataConversion.Float
            };
            source.CustomShorts = new CustomMeshDataPartShort(96)
            {
                Count = 20 + round, InterleaveStride = 4, InterleaveSizes = [2], InterleaveOffsets = [0],
                Conversion = DataConversion.Integer
            };
            r.NextBytes(source.CustomBytes.Values);
            for (var i = 0; i < source.CustomShorts.Values.Length; i++) source.CustomShorts.Values[i] = (short)r.Next();
            if (round == 1) source.CustomShorts.SetAllocationSize(90);
            if (round == 2) source.CustomShorts.SetAllocationSize(source.CustomShorts.Count);
            if (round == 3) (source.TextureIndices, source.TextureIndicesCount) = (null, 0);
            if (round == 4)
                source.CustomBytes = new CustomMeshDataPartByte { Count = 8, Conversion = DataConversion.Float };
            _ = MeshRecycle.CloneExtraData(source, dest);
            var engine = source.Clone();
            (dest.CustomShorts!.Count, engine.CustomShorts.Count) =
                (dest.CustomShorts.Count + 4, engine.CustomShorts.Count + 4);
            Assert.Multiple(() =>
            {
                AssertPart(dest.CustomBytes, engine.CustomBytes, source.CustomBytes.Count);
                AssertPart(dest.CustomShorts, engine.CustomShorts, source.CustomShorts.Count);
                Assert.That(dest.CustomBytes!.Conversion, Is.EqualTo(engine.CustomBytes.Conversion), $"round {round}");
                Assert.That(dest.CustomShorts.Conversion, Is.EqualTo(engine.CustomShorts.Conversion), $"round {round}");
                Assert.That(dest.TextureIds, Is.EqualTo(engine.TextureIds), $"round {round}");
            });
            _ = MeshRecycle.DisposeExtraData(dest);
        }
    }

    [Test]
    public void AMeshThatIsNotRecyclableKeepsTheEnginePath()
    {
        var mesh = new MeshData(16) { Recyclable = false };
        Assert.That(MeshRecycle.DisposeExtraData(mesh), Is.True);
    }
}
