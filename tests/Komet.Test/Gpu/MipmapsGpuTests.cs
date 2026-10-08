using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

// The engine puts a texture into its atlas at runtime (a label, an icon) with glTexSubImage2D and then makes the atlas's mipmaps
// again (BuildMipMaps: glGenerateMipmap, the LOD bias, GL_TEXTURE_MAX_LEVEL), as it makes a map tile's. The copy takes the
// upload from host memory and makes its own levels: every level as OpenGL's to the texel, nothing packed by OpenGL.
[NonParallelizable]
public sealed class MipmapsGpuTests
{
    private const int Side = 512, MaxLevel = 3;

    private const string Vertex = """
        #version 330 core
        out vec2 uv;
        void main() {
            uv = vec2(gl_VertexID & 1, gl_VertexID >> 1);
            gl_Position = vec4(uv * 2.0 - 1.0, 0.5, 1.0);
        }
        """;

    private const string Fragment = """
        #version 330 core
        uniform sampler2D tex;
        uniform float lod;
        in vec2 uv;
        out vec4 color;
        void main() { color = textureLod(tex, uv, lod); }
        """;

    // An atlas, a map tile, and a size whose levels do not halve evenly
    private static readonly (int Width, int Height)[] Sizes = [(128, 64), (96, 96), (37, 53)];

    // between: the upload and the mipmaps come while no segment is open (between frames) rather than between draws
    [TestCase(false)]
    [TestCase(true)]
    public void TheCopyMakesTheMipmapsOpenGlMakes(bool between)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var (program, empty) = (Parity.Linked(Vertex, Fragment), GL.GenVertexArray());
        var textures = Sizes.Select(Texture).ToArray();
        var framebuffer = Made();
        try
        {
            var expected = Frames(null, (program, empty), textures, framebuffer, between);
            ResetContents(textures, Sizes, [.. Enumerable.Range(0, Sizes.Length)]);
            Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
            var drawn = Frames(renderer, (program, empty), textures, framebuffer, between);
            Assert.Multiple(() =>
            {
                Assert.That(scene.Reasons, Is.Empty);
                Assert.That(renderer!.Frame.Closers.Keys.Where(k => k.StartsWith("copied texture", StringComparison.Ordinal)),
                    Is.Empty, "no segment closed for a copy");
                Assert.That(Packed, Is.Zero, "nothing packed by OpenGL after the first frame");
                Assert.That(renderer.Uploads, Is.EqualTo(2 * 2 * Sizes.Length), "each patch and each mipmaps' making the copy's");
                Assert.That(scene.Draws, Is.EqualTo(3 * 2 * Sizes.Length * (MaxLevel + 1)), "every draw Vulkan's");
                Assert.That(Parity.Worst(drawn, expected), Is.Zero, Parity.Differing(drawn, expected));
            });
        }
        finally
        {
            GlTap.Untap();
            Parity.Delete(framebuffer);
            GL.DeleteTextures(textures.Length, textures);
            GL.DeleteVertexArray(empty);
            GL.DeleteProgram(program);
        }
    }

    // As the engine makes its GUI texts and map tiles anew (LoadOrUpdateCairoTexture, LoadOrUpdateTextureFromPixels): the old
    // texture deleted, a new one specified from client memory (BGRA, the unsized RGBA), a tile's mipmaps made; and a text
    // updated whole twice before a draw, between draws and between frames. Their copies are filled from the uploads in host memory: nothing packed by OpenGL, no
    // late signal, no segment closed for them - and every level OpenGL's to the texel
    [Test]
    public void TexturesMadeAnewAreCopiedFromTheirUploads()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var (program, empty) = (Parity.Linked(Vertex, Fragment), GL.GenVertexArray());
        var framebuffer = Made();
        var kept = GL.GenTexture(); // a text: one level
        GL.BindTexture(TextureTarget.Texture2D, kept);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 64, 32, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            Noise(64 * 32, 7));
        GL.BindTexture(TextureTarget.Texture2D, 0);
        int[] made = [0, 0];
        try
        {
            var expected = Anew(null, (program, empty), framebuffer, kept, made);
            GL.TextureSubImage2D(kept, 0, 0, 0, 64, 32, PixelFormat.Rgba, PixelType.UnsignedByte, Noise(64 * 32, 7));
            Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
            var late = renderer!.Frame.LateSignals;
            var drawn = Anew(renderer, (program, empty), framebuffer, kept, made);
            Assert.Multiple(() =>
            {
                Assert.That(scene.Reasons, Is.Empty);
                Assert.That(renderer.Frame.Closers.Keys.Where(k => k.StartsWith("copied texture", StringComparison.Ordinal)),
                    Is.Empty, "no segment closed for a copy");
                Assert.That(Packed, Is.Zero, "nothing packed by OpenGL after the first frame");
                Assert.That(renderer.Frame.LateSignals - late, Is.LessThanOrEqualTo(1), "OpenGL signalled late for the first frame's pack alone");
                Assert.That(Parity.Worst(drawn, expected), Is.Zero, Parity.Differing(drawn, expected));
            });
        }
        finally
        {
            GlTap.Untap();
            Parity.Delete(framebuffer);
            GL.DeleteTextures(3, [kept, made[0], made[1]]);
            GL.DeleteVertexArray(empty);
            GL.DeleteProgram(program);
        }
    }

    // The world map draws a chunk into a tile (96x96, three chunks a side) through a framebuffer object it makes for that and
    // deletes after, between frames, then makes the tile's mipmaps (MultiChunkMapComponent.FinishSetChunks); the object's name
    // comes back for the next tile. The tiles' copies are exported ones a blit refreshes: nothing packed by OpenGL, each tile
    // the one drawn into, every level OpenGL's to the texel.
    [Test]
    public void MapTilesOpenGlDrawsIntoAreBlittedNotPacked()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        rig.Backend!.Flush();
        var (program, empty) = (Parity.Linked(Vertex, Fragment), GL.GenVertexArray());
        var framebuffer = Made();
        var chunks = new[] { Texture((32, 32), 5), Texture((32, 32), 6) };
        try
        {
            var expected = Mapped(null, null, (program, empty), framebuffer, chunks);
            Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
            using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
            Assert.That(renderer, Is.Not.Null, why);
            using var scene = SceneParityGpuTests.Hooked(renderer!, lazy: true);
            var drawn = Mapped(renderer, scene, (program, empty), framebuffer, chunks);
            Assert.Multiple(() =>
            {
                Assert.That(scene.Reasons, Is.Empty);
                Assert.That(renderer!.Copies.Packs, Is.Zero, "nothing packed by OpenGL");
                Assert.That(Parity.Worst(drawn, expected), Is.Zero, Parity.Differing(drawn, expected));
            });
        }
        finally
        {
            GlTap.Untap();
            Parity.Delete(framebuffer);
            GL.DeleteTextures(chunks.Length, chunks);
            GL.DeleteVertexArray(empty);
            GL.DeleteProgram(program);
        }
    }

    // Two tiles, made as LoadOrUpdateTextureFromRgba makes them; before each frame a chunk drawn into one of them in turn, and
    // each frame draws every level of both
    private static float[] Mapped(TerrainRenderer? renderer, SceneRenderer? scene, (int Program, int Empty) gl,
        FrameBufferRef framebuffer, int[] chunks)
    {
        var tiles = new int[2];
        GL.GenTextures(2, tiles);
        foreach (var tile in tiles)
        {
            GL.BindTexture(TextureTarget.Texture2D, tile);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 96, 96, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, new int[96 * 96]);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.NearestMipmapLinear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        try
        {
            for (var frame = 0; frame < 6; frame++)
            {
                if (scene is not null) scene.Framing = false; // between frames: OpenGL draws
                if (frame == 0) Chunk(gl, tiles[1], chunks[1], 8); // every tile a chunk before it is shown
                Chunk(gl, tiles[frame % 2], chunks[frame / 2 % 2], frame);
                if (scene is not null) scene.Framing = true;
                if (renderer is not null) Assert.That(renderer.Start([framebuffer], [0], out var why), Is.True, why);
                Parity.Clear(framebuffer, [0, 0, 0, 0]);
                Drawn(gl, framebuffer, [tiles[0], tiles[1], tiles[1]], [(96, 96), (96, 96), (96, 96)]);
                renderer?.Frame.Close("the frame's end");
            }

            return Parity.Read(framebuffer, Side)[0];
        }
        finally
        {
            GL.DeleteTextures(2, tiles);
        }
    }

    private static void Chunk((int Program, int Empty) gl, int tile, int chunk, int frame)
    {
        var fbo = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D,
            tile, 0);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(gl.Program);
        GL.BindVertexArray(gl.Empty);
        GL.Uniform1(GL.GetUniformLocation(gl.Program, "tex"), 0);
        GL.Uniform1(GL.GetUniformLocation(gl.Program, "lod"), 0f);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, chunk);
        GL.Viewport(32 * (frame % 3), 32 * (frame / 3 % 3), 32, 32);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DeleteFramebuffer(fbo);
        GL.BindTexture(TextureTarget.Texture2D, tile);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Viewport(0, 0, Side, Side);
    }

    // made: the text and the tile, made anew each frame
    private static float[] Anew(TerrainRenderer? renderer, (int Program, int Empty) gl, FrameBufferRef framebuffer, int kept,
        int[] made)
    {
        var packs = 0L;
        for (var frame = 0; frame < 4; frame++)
        {
            if (frame == 1) packs = renderer?.Copies.Packs ?? 0;
            if (renderer is not null) Assert.That(renderer.Start([framebuffer], [0], out var why), Is.True, why);
            Text(made, frame);
            Tile(made, frame);
            if (frame > 0) Whole(kept, frame);
            Parity.Clear(framebuffer, [0, 0, 0, 0]);
            Drawn(gl, framebuffer, [kept, made[0], made[1]], [(64, 32), (40 + 3 * frame, 16), (96, 96)]);
            if (frame > 0) Whole(kept, frame + 10); // the segment open: copied in it, after the draws before
            Drawn(gl, framebuffer, [kept], [(64, 32)], Side / 2);
            renderer?.Frame.Close("the frame's end");
            Whole(kept, frame + 20); // between frames: copied in the next frame's first segment, from that frame's memory
        }

        Packed = (renderer?.Copies.Packs ?? 0) - packs;
        return Parity.Read(framebuffer, Side)[0];
    }

    private static void Text(int[] made, int frame)
    {
        if (made[0] != 0) GL.DeleteTexture(made[0]);
        made[0] = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, made[0]);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 40 + 3 * frame, 16, 0, PixelFormat.Bgra,
            PixelType.UnsignedByte, Noise((40 + 3 * frame) * 16, 50 + frame));
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static void Tile(int[] made, int frame)
    {
        if (made[1] != 0) GL.DeleteTexture(made[1]);
        made[1] = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, made[1]);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, 96, 96, 0, PixelFormat.Rgba, PixelType.UnsignedByte,
            Noise(96 * 96, 70 + frame));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        BuildMipMaps();
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // Updated whole, as LoadOrUpdateCairoTexture updates a text of the same size, twice
    private static void Whole(int texture, int seed)
    {
        GL.BindTexture(TextureTarget.Texture2D, texture);
        for (var i = 0; i < 2; i++)
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 64, 32, PixelFormat.Bgra, PixelType.UnsignedByte,
                Noise(64 * 32, 100 * seed + i));
        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    // Every level of each texture texel for texel, side by side from y
    private static void Drawn((int Program, int Empty) gl, FrameBufferRef framebuffer, int[] textures,
        (int Width, int Height)[] sizes, int y = 0)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(gl.Program);
        GL.BindVertexArray(gl.Empty);
        GL.Uniform1(GL.GetUniformLocation(gl.Program, "tex"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        var x = 0;
        for (var i = 0; i < textures.Length; i++)
        {
            GL.BindTexture(TextureTarget.Texture2D, textures[i]);
            var (width, height) = sizes[i];
            var top = y;
            for (var level = 0; level <= (i == 2 ? MaxLevel : 0); level++)
            {
                var (w, h) = (Math.Max(width >> level, 1), Math.Max(height >> level, 1));
                GL.Viewport(x, top, w, h);
                GL.Uniform1(GL.GetUniformLocation(gl.Program, "lod"), (float)level);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                top += h;
            }

            x += width + 1;
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Viewport(0, 0, Side, Side);
    }

    private static long Packed { get; set; }

    // Three frames: each draws every level of every texture texel for texel; after the first, each texture gets a patch and its
    // mipmaps made again
    private static float[] Frames(TerrainRenderer? renderer, (int Program, int Empty) gl, int[] textures,
        FrameBufferRef framebuffer, bool between)
    {
        var packs = 0L;
        for (var frame = 0; frame < 3; frame++)
        {
            if (frame == 1) packs = renderer?.Copies.Packs ?? 0;
            if (renderer is not null) Assert.That(renderer.Start([framebuffer], [0], out var why), Is.True, why);
            if (between && frame > 0) Patched(textures, frame);
            Parity.Clear(framebuffer, [0, 0, 0, 0]);
            Levels(gl, textures, framebuffer, 0);
            if (!between && frame > 0) Patched(textures, frame);
            Levels(gl, textures, framebuffer, 1);
            renderer?.Frame.Close("the frame's end");
        }

        Packed = (renderer?.Copies.Packs ?? 0) - packs;
        return Parity.Read(framebuffer, Side)[0];
    }

    // half: the upper half of the framebuffer or the lower
    private static void Levels((int Program, int Empty) gl, int[] textures, FrameBufferRef framebuffer, int half)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.Blend);
        GL.UseProgram(gl.Program);
        GL.BindVertexArray(gl.Empty);
        GL.Uniform1(GL.GetUniformLocation(gl.Program, "tex"), 0);
        GL.ActiveTexture(TextureUnit.Texture0);
        var x = 0;
        for (var i = 0; i < textures.Length; i++)
        {
            GL.BindTexture(TextureTarget.Texture2D, textures[i]);
            var (width, height) = Sizes[i];
            var y = half * Side / 2;
            for (var level = 0; level <= MaxLevel; level++)
            {
                var (w, h) = (Math.Max(width >> level, 1), Math.Max(height >> level, 1));
                GL.Viewport(x, y, w, h);
                GL.Uniform1(GL.GetUniformLocation(gl.Program, "lod"), (float)level);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                y += h;
            }

            x += width + 1;
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Viewport(0, 0, Side, Side);
    }

    // As RuntimeUploadTextureToPos: a patch uploaded, then BuildMipMaps
    private static void Patched(int[] textures, int frame)
    {
        for (var i = 0; i < textures.Length; i++)
        {
            var (width, height) = Sizes[i];
            var (w, h) = (Math.Min(16, width / 2), Math.Min(16, height / 2));
            GL.BindTexture(TextureTarget.Texture2D, textures[i]);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, frame * 3 % (width - w), frame * 5 % (height - h), w, h,
                PixelFormat.Rgba, PixelType.UnsignedByte, Noise(w * h, 1000 * frame + i));
            BuildMipMaps();
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static void BuildMipMaps()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.NearestMipmapLinear);
        GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureLodBias, 0f);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMaxLevel, MaxLevel);
    }

    // Before tapping, with the unsized GL_RGBA, as the engine makes its atlases
    private static int Texture((int Width, int Height) size, int seed)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, size.Width, size.Height, 0, PixelFormat.Rgba,
            PixelType.UnsignedByte, Noise(size.Width * size.Height, seed));
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        BuildMipMaps();
        GL.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    // The textures as they were made, for the second run
    private static void ResetContents(int[] textures, (int Width, int Height)[] sizes, int[] seeds)
    {
        for (var i = 0; i < textures.Length; i++)
        {
            var (width, height) = sizes[i];
            GL.BindTexture(TextureTarget.Texture2D, textures[i]);
            GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte,
                Noise(width * height, seeds[i]));
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static int[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, count).Select(_ => random.Next())];
    }

    private static FrameBufferRef Made()
    {
        var framebuffer = new FrameBufferRef { FboId = GL.GenFramebuffer(), Width = Side, Height = Side };
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer.FboId);
        framebuffer.ColorTextureIds = [Target((SizedInternalFormat)0x8058, FramebufferAttachment.ColorAttachment0)];
        framebuffer.DepthTextureId = Target((SizedInternalFormat)0x81A7, FramebufferAttachment.DepthAttachment);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return framebuffer;
    }

    private static int Target(SizedInternalFormat format, FramebufferAttachment attachment)
    {
        var texture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, texture);
        var depth = attachment == FramebufferAttachment.DepthAttachment;
        GL.TexImage2D(TextureTarget.Texture2D, 0, (PixelInternalFormat)format, Side, Side, 0,
            depth ? PixelFormat.DepthComponent : PixelFormat.Rgba, depth ? PixelType.Float : PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, attachment, TextureTarget.Texture2D, texture, 0);
        return texture;
    }
}
