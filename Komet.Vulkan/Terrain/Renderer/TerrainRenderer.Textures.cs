namespace Komet.Vulkan;

// Textures as OpenGL is told of them: uploads, parameters and mipmaps reach their images (TextureImages), copies and
// clears run on the images in the open segment, and an empty unit samples a blank image.
internal sealed unsafe partial class TerrainRenderer
{
    private static readonly Samplers.State BlankSampling = Samplers.Clamped;

    private SharedImage? _blank;
    private bool _blankless; // making it failed: OpenGL draws those

    public Samplers.State? SamplingOf(int texture) => Assert(texture >= 0) ? Images.SamplingOf(texture) : null;

    // The texture's image, now held by the open segment
    public SharedImage? Copy(int texture, out string why, bool stand = false)
    {
        why = "";
        if (!Assert(texture > 0)) return null;
        var image = Images.Get(texture, out why, stand);
        if (image is not null && Frame.Open) Hold(image);
        return image;
    }

    // Taken on demand from rest (sampling layout)
    private void Hold(SharedImage image)
    {
        if (!NotNull(image) || Frame.Holds(image) || !Assert(Frame.Open)) return;
        _ = Frame.Take(new VulkanFrame.Shared(image, Vk.LayoutShaderRead));
    }

    // Images a draw samples and draws into, held by the open segment (targets come with every segment)
    private void Holding(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        if (!Frame.Open) return;
        foreach (var image in sampled.Bounded(MaxSamplers)) Hold(image);
        foreach (var color in target.Colors.Bounded(GlTap.MaxBuffers))
            if (color is not null)
                Hold(color);
        if (target.Depth is { } depth) Hold(depth);
    }

    public void Imaged(int texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, IntPtr pixels)
    {
        if (!Assert(texture > 0)) return;
        if (Targets.Find(texture) is null) Images.Imaged(texture, target, levels, spec, data, pixels);
        else if (levels.Level == 0) Unshared(texture); // a render target specified anew: its image is made again
    }

    public bool SubImaged(int texture, int level, (int X, int Y, int Z, int Width, int Height, int Depth) box,
        (uint Format, uint Type) data, IntPtr pixels) =>
        Assert(texture > 0) && Assert(level >= 0) && Images.SubImaged(texture, level, box, data, pixels);

    // Anything else done to the texture: its image is read back on next use
    public void Changed(int texture)
    {
        if (Assert(texture >= 0)) Images.Changed(texture);
    }

    // BASE_LEVEL, MAX_LEVEL and the sampling parameters
    public void Tuned(int texture, uint name, float value)
    {
        if (Assert(texture >= 0) && Finite(value)) Images.Tuned(texture, name, value);
    }

    // glGenerateMipmap: the image makes its levels too; false when impossible (the image is then changed)
    public bool Mipmapped(int texture) => Assert(texture >= 0) && Images.Mipmapped(texture);

    // An empty unit samples incomplete texture 0: (0, 0, 0, 1) (GL 4.6 core 11.1.3.5). Mesa samples it as 1x1 with the
    // unit's sampling, border included, so a clamp-to-border sampler mixes the border in. Without a sampler object a 1x1
    // (0, 0, 0, 1) image matches and untextured GUI draws stay Vulkan's; with one, OpenGL draws.
    public TerrainDraw.Texture? Unbound(string type, int samplerObject)
    {
        if (type != "sampler2D" || samplerObject != 0 || GlTap.DefaultTextureTouched || _blankless) return null;
        _blank ??= Blank(Device);
        _blankless = _blank is null;
        if (_blank is not { } blank) return null;
        return new TerrainDraw.Texture(blank.View, Sampling.Get(BlankSampling), Vk.LayoutShaderRead);
    }

    private static SharedImage? Blank(VulkanDevice device)
    {
        return Owned.Kept(SharedImage.Private(device, (1, 1, 1, 1), SharedFormat.Rgba8, Vk.Sampled | Vk.TransferDst, out _), device,
            static (image, device) =>
            {
                var handle = image.Image;
                return device.Run(commands => Blanked(commands, handle), []) ? null : "not cleared";
            }, out _);
    }

    private static void Blanked(IntPtr commands, ulong image)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(image != 0)) return;
        var range = new Vk.ColorRange { Aspect = Vk.AspectColor, Levels = 1, Layers = 1 };
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, DstAccess = Vk.AccessTransferWrite, OldLayout = Vk.LayoutUndefined,
            NewLayout = Vk.LayoutTransferDst, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored,
            Image = image, Range = range
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.StageTransfer, 0, 0, null, 0, null, 1, &barrier);
        var black = stackalloc float[] { 0, 0, 0, 1 };
        VkApi.CmdClearColorImage(commands, image, Vk.LayoutTransferDst, black, 1, &range);
        (barrier.SrcAccess, barrier.DstAccess) = (Vk.AccessTransferWrite, Vk.AccessMemoryRead);
        (barrier.OldLayout, barrier.NewLayout) = (Vk.LayoutTransferDst, Vk.LayoutShaderRead);
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }

    // glCopyImageSubData: texel-exact for same formats at any level/layer (Z: layer or face, Depth of them), else a
    // single-layer converting blit. Otherwise read back from OpenGL's copy, or missing while OpenGL rests.
    public bool Copied((uint Texture, int Level, int X, int Y, int Z) to, (uint Texture, int Level, int X, int Y, int Z) from,
        (int Width, int Height, int Depth) size)
    {
        var textures = Images;
        if (!Assert(size.Width >= 0)) return false;
        var into = Targets.Find((int)to.Texture) ?? textures.Get((int)to.Texture, out _);
        var source = into is null ? null : SampledFind((int)from.Texture) ?? textures.Get((int)from.Texture, out _);
        if (into is null || source is null || ReferenceEquals(into, source)) return Uncopied("of a texture into itself or one with no image");
        var (fromAt, toAt) = ((from.Level, from.Z, Layers: size.Depth), (to.Level, to.Z, Layers: size.Depth));
        var copied = source.Format == into.Format
            ? Exact((source, into), (fromAt, toAt), ((from.X, from.Y), (to.X, to.Y), (size.Width, size.Height)))
            : size.Depth == 1 && Blitted((source, into), ((from.X, from.Y), (to.X, to.Y), (size.Width, size.Height)),
                ((from.Level, from.Z), (to.Level, to.Z)));
        if (!copied) return Uncopied($"between formats {source.Format.Vulkan} and {into.Format.Vulkan}, or out of bounds");
        textures.Wrote((int)to.Texture);
        return true;
    }

    // glCopyTexSubImage2D: read buffer's level/layer (depth for depth textures) into the texture's (Z: cube face)
    public bool FramebufferRead(int texture, int level, (int X, int Y, int Z) at, (int Fbo, int Read) from,
        (int X, int Y, int Width, int Height) rect)
    {
        var textures = Images;
        if (from.Fbo <= 0 || !Assert(texture > 0) || !Assert(level >= 0)) return false;
        var into = Targets.Find(texture) ?? textures.Get(texture, out _);
        if (into is null) return Uncopied("into a texture with no image");
        var attachment = (into.Format.Aspect & Vk.AspectDepth) != 0 ? GlFramebuffer.Depth : from.Read;
        if (attachment is < 0 or > GlFramebuffer.Colors || ReadAt(from.Fbo, attachment, out var read, out _) is not { } source ||
            ReferenceEquals(source, into)) return Uncopied("from a framebuffer it cannot read");
        var copied = source.Format == into.Format
            ? Exact((source, into), ((read.Level, read.Layer, 1), (level, at.Z, 1)),
                ((rect.X, rect.Y), (at.X, at.Y), (rect.Width, rect.Height)))
            : Blitted((source, into), ((rect.X, rect.Y), (at.X, at.Y), (rect.Width, rect.Height)), (read, (level, at.Z)));
        if (!copied) return Uncopied($"from a framebuffer between formats {source.Format.Vulkan} and {into.Format.Vulkan}");
        textures.Wrote(texture);
        return true;
    }

    // Resting OpenGL makes no copy either: nothing to read back
    private bool Uncopied(string why)
    {
        Miss(GlTap.Resting ? $"a texture copy {why}" : $"a texture copy {why} (OpenGL's is read back)");
        return false;
    }

    private bool Exact((SharedImage From, SharedImage To) images,
        ((int Level, int Layer, int Layers) From, (int Level, int Layer, int Layers) To) at,
        ((int X, int Y) From, (int X, int Y) To, (int Width, int Height) Size) region)
    {
        if (!Inside(images.From, at.From, (region.From, region.Size)) || !Inside(images.To, at.To, (region.To, region.Size)) ||
            (!Frame.Open && !Frame.Begin(out _))) return false;
        Hold(images.From);
        Hold(images.To);
        var aspect = images.From.Format.Aspect;
        var copy = new Vk.ImageCopy
        {
            Source = new Vk.Layers { Aspect = aspect, Level = (uint)at.From.Level, BaseLayer = (uint)at.From.Layer, Count = (uint)at.From.Layers },
            SourceX = region.From.X, SourceY = region.From.Y,
            Target = new Vk.Layers { Aspect = aspect, Level = (uint)at.To.Level, BaseLayer = (uint)at.To.Layer, Count = (uint)at.To.Layers },
            TargetX = region.To.X, TargetY = region.To.Y, Width = (uint)region.Size.Width, Height = (uint)region.Size.Height,
            Depth = 1
        };
        return Assert(Frame.Holds(images.To)) && Assert(Frame.Holds(images.From)) &&
               Frame.CopyImage(images.From, images.To, [copy]);
    }

    private static bool Inside(SharedImage image, (int Level, int Layer, int Layers) at, ((int X, int Y) At, (int Width, int Height) Size) box)
    {
        if (!Index(at.Level, image.Levels) || at.Layer < 0 || at.Layers <= 0 || at.Layer + at.Layers > image.Layers) return false;
        var (width, height) = (Math.Max(image.Width >> at.Level, 1), Math.Max(image.Height >> at.Level, 1));
        return box.Size.Width > 0 && box.Size.Height > 0 && box.At.X >= 0 && box.At.Y >= 0 &&
               box.At.X + box.Size.Width <= width && box.At.Y + box.Size.Height <= height;
    }

    // Spec-required blit formats (A2B10G10R10 too); others (B10G11R11F, R16, RGBA16) per VulkanDevice.Blits. Converts
    // like OpenGL's blit: via floats, normalized rounded to nearest
    internal static readonly uint[] Blittable =
    [
        Vk.FormatRgba8, Vk.FormatSrgba8, Vk.FormatRgba16F, Vk.FormatRgba32F, Vk.FormatR8, Vk.FormatRg8, Vk.FormatR16F,
        Vk.FormatRg16F, Vk.FormatR32F, Vk.FormatA2B10G10R10
    ];

    internal static bool Converts(SharedFormat from, SharedFormat to) =>
        Assert(from.Aspect != 0) && Assert(to.Aspect != 0) && (from == to ||
                                      (Blits(from.Vulkan, false) && Blits(to.Vulkan, true)));

    private static bool Blits(uint format, bool into) =>
        Array.IndexOf(Blittable, format) >= 0 || VulkanDevice.Blits(format, into);

    private bool Blitted((SharedImage From, SharedImage To) images,
        ((int X, int Y) From, (int X, int Y) To, (int Width, int Height) Size) region,
        ((int Level, int Layer) From, (int Level, int Layer) To) at)
    {
        if (images.From.Format.Aspect != images.To.Format.Aspect || !Converts(images.From.Format, images.To.Format) ||
            (images.From.OpaqueAlpha && !images.To.OpaqueAlpha) ||
            !Inside(images.From, (at.From.Level, at.From.Layer, 1), (region.From, region.Size)) ||
            !Inside(images.To, (at.To.Level, at.To.Layer, 1), (region.To, region.Size)) ||
            (!Frame.Open && !Frame.Begin(out _))) return false;
        Hold(images.From);
        Hold(images.To);
        var blit = new Vk.ImageBlit
        {
            Source = new Vk.Layers { Level = (uint)at.From.Level, BaseLayer = (uint)at.From.Layer, Count = 1 },
            SourceX0 = region.From.X, SourceY0 = region.From.Y, SourceX1 = region.From.X + region.Size.Width,
            SourceY1 = region.From.Y + region.Size.Height, SourceZ1 = 1,
            Target = new Vk.Layers { Level = (uint)at.To.Level, BaseLayer = (uint)at.To.Layer, Count = 1 },
            TargetX0 = region.To.X, TargetY0 = region.To.Y,
            TargetX1 = region.To.X + region.Size.Width, TargetY1 = region.To.Y + region.Size.Height, TargetZ1 = 1
        };
        return Frame.Blit([images], blit, false);
    }

    // glClearTex(Sub)Image of whole levels (any layer run) as vkCmdClearColorImage/DepthStencilImage, value read as
    // OpenGL does (zeros for none). Boxes smaller than the level have no such clear and are missing.
    public bool TextureCleared(GlTap.TextureClear clear)
    {
        var images = Images;
        if (!Assert(clear.Level >= 0)) return false;
        var name = (int)clear.Name;
        var image = name > 0 ? Targets.Find(name) ?? images.Get(name, out _, stand: true) : null;
        if (image is null || clear.Level >= image.Levels) return ClearMissing("into a texture with no image or no such level");
        var (width, height) = (Math.Max(image.Width >> clear.Level, 1), Math.Max(image.Height >> clear.Level, 1));
        var box = clear.Box.Width < 0 ? (0, 0, 0, width, height, image.Layers) : clear.Box;
        if (box.Item1 != 0 || box.Item2 != 0 || box.Item4 != width || box.Item5 != height || box.Item3 < 0 || box.Item6 <= 0 ||
            box.Item3 + box.Item6 > image.Layers) return ClearMissing("of part of a level");
        var aspect = ClearedAspect(image.Format.Aspect, clear.Data.Format);
        if (aspect == 0) return ClearMissing($"of 0x{clear.Data.Format:X} into a {image.Format.Vulkan} image");
        var (color, depth) = ClearValue(clear.Data, clear.Value);
        var range = new Vk.ColorRange
        {
            Aspect = aspect, BaseLevel = (uint)clear.Level, Levels = 1, BaseLayer = (uint)box.Item3, Layers = (uint)box.Item6
        };
        if (!Frame.Open && !Frame.Begin(out _)) return ClearMissing("with no segment for it");
        Hold(image);
        if (!Frame.ClearImage(image, range, color, depth)) return ClearMissing("the segment holds no image of");
        images.Wrote(name);
        return Assert(Frame.Open);
    }

    private bool ClearMissing(string why)
    {
        Miss($"a texture clear {why}");
        return false;
    }

    // color for color, depth (and stencil if given and held) for depth; 0 when it does not fit
    private static uint ClearedAspect(uint aspect, uint format)
    {
        if ((aspect & Vk.AspectColor) != 0) return format is GlEnum.DepthComponent or GlEnum.DepthStencil or GlEnum.StencilIndex ? 0 : Vk.AspectColor;
        return format switch
        {
            GlEnum.DepthComponent => Vk.AspectDepth,
            GlEnum.DepthStencil => aspect & Vk.AspectDepthStencil,
            GlEnum.StencilIndex => aspect & Vk.AspectStencil,
            _ => 0
        };
    }

    // As OpenGL reads it (none: zeros; missing color components: 0, 0, 0, 1)
    private static ((float R, float G, float B, float A) Color, Vk.DepthStencilValue Depth) ClearValue((uint Format, uint Type) data,
        nint value)
    {
        if (value == 0 || !Assert(data.Format != uint.MaxValue) || !Assert(data.Type != uint.MaxValue)) return (default, default);
        var at = (byte*)value;
        switch (data.Format)
        {
            case GlEnum.DepthStencil when data.Type == GlEnum.UnsignedInt248:
                return (default, new Vk.DepthStencilValue { Depth = (*(uint*)at >> 8) / (float)0xFFFFFF, Stencil = *(uint*)at & 0xFF });
            case GlEnum.DepthStencil when data.Type == GlEnum.Float32UnsignedInt248:
                return (default, new Vk.DepthStencilValue { Depth = *(float*)at, Stencil = ((uint*)at)[1] & 0xFF });
            case GlEnum.DepthComponent:
                return (default, new Vk.DepthStencilValue { Depth = Component(at, 0, data.Type) });
            case GlEnum.StencilIndex:
                var index = data.Type switch
                {
                    GlEnum.UnsignedInt or GlEnum.Int => *(uint*)at, GlEnum.UnsignedShort or GlEnum.Short => *(ushort*)at, _ => at[0]
                };
                return (default, new Vk.DepthStencilValue { Stencil = index & 0xFF });
        }

        var count = Components(data.Format);
        Span<float> rgba = [0, 0, 0, 1];
        for (var i = 0; i < Math.Min(count, 4); i++) rgba[i] = Component(at, i, data.Type);
        if (data.Format is GlEnum.Bgra or GlEnum.Bgr) (rgba[0], rgba[2]) = (rgba[2], rgba[0]);
        return ((rgba[0], rgba[1], rgba[2], rgba[3]), default);
    }

    private static float Component(byte* at, int index, uint type) => type switch
    {
        GlEnum.Float => ((float*)at)[index],
        GlEnum.HalfFloat => (float)((Half*)at)[index],
        GlEnum.UnsignedShort => ((ushort*)at)[index] / 65535f,
        GlEnum.Short => Math.Max(((short*)at)[index] / 32767f, -1),
        GlEnum.UnsignedInt => (float)(((uint*)at)[index] / (double)uint.MaxValue),
        GlEnum.Int => (float)Math.Max(((int*)at)[index] / (double)int.MaxValue, -1),
        GlEnum.Byte => Math.Max(((sbyte*)at)[index] / 127f, -1),
        _ => at[index] / 255f
    };
}
