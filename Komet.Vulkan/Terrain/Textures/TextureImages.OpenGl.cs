using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Images whose contents Vulkan missed are read back once: all levels and layers, tight into host memory
// (glGetTextureSubImage, no pack buffer), filled like an upload; GL_PACK_* state restored.
//
// Vulkan-only lets go: OpenGL's textures lack what only Vulkan drew or copied (map tiles, icons, mod framebuffers,
// distant shadow map, render targets). Each such image is written into its texture once via host memory, all levels and
// layers; lean textures (GlTap.Lean) are respecified at full size first (Restored). Waits for the GPU: a switch.
internal sealed unsafe partial class TextureImages
{
    private const int UnpackImageHeight = 0x806E, UnpackSkipImages = 0x806D, UnpackBuffer = 0x88EF;
    private const int Binding2D = 0x8069, BindingArray = 0x8C1D, BindingCube = 0x8514;
    private const int PackAlignment = 0x0D05, PackRowLength = 0x0D02, PackSkipRows = 0x0D03, PackSkipPixels = 0x0D04;

    // Written, bytes, and those left (past most or budget, or no exact read). skip: already restored whole
    public (int Count, long Bytes, int Left) WriteBack(int most, long budget, IReadOnlySet<int> skip)
    {
        if (!Assert(most > 0) || !Assert(budget > 0) || !NotNull(skip) || !_device.WaitIdle()) return default;
        return HandedAll(_images
            .Where(p => p.Value is { Stale: false } && (p.Value.Drawn || GlTap.Behind((uint)p.Key)) && !GlTap.LeanTexture((uint)p.Key) && !skip.Contains(p.Key) &&
                        !GlTap.IsRenderbuffer(p.Key)) // a renderbuffer takes no upload: it stays as OpenGL had it
            .Select(p => (p.Key, p.Value.Image, Math.Clamp(GlTexture.Levels(p.Key), 1, p.Value.Image.Levels), // none past OpenGL's
                Vk.LayoutShaderRead)), (most, budget));
    }

    // Render targets (TerrainTargets' images): like textures, within what is left
    public (int Count, long Bytes, int Left) WriteBack(IEnumerable<SharedImage> targets, System.Func<int, int> resting,
        (int Most, long Budget) room, IReadOnlySet<int> skip)
    {
        if (!NotNull(targets) || !NotNull(resting) || !NotNull(skip) || room.Most <= 0 || room.Budget <= 0) return default;
        return HandedAll(targets
            .Where(i => i.Texture > 0 && !GlTap.LeanTexture((uint)i.Texture) && !skip.Contains(i.Texture))
            .Select(i => (i.Texture, i, 1, resting(i.Texture))), room);
    }

    // In Komet's quiet with tight unpack state, while they fit
    private (int Count, long Bytes, int Left) HandedAll(IEnumerable<(int Texture, SharedImage Image, int Levels, int Resting)> from,
        (int Most, long Budget) room)
    {
        var (count, bytes, left) = (0, 0L, 0);
        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work
        var saved = UnpackTight();
        try
        {
            foreach (var (texture, image, levels, resting) in from.Bounded(MaxImages))
            {
                if (Handed(texture, (image, levels, resting), (room.Most - count, room.Budget - bytes)) is not { } size)
                {
                    left++;
                    continue;
                }

                (count, bytes) = (count + 1, bytes + size);
            }
        }
        finally
        {
            UnpackRestored(saved);
        }

        return (count, bytes, left);
    }

    // null when it does not fit or is not exactly readable
    private long? Handed(int texture, (SharedImage Image, int Levels, int Resting) from, (int Most, long Budget) room)
    {
        var size = Footprint(from.Image, from.Levels);
        if (room.Most <= 0 || size <= 0 || size > room.Budget) return null;
        return Written(texture, from, size) ? size : null;
    }

    // Tight per SharedFormat.Pack; 0 for formats without exact read
    private static long Footprint(SharedImage image, int levels)
    {
        if (image.Format.Pack is not var (_, _, texel)) return 0;
        var bytes = 0L;
        for (var level = 0; level < Math.Min(levels, MaxLevels); level++)
            bytes += (long)Math.Max(image.Width >> level, 1) * Math.Max(image.Height >> level, 1) * image.Layers * texel;
        return bytes < int.MaxValue ? bytes : 0;
    }

    private bool Written(int texture, (SharedImage Image, int Levels, int Resting) from, long size)
    {
        var (image, levels, _) = from;
        if (image.Format.Pack is not var (format, type, texel)) return false;
        using var host = HostBuffer.Create(_device, (ulong)size, bulk: true);
        if (host is null || host.Take((int)size, out _, 16) != 0 || !CopiedOut(from, host.Buffer, texel)) return false;
        var at = 0L;
        for (var level = 0; level < Math.Min(levels, MaxLevels); level++)
        {
            var (width, height) = image.LevelSize(level);
            var pixels = (IntPtr)host.Pointer(at);
            if (!image.Arrayed && !image.Cube)
                GL.TextureSubImage2D(texture, level, 0, 0, width, height, format, type, pixels);
            else GL.TextureSubImage3D(texture, level, 0, 0, 0, width, height, image.Layers, format, type, pixels);
            at += (long)width * height * image.Layers * texel;
        }

        return Assert(at == size);
    }

    private bool CopiedOut((SharedImage Image, int Levels, int Resting) from, ulong buffer, int texel)
    {
        var (image, levels, resting) = from;
        if (!Assert(resting > 0)) return false;
        var aspect = Planes(image.Format.Aspect);
        return _device.Run(commands =>
        {
            image.Moved(commands, (resting, Vk.LayoutTransferSrc));
            var at = 0UL;
            for (var level = 0; level < Math.Min(levels, MaxLevels); level++)
            {
                var (width, height) = image.LevelSize(level);
                var region = new Vk.BufferImageCopy
                {
                    Offset = at, Aspect = aspect, Level = (uint)level, Layers = (uint)image.Layers,
                    Width = (uint)width, Height = (uint)height, Depth = 1
                };
                VkApi.CmdCopyImageToBuffer(commands, image.Image, Vk.LayoutTransferSrc, buffer, 1, &region);
                at += (ulong)((long)width * height * image.Layers * texel);
            }

            image.Moved(commands, (Vk.LayoutTransferSrc, resting));
            VulkanFrame.HostReadable(commands);
        }, []);
    }

    // Buffer copies take one plane: depth of a depth-stencil image
    private static uint Planes(uint aspect) =>
        (aspect & Vk.AspectDepth) != 0 ? Vk.AspectDepth : aspect;

    // Any state: even stale it is closest, as OpenGL holds nothing
    public SharedImage? Held(int texture) =>
        Assert(texture > 0) && _images.TryGetValue(texture, out var entry) ? entry.Image : null;

    // Every recorded level respecified (own internal format), filled from a same-shape image, else blank. True when
    // filled.
    public bool Restored(int texture, SharedImage? image, int resting)
    {
        var gl = GlTexture.Of(texture);
        var kind = (uint)gl.Kind;
        if (!Assert(texture > 0) || gl.Width <= 0 || gl.Height <= 0 || kind is not (Texture2D or GlTexture.Array2D or GlTap.TextureCube))
        {
            GlTap.Restored((uint)texture, true); // nothing known to give it: OpenGL keeps its stand-in (sampled, never past it)
            return false;
        }

        var levels = Math.Clamp(GlTexture.Levels(texture), 1, MaxLevels);
        var layers = LayersOf(kind, gl.Depth);
        var source = image is not null && (image.Width, image.Height, image.Layers) == (gl.Width, gl.Height, layers) ? image : null;
        var filled = Math.Min(levels, source?.Levels ?? 0);
        var size = source is null ? 0 : Footprint(source, filled);
        using var host = size > 0 ? HostBuffer.Create(_device, (ulong)size, bulk: true) : null;
        var full = host is not null && host.Take((int)size, out _, 16) == 0 &&
                   CopiedOut((source!, filled, resting), host.Buffer, source!.Format.Pack!.Value.Bytes);
        var saved = UnpackTight();
        var bound = GL.GetInteger((GetPName)(kind switch { GlTap.TextureCube => BindingCube, GlTexture.Array2D => BindingArray, _ => Binding2D }));
        try
        {
            GL.BindTexture((TextureTarget)kind, texture);
            Specified((kind, (uint)gl.Format), (gl.Width, gl.Height, layers, levels), full ? (source, filled, host) : (null, 0, null));
        }
        finally
        {
            GL.BindTexture((TextureTarget)kind, bound);
            UnpackRestored(saved);
        }

        GlTap.Restored((uint)texture, true);
        return full;
    }

    // glTexImage2D (cube: six faces) or glTexImage3D (array layers) per level, pixels from the host buffer where
    // present
    private static void Specified((uint Kind, uint Format) texture, (int Width, int Height, int Layers, int Levels) size,
        (SharedImage? Image, int Levels, HostBuffer? Host) data)
    {
        var (kind, format) = texture;
        var pack = data.Image is { } image ? image.Format.Pack : null;
        var (blankFormat, blankType) = GlTap.SpecData(format);
        var at = 0L;
        for (var level = 0; level < Math.Min(size.Levels, MaxLevels); level++)
        {
            var (width, height) = (Math.Max(size.Width >> level, 1), Math.Max(size.Height >> level, 1));
            var given = level < data.Levels && pack is not null && data.Host is not null;
            var (pf, pt) = given ? (pack!.Value.Format, pack.Value.Type) : ((PixelFormat)blankFormat, (PixelType)blankType);
            var face = (long)width * height * (given ? pack!.Value.Bytes : 0);
            var pixels = given ? (IntPtr)data.Host!.Pointer(at) : IntPtr.Zero;
            if (kind == GlTexture.Array2D)
                GL.TexImage3D(TextureTarget.Texture2DArray, level, (PixelInternalFormat)format, width, height, size.Layers, 0, pf,
                    pt, pixels);
            else
                for (var f = 0; f < Math.Min(kind == GlTap.TextureCube ? Faces : 1, Faces); f++)
                    GL.TexImage2D(kind == GlTap.TextureCube ? TextureTarget.TextureCubeMapPositiveX + f : TextureTarget.Texture2D,
                        level, (PixelInternalFormat)format, width, height, 0, pf, pt, given ? pixels + (nint)(f * face) : 0);
            at += face * size.Layers;
        }
    }

    // Rows tight (no unpack buffer, no skips), restored after
    private static (int RowLength, int SkipRows, int SkipPixels, int Alignment, int ImageHeight, int SkipImages, int Buffer)
        UnpackTight()
    {
        var saved = (GL.GetInteger(GetPName.UnpackRowLength), GL.GetInteger(GetPName.UnpackSkipRows),
            GL.GetInteger(GetPName.UnpackSkipPixels), GL.GetInteger(GetPName.UnpackAlignment),
            GL.GetInteger((GetPName)UnpackImageHeight), GL.GetInteger((GetPName)UnpackSkipImages),
            GL.GetInteger((GetPName)UnpackBuffer));
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        GL.PixelStore((PixelStoreParameter)UnpackImageHeight, 0);
        GL.PixelStore((PixelStoreParameter)UnpackSkipImages, 0);
        if (saved.Item7 != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
        return saved;
    }

    // The unpack state UnpackTight saved, set again
    private static void UnpackRestored(
        (int RowLength, int SkipRows, int SkipPixels, int Alignment, int ImageHeight, int SkipImages, int Buffer) saved)
    {
        GL.PixelStore(PixelStoreParameter.UnpackRowLength, saved.RowLength);
        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, saved.SkipRows);
        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, saved.SkipPixels);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, saved.Alignment);
        GL.PixelStore((PixelStoreParameter)UnpackImageHeight, saved.ImageHeight);
        GL.PixelStore((PixelStoreParameter)UnpackSkipImages, saved.SkipImages);
        if (saved.Buffer != 0) GL.BindBuffer(BufferTarget.PixelUnpackBuffer, saved.Buffer);
    }

    private bool ReadBack(int texture, Entry entry, out string why)
    {
        why = "";
        var image = entry.Image;
        if (image.Format.Pack is not var (format, type, bytes))
        {
            why = $"texture {texture} cannot be read back";
            return false;
        }

        if (GlTap.LeanTexture((uint)texture))
        {
            why = $"texture {texture} changed in a way Vulkan did not follow, and lean gave OpenGL none of it to read back";
            return false;
        }

        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work
        var saved = PackTight();
        try
        {
            for (var level = 0; level < Math.Min(image.Levels, MaxLevels); level++)
                if (!LevelRead(texture, (entry, level), (format, type, bytes), out why))
                    return false;
        }
        finally
        {
            PackRestored(saved);
        }

        Readbacks++;
        return true;
    }

    private bool LevelRead(int texture, (Entry Entry, int Level) at, (PixelFormat Format, PixelType Type, int Bytes) pack,
        out string why)
    {
        why = "";
        var image = at.Entry.Image;
        var (width, height) = (Math.Max(image.Width >> at.Level, 1), Math.Max(image.Height >> at.Level, 1));
        var size = (long)width * height * image.Layers * pack.Bytes;
        if (size > int.MaxValue || Taken((int)size) is not var (buffer, offset))
        {
            why = $"no host memory to read texture {texture} back";
            return false;
        }

        GL.GetTextureSubImage(texture, at.Level, 0, 0, 0, width, height, image.Layers, pack.Format, pack.Type, (int)size,
            (IntPtr)buffer.Pointer(offset));
        var region = new Vk.BufferImageCopy
        {
            Offset = (ulong)offset, Aspect = image.Format.Aspect, Level = (uint)at.Level, Layers = (uint)image.Layers,
            Width = (uint)width, Height = (uint)height, Depth = 1
        };
        if (!Fill(new Op(OpKind.Fill, image, buffer.Buffer, region, 0)))
        {
            why = $"texture {texture} read back found no room to fill";
            return false;
        }

        ReadBytes += size;
        return true;
    }

    // Rows tight (alignment 1, no row length or skips), no pack buffer
    private static ((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer) PackTight()
    {
        var saved = GlTap.Pack ?? (GlTap.Packing(), (uint)GL.GetInteger((GetPName)0x88ED));
        var (rows, skipRows, skipPixels, alignment) = saved.Store;
        if (alignment != 1) GL.PixelStore((PixelStoreParameter)PackAlignment, 1);
        if (rows != 0) GL.PixelStore((PixelStoreParameter)PackRowLength, 0);
        if (skipRows != 0) GL.PixelStore((PixelStoreParameter)PackSkipRows, 0);
        if (skipPixels != 0) GL.PixelStore((PixelStoreParameter)PackSkipPixels, 0);
        if (saved.Item2 != 0) GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        return saved;
    }

    // The pack state PackTight saved, set again
    private static void PackRestored(((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer) saved)
    {
        var (rows, skipRows, skipPixels, alignment) = saved.Store;
        if (alignment != 1) GL.PixelStore((PixelStoreParameter)PackAlignment, alignment);
        if (rows != 0) GL.PixelStore((PixelStoreParameter)PackRowLength, rows);
        if (skipRows != 0) GL.PixelStore((PixelStoreParameter)PackSkipRows, skipRows);
        if (skipPixels != 0) GL.PixelStore((PixelStoreParameter)PackSkipPixels, skipPixels);
        if (saved.Buffer != 0) GL.BindBuffer(BufferTarget.PixelPackBuffer, saved.Buffer);
    }
}
