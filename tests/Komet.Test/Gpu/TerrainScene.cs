using System.Runtime.InteropServices;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

internal sealed class TerrainScene : IDisposable
{
    public const int Size = 64, AtlasSide = 16;
    public static readonly (int[] Starts, int[] Sizes, int Count) Ranges = ([0, 12 * 4], [12, 6], 2);

    private readonly List<SharedImage> _images = [];
    private readonly int _indices;

    // foliage: the quads and atlas of Foliage instead of the three quads (chunkopaque, chunktopsoil)
    public TerrainScene(VulkanDevice device, bool high, string program = "chunkopaque", Foliage? foliage = null)
    {
        Device = device;
        Name = program;
        var (vertex, fragment) = EngineShaders.Program(program, high);
        Program = Parity.Linked(vertex, fragment);
        // ported as the game ports it: read back from the driver, the linker's attribute locations, origin pushed per pool
        var ported = ProgramPorts.Port(Program, program, out var why, new HashSet<string>(["origin"], StringComparer.Ordinal));
        Ported = ported ?? throw new InvalidOperationException(why);
        Classic = program is "chunkliquid" or "chunkliquiddepth";
        _foliage = foliage;
        Vao = Classic ? ClassicPool() : Pool(program == "chunktopsoil", foliage?.Faces);
        _indices = Quads(foliage?.Faces.Length ?? 16);
        Uniforms();
        Textures();
    }

    public VulkanDevice Device { get; }

    // Drawn as OcclusionCulling's draws are, from commands in Counts' buffers and a count the GPU reads there: one command per
    // quad of the first range (the one in front, the one partly behind it), and the count names the first only, so OpenGL
    // then draws the quad in front alone
    public bool Counted { get; set; }
    public VulkanBackend? Counts { get; set; }
    private int _commands, _count;
    private readonly Foliage? _foliage;
    public string Name { get; }
    public bool Classic { get; } // drawn on the classic path, useSSBOs false, as the engine draws the liquid pools
    public int Program { get; }
    public GlslPort.Ported Ported { get; }
    public VAO Vao { get; }
    public UniformMirror Mirror { get; } = new();
    public Dictionary<string, SharedImage> Sampled { get; } = [];

    // The engine's quads: the camera looks down -z from the origin, the model-view matrix is the identity. The topsoil pass's
    // pools carry a second attribute, two normalized shorts per vertex (uv2In), as ChunkRenderer asks for them.
    private static VAO Pool(bool topsoil, FaceData[]? given)
    {
        var vao = new VAO { VaoId = GL.GenVertexArray(), vaoSlotNumber = topsoil ? 2 : 1 };
        var faces = given ??
        [
            Face([-1, -1, -4, 1, -1, -4, 1, 1, -4, -1, 1, -4], 0), // in front
            Face([0, 0, -6, 2, 0, -6, 2, 2, -6, 0, 2, -6], 0.5f), // partly behind the first
            Face([-0.5f, -0.5f, -3, -0.5f, 0.5f, -3, 0.5f, 0.5f, -3, 0.5f, -0.5f, -3], 0.25f) // turned away
        ];
        GL.BindVertexArray(vao.VaoId);
        GL.CreateBuffers(1, out vao.xyzVboId);
        GL.NamedBufferStorage(vao.xyzVboId, faces.Length * 64, faces, BufferStorageFlags.DynamicStorageBit);
        var light = Enumerable.Repeat(unchecked((int)0xFFD0E0FF), faces.Length * 4).ToArray();
        vao.rgbaVboId = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vao.rgbaVboId);
        GL.BufferData(BufferTarget.ArrayBuffer, light.Length * 4, light, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
        GL.EnableVertexAttribArray(0);
        if (topsoil)
        {
            var uv2 = Enumerable.Range(0, faces.Length * 4 * 2)
                .Select(i => (ushort)(i % 2 == 0 ? 0x1000 + i * 97 : 0x2000 + i * 31)).ToArray();
            vao.customDataShortVboId = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, vao.customDataShortVboId);
            GL.BufferData(BufferTarget.ArrayBuffer, uv2.Length * 2, uv2, BufferUsageHint.StaticDraw);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.UnsignedShort, true, 4, 0);
            GL.EnableVertexAttribArray(1);
        }

        GL.BindVertexArray(0);
        return vao;
    }

    // The liquid pools' layout: xyz, uv, rgba, render flags, a flow vector, then two ints interleaved (colormap and water flags)
    private static VAO ClassicPool()
    {
        float[][] quads =
        [
            [-1, -1, -4, 1, -1, -4, 1, 1, -4, -1, 1, -4], [0, 0, -6, 2, 0, -6, 2, 2, -6, 0, 2, -6],
            [-0.5f, -0.5f, -3, -0.5f, 0.5f, -3, 0.5f, 0.5f, -3, 0.5f, -0.5f, -3]
        ];
        var xyz = quads.SelectMany(q => q).ToArray();
        float[] corners = [0, 0, 0.25f, 0, 0.25f, 0.25f, 0, 0.25f];
        var uv = Enumerable.Range(0, 3).SelectMany(q => corners.Select((c, i) => i % 2 == 0 ? c + q * 0.25f : c))
            .ToArray();
        var vao = new VAO { VaoId = GL.GenVertexArray(), vaoSlotNumber = 7 };
        GL.BindVertexArray(vao.VaoId);
        vao.xyzVboId = Attribute(0, xyz, 3);
        vao.uvVboId = Attribute(1, uv, 2);
        vao.rgbaVboId = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vao.rgbaVboId);
        var light = Enumerable.Repeat(unchecked((int)0xFFD0E0FF), 12).ToArray();
        GL.BufferData(BufferTarget.ArrayBuffer, light.Length * 4, light, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
        GL.EnableVertexAttribArray(2);
        vao.flagsVboId = Integers(3, new int[12], 1, 0, 4);
        vao.customDataFloatVboId = Attribute(4, new float[24], 2);
        vao.customDataIntVboId = Integers(5, new int[24], 1, 0, 8);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vao.customDataIntVboId);
        GL.VertexAttribIPointer(6, 1, VertexAttribIntegerType.Int, 8, 4);
        GL.EnableVertexAttribArray(6);
        int[] pattern = [0, 1, 2, 0, 2, 3];
        var indices = Enumerable.Range(0, 18).Select(i => i / 6 * 4 + pattern[i % 6]).ToArray();
        vao.vboIdIndex = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, vao.vboIdIndex);
        GL.BufferData(BufferTarget.ElementArrayBuffer, indices.Length * 4, indices, BufferUsageHint.StaticDraw);
        GL.BindVertexArray(0);
        return vao;
    }

    private static int Attribute(int location, float[] values, int size)
    {
        var buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, values.Length * 4, values, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(location, size, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(location);
        return buffer;
    }

    private static int Integers(int location, int[] values, int size, int offset, int stride)
    {
        var buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, values.Length * 4, values, BufferUsageHint.StaticDraw);
        GL.VertexAttribIPointer(location, size, VertexAttribIntegerType.Int, stride, offset);
        GL.EnableVertexAttribArray(location);
        return buffer;
    }

    private static FaceData Face(float[] xyz, float u) =>
        new(xyz, 0, u, 0, 0.25f, 0.25f, [0, 0, 0, 0], 0, 0, false);

    private static int Quads(int quads)
    {
        ReadOnlySpan<int> corners = [0, 1, 2, 0, 2, 3];
        var pattern = new int[6 * quads];
        for (var i = 0; i < pattern.Length; i++) pattern[i] = i / 6 * 4 + corners[i % 6];
        GL.CreateBuffers(1, out int buffer);
        GL.NamedBufferStorage(buffer, pattern.Length * 4, pattern, BufferStorageFlags.None);
        return buffer;
    }

    private void Uniforms()
    {
        const float near = 0.1f, far = 100f;
        const float depth = -(far + near) / (far - near), offset = -2 * far * near / (far - near);
        float[] projection = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, depth, -1, 0, 0, offset, 0];
        Set("projectionMatrix", projection);
        Set("modelViewMatrix", [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
        Set("origin", [0, 0, 0]);
        Set("rgbaAmbientIn", [0.9f, 0.9f, 1]);
        Set("rgbaFogIn", [0.5f, 0.6f, 0.7f, 1]);
        Set("fogDensityIn", [0.02f]);
        Set("fogMinIn", [0.05f]);
        Set("alphaTest", [0.001f]);
        Set("viewDistance", [500]);
        Set("viewDistanceLod0", [300]);
        Set("dayLight", [1]);
        Set("sunPosition", [0, 1, 0]);
        Set("lightPosition", [0.3f, 0.8f, 0.5f]);
        Set("atlasHeight", [AtlasSide]);
        Set("frameSize", [Size, Size]);
        Set("zNear", [near]);
        Set("zFar", [far]);
        Set("horizontalResolution", [Size]);
        Set("shadowIntensity", [0.8f]);
        Set("blockTextureSize", [0.25f, 0.25f]);
        Set("mvpMatrix", projection); // the model-view matrix is the identity
        Set("textureAtlasSize", [AtlasSide, AtlasSide]);
        Set("sunPosRel", [0.3f, 0.8f, 0.5f]);
        Set("sunColor", [1, 0.9f, 0.8f]);
        Set("reflectColor", [0.4f, 0.5f, 0.6f]);
        Set("playerViewVec", [0, 0, -1]);
        Set("sunSpecularIntensity", [0.5f]);
        Set("waterStillCounter", [0.3f]);
        Set("waterFlowCounter", [0.7f]);
    }

    public void Set(string name, float[] values)
    {
        var member = Ported.VertexUniforms.Members.Concat(Ported.FragmentUniforms.Members)
            .Concat(Ported.HotUniforms.Members).FirstOrDefault(u => u.Name == name);
        if (member is null) return;
        var location = GL.GetUniformLocation(Program, name);
        var integer = member.Type is "int" || member.Type.StartsWith("ivec", StringComparison.Ordinal);
        if (Mirror.EntryOf(name) is { } entry)
            Mirror.SetAt(entry, 0, integer
                ? MemoryMarshal.Cast<int, uint>(values.Select(v => (int)v).ToArray())
                : MemoryMarshal.Cast<float, uint>(values));
        if (location < 0) return;
        if (integer) GL.ProgramUniform1(Program, location, values.Length, values.Select(v => (int)v).ToArray());
        else if (member.Type == "mat4") GL.ProgramUniformMatrix4(Program, location, 1, false, values);
        else if (values.Length == 1) GL.ProgramUniform1(Program, location, 1, values);
        else if (values.Length == 2) GL.ProgramUniform2(Program, location, 1, values);
        else if (values.Length == 3) GL.ProgramUniform3(Program, location, 1, values);
        else GL.ProgramUniform4(Program, location, 1, values);
    }

    private void Textures()
    {
        foreach (var sampler in Ported.Samplers)
        {
            var depth = sampler.Type == "sampler2DShadow" || sampler.Name == "liquidDepth";
            var image = SharedImage.Create(Device, AtlasSide, AtlasSide, 1,
                depth ? SharedFormat.Depth32F : SharedFormat.Rgba8,
                Vk.Sampled | Vk.TransferDst, out var why) ?? throw new InvalidOperationException(why);
            _images.Add(image);
            Sampled[sampler.Name] = image;
            if (depth) GL.ClearTexImage(image.Texture, 0, PixelFormat.DepthComponent, PixelType.Float, [1f]);
            else GL.TextureSubImage2D(image.Texture, 0, 0, 0, AtlasSide, AtlasSide, PixelFormat.Rgba,
                PixelType.UnsignedByte,
                _foliage is { } leaves && sampler.Name is "terrainTex" or "tex2d" // chunkopaque's atlas, chunkshadowmap's
                    ? leaves.Atlas
                    : Pattern(sampler.Name.StartsWith("terrainTex", StringComparison.Ordinal)));
            GL.TextureParameter(image.Texture, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TextureParameter(image.Texture, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TextureParameter(image.Texture, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TextureParameter(image.Texture, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            if (sampler.Type == "sampler2DShadow")
                GL.TextureParameter(image.Texture, TextureParameterName.TextureCompareMode,
                    (int)TextureCompareMode.CompareRefToTexture);
        }
    }

    private static byte[] Pattern(bool atlas)
    {
        var pixels = new byte[AtlasSide * AtlasSide * 4];
        for (var i = 0; i < AtlasSide * AtlasSide; i++)
        {
            var (x, y) = (i % AtlasSide, i / AtlasSide);
            (pixels[4 * i], pixels[4 * i + 1], pixels[4 * i + 2], pixels[4 * i + 3]) = atlas
                ? ((byte)(x * 16), (byte)(y * 16), (byte)(255 - x * 8), (byte)255)
                : ((byte)40, (byte)60, (byte)80, (byte)255);
        }

        return pixels;
    }

    public bool Draw(TerrainRenderer? capture, IReadOnlyDictionary<string, int> textures)
    {
        GL.UseProgram(Program);
        var unit = 0;
        foreach (var sampler in Ported.Samplers)
        {
            var texture = textures.TryGetValue(sampler.Name, out var given) ? given : Sampled[sampler.Name].Texture;
            GL.BindTextureUnit(unit, texture);
            GL.Uniform1(GL.GetUniformLocation(Program, sampler.Name), unit);
            unit++;
        }

        // the pool's origin, as MeshDataPoolManager.Render sets it before each pool: a push constant in Vulkan
        GL.Uniform3(GL.GetUniformLocation(Program, "origin"), 0.25f, -0.125f, 0f);
        var pointers = Ranges.Starts.SelectMany(s => (int[])[s, 0]).ToArray();
        var counted = Counted && Counts is not null;
        var taken = capture is not null && (counted
            ? TakeCounted(capture)
            : capture.Take(Vao, (pointers, Ranges.Sizes, Ranges.Count, !Classic), Sync, _ => default));
        if (!taken)
        {
            GL.BindVertexArray(Vao.VaoId);
            if (!Classic)
            {
                GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, Vao.xyzVboId);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indices);
            }

            for (var i = 0; i < (counted ? 1 : Ranges.Count); i++)
                GL.DrawElements(PrimitiveType.Triangles, counted ? 6 : Ranges.Sizes[i], DrawElementsType.UnsignedInt,
                    Ranges.Starts[i]);
            GL.BindVertexArray(0);
        }

        GL.UseProgram(0);
        return taken;
    }

    // The quads of each of draws (first quad, count) as OcclusionCulling's draws of one call: OpenGL's without a capture, else
    // the capture's from commands and counts in Counts' buffers, on Komet's own depth when own (as RenderOpaque's,
    // VulkanRenderer), but for the draw at plain, which goes on the engine's (another's draw into it). The segment is closed
    // before the draw at close, drawn asked after each draw. Whether the capture took every draw. kept: a shadow pass's draws,
    // Owning left as the stage set it, the counted ones when Counted, else as the engine's pool draws (Sync)
    public bool DrawCall(TerrainRenderer? capture, (int First, int Count)[] draws, bool own, int plain = -1, int close = -1,
        Action<TerrainRenderer>? drawn = null, bool kept = false)
    {
        GL.UseProgram(Program);
        var unit = 0;
        foreach (var sampler in Ported.Samplers)
        {
            GL.BindTextureUnit(unit, Sampled[sampler.Name].Texture);
            GL.Uniform1(GL.GetUniformLocation(Program, sampler.Name), unit++);
        }

        GL.Uniform3(GL.GetUniformLocation(Program, "origin"), 0.25f, -0.125f, 0f);
        var taken = capture is null ? GlCall(draws) : Handed(capture, draws, (own, plain, close, kept), drawn);
        GL.UseProgram(0);
        return taken;
    }

    private bool GlCall((int First, int Count)[] draws)
    {
        GL.BindVertexArray(Vao.VaoId);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, Vao.xyzVboId);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, _indices);
        foreach (var (first, count) in draws)
            GL.DrawElements(PrimitiveType.Triangles, 6 * count, DrawElementsType.UnsignedInt, 24 * first);
        GL.BindVertexArray(0);
        return false;
    }

    private bool Handed(TerrainRenderer capture, (int First, int Count)[] draws, (bool On, int Plain, int Close, bool Kept) own,
        Action<TerrainRenderer>? drawn)
    {
        var counts = Counts!;
        if (_commands == 0)
        {
            (_commands, _count) = (counts.Buffer(), counts.Buffer());
            counts.Upload<uint>(_commands, [], 20 * 64);
            counts.Upload<uint>(_count, [], 4 * 64);
        }

        counts.UploadAt<uint>(_commands, 0, [.. draws.SelectMany(d => (uint[])[6 * (uint)d.Count, 1, 6 * (uint)d.First, 0, 0])]);
        counts.UploadAt<uint>(_count, 0, [.. draws.Select(_ => 1u)]);
        var ((commands, at), (count, countAt)) = (counts.Native(_commands), counts.Native(_count));
        var taken = true;
        for (var d = 0; d < draws.Length; d++)
        {
            if (!own.Kept) capture.Owning = own.On && d != own.Plain;
            if (d == own.Close) capture.Frame.Close("the test closes the segment");
            taken &= own.Kept && !Counted
                ? capture.Take(Vao, ([24 * draws[d].First, 0], [6 * draws[d].Count], 1, true), Sync, _ => default)
                : capture.TakeCounted(Vao, true, ((commands, at + 20 * (ulong)d), (count, countAt + 4 * (ulong)d), 1),
                    _ => default);
            drawn?.Invoke(capture);
        }

        if (!own.Kept) capture.Disown();
        return taken;
    }

    public SegmentSync Sync => Name is "chunkshadowmap" or "chunkliquiddepth" ? SegmentSync.Deferred : SegmentSync.Ordered;

    private bool TakeCounted(TerrainRenderer capture)
    {
        var counts = Counts!;
        if (_commands == 0)
        {
            (_commands, _count) = (counts.Buffer(), counts.Buffer());
            counts.Upload<uint>(_commands, [], 40);
            counts.UploadAt<uint>(_commands, 0, [6, 1, 0, 0, 0, 6, 1, 6, 0, 0]);
            counts.Upload<uint>(_count, [], 16);
            counts.UploadAt<uint>(_count, 0, [1]);
        }

        return capture.TakeCounted(Vao, !Classic, (counts.Native(_commands), counts.Native(_count), 2), _ => default);
    }

    public void Dispose()
    {
        foreach (var image in _images) image.Dispose();
        GL.DeleteBuffer(_indices);
        GL.DeleteProgram(Program);
        Vao.Dispose();
    }
}

// Leaves for the depth pre-pass: quads in layers one behind the other, waving in the wind (the leaves' wind mode), and an atlas
// whose texels are clear, partly clear or solid. ties: one layer twice, the second copy with other texels.
internal sealed record Foliage(FaceData[] Faces, byte[] Atlas)
{
    private const int LeavesWind = 3 << 25;

    public static Foliage Layers(int layers, int across, int seed, bool ties)
    {
        var random = new Random(seed);
        var faces = new List<FaceData>();
        for (var layer = 0; layer < layers; layer++)
        {
            var z = -(1.5f + 0.4f * layer);
            var side = -z * 2.2f / across;
            for (var i = 0; i < across * across; i++)
            {
                var (x, y) = (((i % across) - (across - 1) / 2f) * side * 0.75f - side / 2,
                    ((i / across) - (across - 1) / 2f) * side * 0.75f - side / 2);
                var (u, v) = (random.Next(4) * 0.25f, random.Next(4) * 0.25f);
                float[] xyz = [x, y, z, x + side, y, z, x + side, y + side, z, x, y + side, z];
                int[] flags = [.. Enumerable.Range(0, 4).Select(c => LeavesWind | ((i + c) % 3 << 29))];
                faces.Add(new FaceData(xyz, 0, u, v, 0.25f, 0.25f, flags, 0, 0, false));
                if (ties && layer == 1) faces.Add(new FaceData(xyz, 0, (u + 0.5f) % 1, v, 0.25f, 0.25f, flags, 0, 0, false));
            }
        }

        return new Foliage([.. faces.OrderBy(_ => random.Next())], Texels());
    }

    // Clear (discarded), a third clear, three quarters, and solid, each in its own pattern
    private static byte[] Texels()
    {
        var pixels = new byte[TerrainScene.AtlasSide * TerrainScene.AtlasSide * 4];
        for (var i = 0; i < TerrainScene.AtlasSide * TerrainScene.AtlasSide; i++)
        {
            var (x, y) = (i % TerrainScene.AtlasSide, i / TerrainScene.AtlasSide);
            var alpha = ((x * 7 + y * 3) % 5, (x + 2 * y) % 7, (3 * x + y) % 11) switch
            {
                (0, _, _) => 0,
                (_, 0, _) => 170,
                (_, _, 0) => 60,
                _ => 255
            };
            (pixels[4 * i], pixels[4 * i + 1], pixels[4 * i + 2], pixels[4 * i + 3]) =
                ((byte)(40 + x * 12), (byte)(90 + y * 9), (byte)(200 - x * 6), (byte)alpha);
        }

        return pixels;
    }
}
