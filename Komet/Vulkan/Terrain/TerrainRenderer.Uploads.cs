namespace Komet.Vulkan;

// An upload into a texture whose copy the open segment samples is copied in the segment where it came, so draws before it
// sample the old pixels and those after it the new; one while no segment is open, into a private copy, in the next segment's
// setup (a GUI text drawn anew between frames). An upload of every texel the copy has (a GUI text or map tile updated whole)
// makes a stale copy current again. A texture specified from client memory before it has a copy (a GUI text, a map tile, made
// anew) keeps its upload in host memory, so the copy its first draw makes takes it - and the levels glGenerateMipmap makes from
// it - instead of OpenGL packing the texture. Otherwise the copy went stale and the next draw sampling it closed the segment
// (FluffyClouds' cloud data, every cloud tick), or OpenGL packed it.
internal sealed unsafe partial class TerrainRenderer
{
    private const int MaxSpecBytes = 1 << 20, MaxSpecs = 256, MaxKeptBytes = 4 << 20; // the last: a frame's, of its 32 MB

    // A level 0 kept in host memory for the copy to come: the frame whose memory holds it, where, its size there; whether its
    // levels were made from it since
    private readonly record struct Spec(long Frame, ulong Buffer, long At, int Bytes, int Width, int Height, uint Format,
        bool Mipmaps);

    private readonly Dictionary<int, Spec> _specs = [];
    private (long Frame, long Bytes) _kept; // what the frame's host memory holds of them (a world loading makes hundreds)

    public long Uploads { get; private set; }

    // What is kept for copies to come, for the report: entries, and the bytes of those whose frame's host memory (not the managed
    // heap) still holds them
    public (int Count, long Bytes) KeptUploads
    {
        get
        {
            var bytes = 0L;
            foreach (var spec in _specs.Values.Bounded(MaxSpecs))
                if (spec.Frame > Frame.Done)
                    bytes += spec.Bytes;
            return Assert(bytes >= 0) ? (_specs.Count, bytes) : default;
        }
    }

    public void Specified(int texture, int level, (int Format, int Width, int Height) spec, (uint Format, uint Type) data,
        IntPtr pixels)
    {
        if (!Assert(level >= 0)) return;
        if (!Copies.Has(texture))
        {
            Kept(texture, level, spec, data, pixels);
            return;
        }

        if (Copies.Fits(texture, level, spec))
        {
            if (pixels == 0 || !Uploaded(texture, level, (0, 0, spec.Width, spec.Height), data, pixels))
                Copies.Changed(texture);
            return;
        }

        if (Copies.InUse(texture, Frame.Number)) Frame.Close("a copied texture specified anew");
        Copies.Forget(texture, Frame.Number);
        _ = Assert(!Copies.Has(texture));
        Kept(texture, level, spec, data, pixels);
    }

    // Anything else done to a texture: what was kept of it is not what it holds any more
    public void Changed(int texture)
    {
        _ = _specs.Remove(texture);
        Copies.Changed(texture);
    }

    // GL_TEXTURE_BASE_LEVEL: the levels are made from another base. GL_TEXTURE_MAX_LEVEL lowered after the levels were made
    // (BuildMipMaps) leaves fewer for the copy; raised, it would reach levels made before them - the engine makes a texture anew
    // instead (LoadOrUpdateTextureFromPixels), whose levels glGenerateMipmap makes all.
    public void Tuned(int texture, uint name, float value)
    {
        if (name == 0x813C) _ = _specs.Remove(texture);
        Copies.Tuned(texture, name, value);
    }

    private void Kept(int texture, int level, (int Format, int Width, int Height) spec, (uint Format, uint Type) data,
        IntPtr pixels)
    {
        _ = _specs.Remove(texture);
        var kind = spec.Format is 0x1907 or 0x8051 ? SharedFormat.Rgba8 : SharedFormat.Of(spec.Format);
        if (level != 0 || pixels == 0 || GlTap.Unpacking || kind is not { Aspect: Vk.AspectColor } format ||
            spec.Width <= 0 || spec.Height <= 0 || (long)spec.Width * spec.Height * 4 > MaxSpecBytes) return;
        if (_kept.Frame != Frame.Number) _kept = (Frame.Number, 0);
        if (_kept.Bytes + (long)spec.Width * spec.Height * 4 > MaxKeptBytes) return;
        _kept.Bytes += (long)spec.Width * spec.Height * 4;
        if (_specs.Count >= MaxSpecs) _specs.Clear();
        var at = Staged(format.Vulkan, (spec.Width, spec.Height), data, pixels, out var staged);
        if (at >= 0)
            _specs[texture] = new Spec(Frame.Number, staged.Buffer, at, staged.Bytes, spec.Width, spec.Height, format.Vulkan, false);
    }

    // The new copy filled from what was kept of its texture, in host memory still; false when it was not kept as the copy is
    private bool Seeded(int texture, SharedImage copy)
    {
        if (!_specs.Remove(texture, out var spec) || copy.Exported || spec.Frame <= Frame.Done) return false;
        if ((spec.Width, spec.Height, spec.Format) != (copy.Width, copy.Height, copy.Format.Vulkan) ||
            (copy.Levels > 1 && !spec.Mipmaps)) return false;
        if (Frame.Restaged((spec.Buffer, spec.At, spec.Bytes)) is not var (buffer, at)) return false; // into the frame's own
        var region = new Vk.BufferImageCopy
        {
            Offset = (ulong)at, Aspect = copy.Format.Aspect, Layers = 1, Width = (uint)spec.Width, Height = (uint)spec.Height,
            Depth = 1
        };
        return Copies.Queue(texture, (buffer, at, spec.Bytes), region, Frame.Number, whole: true,
            mipmaps: copy.Levels > 1 ? copy.Levels : 0) && Tallied();
    }

    private bool Seedable(int texture) => _specs.TryGetValue(texture, out var spec) && spec.Frame > Frame.Done;

    public bool Uploaded(int texture, int level, (int X, int Y, int Width, int Height) rect, (uint Format, uint Type) data,
        IntPtr pixels)
    {
        var image = Copies.Overwritable(texture);
        var whole = level == 0 && rect.X == 0 && rect.Y == 0 && image is not null &&
                    (rect.Width, rect.Height) == (image.Width, image.Height);
        if (!whole) image = Copies.Current(texture);
        if (image is null || (Frame.Open ? !Frame.Holds(image) : image.Exported) ||
            !Assert(rect.Width >= 0 && rect.Height >= 0) || level < 0 || level >= image.Levels) return false;
        var (width, height) = (Math.Max(1, image.Width >> level), Math.Max(1, image.Height >> level));
        if (rect.X < 0 || rect.Y < 0 || rect.X + rect.Width > width || rect.Y + rect.Height > height) return false;
        if (rect.Width == 0 || rect.Height == 0) return true; // nothing uploaded
        var at = Staged(image.Format.Vulkan, (rect.Width, rect.Height), data, pixels, out var staged);
        if (at < 0) return false;
        var region = new Vk.BufferImageCopy
        {
            Offset = (ulong)at, Aspect = image.Format.Aspect, Level = (uint)level, Layers = 1, X = rect.X, Y = rect.Y,
            Width = (uint)rect.Width, Height = (uint)rect.Height, Depth = 1
        };
        if (!Frame.Open)
            return Copies.Queue(texture, (staged.Buffer, at, staged.Bytes), region, Frame.Number, whole) && Tallied();
        Filled(); // a fill of the copy waiting for the setup comes before
        if (!Copied(image, staged.Buffer, region)) return false;
        if (whole) Copies.Overwritten(texture);
        return Assert(Frame.Open);
    }

    // Converted into the frame's host memory as OpenGL unpacks it: where it lies, -1 when it cannot be
    private long Staged(uint format, (int Width, int Height) rect, (uint Format, uint Type) data, IntPtr pixels,
        out (ulong Buffer, int Bytes) staged)
    {
        staged = default;
        var (source, target) = TextureUploads.Bytes(format, data);
        if (source == 0 || pixels == 0 || Frame.Staging is not { } staging || !Assert(rect.Width > 0 && rect.Height > 0))
            return -1;
        var unpack = GlTap.Unpack;
        var row = (unpack.RowLength > 0 ? unpack.RowLength : rect.Width) * source;
        var alignment = unpack.Alignment is 1 or 2 or 4 or 8 ? unpack.Alignment : 4;
        var stride = (row + alignment - 1) / alignment * alignment;
        var at = staging.Take(rect.Width * rect.Height * target, out var into, 16);
        if (at < 0) return -1;
        var first = (byte*)pixels + (long)unpack.SkipRows * stride + (long)unpack.SkipPixels * source;
        TextureUploads.Convert(format, data, first, (rect.Width, rect.Height, stride), into);
        staged = (staging.Buffer, into.Length);
        return at;
    }

    // glGenerateMipmap on a texture with a current color copy: the copy's levels are made from its base, in the open segment
    // that holds it, or (a private one) in the next segment's setup; false when they cannot be (the copy changed then)
    public bool Mipmapped(int texture)
    {
        if (!Copies.Has(texture) && _specs.TryGetValue(texture, out var spec))
        {
            _specs[texture] = spec with { Mipmaps = true };
            return true; // no copy to change: the one to come makes its levels from what was kept
        }

        if (Copies.Current(texture) is not { } image || image.Format.Aspect != Vk.AspectColor) return false;
        var levels = Copies.Made(texture);
        if (levels == 1) return Assert(image.Levels >= 1); // none of the levels made is the copy's
        if (levels == 0) return false;
        if (!Frame.Open) return Copies.Mipmapped(texture, levels, Frame.Number) && Tallied();
        Filled(); // a fill of the copy waiting for the setup comes before
        return Frame.Holds(image) && Frame.Transfer(image, commands => Mipmaps.Record(commands, image, levels)) && Tallied();
    }

    private bool Tallied()
    {
        Uploads++;
        return Assert(Uploads > 0);
    }

    private bool Copied(SharedImage image, ulong staged, Vk.BufferImageCopy region)
    {
        var (from, to) = (staged, image.Image);
        if (!Assert(from != 0) || !Assert(to != 0) || !Frame.Transfer(image, commands =>
            {
                var copy = region;
                VkApi.CmdCopyBufferToImage(commands, from, to, Vk.LayoutTransferDst, 1, &copy);
            })) return false;
        return Tallied();
    }
}
