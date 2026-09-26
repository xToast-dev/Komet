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

// Use runs for every entity, held item and block entity, 118 times a frame, and must not allocate. The platform is a real
// ClientPlatformWindows without a window: Fill only reads its shader uniforms and frame buffer sizes, and the programs here take no
// texture, the one path of Stage that talks to GL.
public sealed class ShaderUseCacheTests
{
    private const int Calls = 10_000, Passes = 3;

    private static readonly string[] Includes =
        ["fogandlight.fsh", "fogandlight.vsh", "shadowcoords.vsh", "vertexwarp.vsh", "colormap.vsh"];

    private float _lodBias;

    private ClientPlatformAbstract? _platform;
    private int _shadowQuality, _viewDistance;

    [SetUp]
    public void FakePlatform()
    {
        (_platform, _shadowQuality) = (ScreenManager.Platform, ShaderProgramBase.shadowmapQuality);
        (_viewDistance, _lodBias) = (ClientSettings.ViewDistance, ClientSettings.LodBias);
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var buffers = Enumerable.Range(0, 16)
            .Select(i => new FrameBufferRef { Width = 1920, Height = 1080, DepthTextureId = i }).ToList();
        AccessTools.Field(typeof(ClientPlatformWindows), "frameBuffers").SetValue(platform, buffers);
        platform.ShaderUniforms = new DefaultShaderUniforms { ColorMapRects4 = new float[160] };
        ScreenManager.Platform = platform;
        ShaderProgramBase.shadowmapQuality = 0; // with shadows on, Fill binds the shadow maps, which needs GL
        ShaderUseCache.Enabled = true;
    }

    [TearDown]
    public void Restore()
    {
        (ScreenManager.Platform, ShaderProgramBase.shadowmapQuality) = (_platform, _shadowQuality);
        (ClientSettings.ViewDistance, ClientSettings.LodBias) = (_viewDistance, _lodBias);
        ShaderUseCache.Enabled = true;
    }

    private static ShaderProgram Program(int pass, int ubos = 0)
    {
        var program = new ShaderProgram { PassId = pass, ProgramId = 7 };
        foreach (var file in Includes) _ = program.includes.Add(file);
        for (var i = 0; i < ubos; i++) _ = program.ubos.Add("ubo" + i, new CountingUbo());
        return program;
    }

    private static long Allocated(Action action, int calls = Calls)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The last of several passes: tiered compilation promotes the methods during the first ones, and the runtime allocates for that
    private static long Steady(Action action)
    {
        long bytes = 0;
        for (var pass = 0; pass < Passes; pass++) bytes = Allocated(action);
        return bytes;
    }

    [Test]
    public void StagingAFrameAllocatesNothingOnceBuilt()
    {
        var program = Program(100);
        ShaderUseCache.NextFrame();
        Assert.That(ShaderUseCache.Stage(program), Is.Not.Null, "the layout overflowed");
        Assert.That(Steady(() => ShaderUseCache.Changed(ShaderUseCache.Stage(program)!, false)), Is.Zero);
    }

    // Engine code writes some default uniforms outside Use with values Fill would not produce (SystemRenderSunMoon zeroes Standard's
    // shadowIntensity); those go up on every Use, as the engine's Use resets them. Uniforms other code writes only with Fill's own value
    // (lightPosition, windWaveCounter) stay cached, and a changed one (zNear) goes up again.
    [Test]
    public void UniformsWrittenOutsideUseAreSentOnEveryUse()
    {
        string[] external =
        [
            "shadowIntensity", "flatFogDensity", "flatFogStart", "waterWaveCounter", "globalWarpIntensity",
            "glitchWaviness", "windWaveIntensity"
        ];
        var program = Program(105);
        for (var i = 0; i < external.Length; i++) program.uniformLocations[external[i]] = 100 + i;
        (program.uniformLocations["zNear"], program.uniformLocations["lightPosition"],
            program.uniformLocations["windWaveCounter"]) = (99, 90, 91);
        ShaderUseCache.NextFrame();
        var s = ShaderUseCache.Stage(program)!;
        var first = ShaderUseCache.Changed(s, true);
        ScreenManager.Platform.ShaderUniforms.ZNear = 0.05f;
        s = ShaderUseCache.Stage(program)!;
        var again = ShaderUseCache.Changed(s, false);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.GreaterThan(again), "the first Use of a frame sends everything");
            Assert.That(s.Sends.Take(again).Select(i => s.Slots[i].Loc),
                Is.EquivalentTo(Enumerable.Range(99, external.Length + 1)));
        });
    }

    // Each buffer once, in the dictionary's order, as the engine's foreach binds them
    [Test]
    public void UniformBuffersAreBoundInOrderWithoutAllocating()
    {
        var program = Program(101, 3);
        var bound = new List<int>();
        CountingUbo.Order = bound;
        ShaderUseCache.BindUbos(program);
        CountingUbo.Order = null; // the recording itself must not show up in the measurement
        var binds = CountingUbo.Binds;
        var bytes = Steady(() => ShaderUseCache.BindUbos(program));
        Assert.Multiple(() =>
        {
            Assert.That(bound, Is.EqualTo(program.ubos.Values.Cast<CountingUbo>().Select(u => u.Id)));
            Assert.That(CountingUbo.Binds - binds, Is.EqualTo(3L * Calls * Passes));
            Assert.That(bytes, Is.Zero);
        });
    }

    // A change made during a frame reaches the uniforms with the next frame, the way the uploads already trail by one
    [Test]
    public void SettingsAreReadOncePerFrame()
    {
        var program = Program(102);
        (ClientSettings.ViewDistance, ClientSettings.LodBias) = (256, 0.5f);
        ShaderUseCache.NextFrame();
        Assert.That(ShaderUseCache.FrameSettings, Is.EqualTo((256f, 128f)));
        ClientSettings.ViewDistance = 1024;
        _ = ShaderUseCache.Stage(program);
        Assert.That(ShaderUseCache.FrameSettings, Is.EqualTo((256f, 128f)), "read again within the frame");
        ShaderUseCache.NextFrame();
        Assert.That(ShaderUseCache.FrameSettings, Is.EqualTo((1024f, 320f)),
            "the view distance is capped at 640 for LOD 0");
    }

    // A world's first Use() can come before its first uniform update; the frame's settings are read right then
    [Test]
    public void AFrameWithoutUpdateReadsTheSettingsOnFirstUse()
    {
        var program = Program(103);
        ClientSettings.ViewDistance = 384;
        ShaderUseCache.Enabled = false; // the update skips the read while the cache is off
        ShaderUseCache.NextFrame();
        ShaderUseCache.Enabled = true;
        Assert.That(ShaderUseCache.Stage(program), Is.Not.Null);
        Assert.That(ShaderUseCache.FrameSettings.ViewDistance, Is.EqualTo(384f));
    }

    private sealed class CountingUbo : UBORef
    {
        private static int _next;
        public static List<int>? Order { get; set; }
        public static long Binds { get; private set; }
        public int Id { get; } = Interlocked.Increment(ref _next);

        public override void Bind()
        {
            Binds++;
            Order?.Add(Id);
        }

        public override void Unbind()
        {
        }

        public override void Update<T>(T data)
        {
        }

        public override void Update<T>(T data, int offset, int size)
        {
        }

        public override void Update(object data, int offset, int size)
        {
        }
    }
}
