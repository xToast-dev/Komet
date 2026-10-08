using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Small copies are refreshed every frame, as nobody says when they change; large ones when the engine changed them. A copy is
// private (Vulkan's own image, in memory no submission names) unless something needs it exported: OpenGL blits the texture's
// levels into a texture of its own (Through) and packs that into GlStaging (glGetTextureImage into a pack buffer), the setup
// of a segment opening after OpenGL's signal copies them into the image (Flush). Packed straight from the engine's texture, a
// texture Mesa has not finalized (its levels specified one by one, never drawn with) is read on the CPU: a wait for the GPU,
// and a crash into imported memory; a texture made with glTextureStorage is final from the start. Exported, as a SharedImage
// OpenGL blits into level by level, are a copy Vulkan draws into (a stand, which OpenGL takes back), one refreshed in most
// frames (Hot: one blit costs less than a pack and a copy then), a small one OpenGL draws into through a framebuffer (Drawn: a
// map tile, rendered between frames and its mipmaps made, each time packed whole otherwise) and every copy without staging.
// The blit is a framebuffer
// blit: the engine's textures carry the unsized GL_RGBA, which glCopyImageSubData refuses to match with GL_RGBA8.
// At most cap copies live (the creative inventory keeps hundreds of icon textures): a new one takes the place of the copy
// sampled longest ago, never of one sampled in the frame being drawn, and that one is retired through the frame's fence.
internal sealed unsafe class TerrainTextures(
    VulkanDevice device, GlStaging? staging = null, int cap = TerrainTextures.MaxTextures) : IDisposable
{
    public const int MaxTextures = 1024;
    private const int MaxLevels = 16, SmallSide = 1024, TextureWidth = 0x1000, TextureHeight = 0x1001, TargetName = 0x1006;
    private const int Faces = 6;
    private const int IdleFrames = 60; // a copy unsampled that long goes between frames once the copies near the cap
    private const int InternalFormat = 0x1003; // glTexImage2D with GL_RGBA: RGBA8 on every driver
    private const int UnsizedRgb = 0x1907, Rgb8 = 0x8051;
    private const uint MinLod = 0x813A, MaxLod = 0x813B, BaseLevelName = 0x813C, MaxLevelName = 0x813D, LodBias = 0x8501;
    private const int HotFrames = 8, ColdFrames = 600, MaxFills = 1024, MaxThrough = 16, ThroughFrames = 120;
    private const ulong HotWindow = 0x1FFFE; // the last 16 frames, as Recent shifted to the frame now holds them
    private const long ThroughBytes = 16L << 20; // a larger one goes right after its pack
    private const uint Sampled = Vk.Sampled | Vk.TransferDst | Vk.TransferSrc;

    private readonly Dictionary<int, Copy> _copies = [];
    private readonly int _cap = Math.Clamp(cap, 1, MaxTextures);
    private readonly GlTexture.Copier _copier = new();
    private readonly HashSet<int> _hot = []; // refreshed in every frame lately: copied exported
    private readonly HashSet<int> _drawn = []; // OpenGL drew into them: copied exported while small, never cold
    private readonly List<Fill> _fills = []; // packed, not copied into their images yet
    private readonly List<int> _moving = [];
    private readonly Packing _packing = new();

    // Immutable textures OpenGL blits into and packs from, by sized format, size and levels; let go when unused for long
    private readonly Dictionary<(int Format, int Width, int Height, int Levels, int Layers), (int Texture, long Used)> _through = [];
    private readonly List<(int, int, int, int, int)> _unused = [];
    private long _refreshed = -1; // the frame whose small copies are fresh

    public IEnumerable<SharedImage> Images => _copies.Values.Select(c => c.Image);

    public int Count => _copies.Count;

    public long Version { get; private set; }

    // Whether every change is told (the scene's GlTap hears every upload and draw): small copies need no refresh per frame
    public bool Trusting { get; set; }

    public IEnumerable<(int Texture, SharedImage Image)> Copied => _copies.Select(c => (c.Key, c.Value.Image));

    // For the report: copies made private, levels packed and their bytes, fills copied, copies moved between the kinds
    public int Private { get; private set; }
    public long Packs { get; private set; }
    public long PackedBytes { get; private set; }
    public long Fills { get; private set; }
    public long Moves { get; private set; }
    public long Evictions { get; private set; } // copies that made room for another

    private sealed class Copy(SharedImage image, bool small)
    {
        public SharedImage Image { get; } = image;
        public bool Small { get; } = small;
        public bool Dirty { get; set; } = true;
        public Samplers.State Sampling { get; set; }
        public bool Resample { get; set; } // a parameter Samplers could not follow: read the sampling at the refresh

        // Vulkan drew into the copy (a framebuffer of the scene's own): the GL texture is behind
        public bool Written { get; set; }

        public bool Stand { get; init; } // Vulkan may draw into it: exported for good
        public bool Filled { get; set; } // a fill of it was recorded: its image holds contents (and metadata) since
        public long Refreshed { get; set; } = -1; // the frame of its last refresh
        public ulong Recent { get; set; } // bit n: refreshed n frames before Refreshed

        // GL_TEXTURE_BASE_LEVEL and GL_TEXTURE_MAX_LEVEL as last set: glGenerateMipmap makes the levels between
        public int BaseLevel { get; set; }
        public int MaxLevel { get; set; } = int.MaxValue;
    }

    // A private copy's levels as OpenGL packed them, or an upload the CPU staged in the frame's host memory (Host: Frame names
    // the frame whose memory holds it), waiting for a segment's setup
    private readonly record struct Fill(int Texture, SharedImage Image, GlStaging.Range Staged, Vk.BufferImageCopy[] Levels)
    {
        public long Frame { get; init; }
        public bool Host => Staged.Ticket < 0;
        public int Mipmaps { get; init; } // after the copy, the levels made from the base (Mipmaps.Record), 0 for none
        public int Bytes { get; init; } // of a host fill: what it copies from host memory
    }

    // stand: Vulkan may draw into it, so it is exported (a private copy of the texture must go first: Movable, Forget).
    // frame: the one being drawn (VulkanFrame.Number), whose copies stay; none (negative): no copy makes room for a new one.
    public SharedImage? Get(int texture, out string why, bool stand = false, long frame = -1)
    {
        why = "";
        if (!Assert(texture > 0) || !Assert(_copies.Count <= _cap)) return null;
        if (_copies.TryGetValue(texture, out var known))
        {
            known.Image.Used = Math.Max(known.Image.Used, frame); // the draw asking samples it: no new copy evicts it
            return known.Image;
        }

        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)InternalFormat, out int format);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureWidth, out int width);
        GL.GetTextureLevelParameter(texture, 0, (GetTextureParameter)TextureHeight, out int height);
        // RGB textures as RGBA8 (the blit fills alpha with 1 as OpenGL samples them), the rest by their own format; a depth
        // only as DEPTH_COMPONENT32F, as a blit between depth formats is an error
        var kind = format is UnsizedRgb or Rgb8 ? SharedFormat.Rgba8 : SharedFormat.Of(format);
        if (kind == SharedFormat.Depth32F && format != GlInterop.Depth32F) kind = null;
        if (kind is not { } shared || width <= 0 || height <= 0)
        {
            why = $"texture {texture} (0x{format:X}, {width}x{height}) has no shared copy";
            return null;
        }

        // A cube map is copied private only: packed face by face through a cube of OpenGL's own (the level queries read face 0)
        GL.GetTextureParameter(texture, (GetTextureParameter)TargetName, out int target);
        var cube = target == (int)GlTap.TextureCube;
        if (cube && (stand || staging is null || Packing.Of(shared) is null))
        {
            why = $"cube map {texture} has no copy without staging";
            return null;
        }

        if (_copies.Count >= _cap && !Evicted(frame, frame))
        {
            why = $"all {_cap} texture copies are sampled in this frame";
            return null;
        }

        var image = cube
            ? SharedImage.Private(device, (width, height, GlTexture.Levels(texture), Faces), shared, Sampled, out why, true)
            : Made(texture, (width, height, GlTexture.Levels(texture)), shared, stand, out why);
        if (image is null) return null;
        image.Used = frame;
        _copies[texture] = new Copy(image, Math.Max(width, height) <= SmallSide)
        {
            Sampling = GlTexture.Sampling(texture), Stand = stand
        };
        (Version, Shape, Stale) = (Version + 1, Shape + 1, true);
        _dirty++;
        if (!image.Exported) Private++;
        return image;
    }

    private SharedImage? Made(int texture, (int Width, int Height, int Levels) size, SharedFormat format, bool stand,
        out string why)
    {
        _ = Assert(size.Levels is > 0 and <= MaxLevels) && Assert(texture > 0);
        var drawn = _drawn.Contains(texture) && Math.Max(size.Width, size.Height) <= SmallSide;
        if (!stand && staging is not null && !_hot.Contains(texture) && !drawn && Packing.Of(format) is not null)
            return SharedImage.Private(device, (size.Width, size.Height, size.Levels, 1), format, Sampled, out why);
        var attachment = format.Aspect == Vk.AspectDepth ? Vk.DepthAttachment : Vk.ColorAttachment; // the scene draws into it
        return SharedImage.Create(device, size.Width, size.Height, size.Levels, format, Sampled | attachment, out why);
    }

    public bool Dirty(int texture) =>
        Assert(texture >= 0) && Assert(_copies.Count <= MaxTextures) && _copies.TryGetValue(texture, out var copy) &&
        copy.Dirty;

    // A private copy, which a stand cannot use: it goes (Forget) and comes back exported
    public bool Movable(int texture) =>
        Assert(texture >= 0) && Assert(_copies.Count <= MaxTextures) && _copies.TryGetValue(texture, out var copy) &&
        !copy.Image.Exported;

    // Whether a copy made now would be exported (OpenGL blits into it, so it cannot follow OpenGL's signal)
    public bool Exports(int texture, bool stand) =>
        stand || staging is null || !Assert(texture > 0) || !Assert(_copies.Count <= MaxTextures) ||
        (_copies.TryGetValue(texture, out var copy) ? copy.Image.Exported : _hot.Contains(texture) || _drawn.Contains(texture));

    public bool Fits(int texture, int level, (int Format, int Width, int Height) spec)
    {
        if (!Assert(level >= 0) || !_copies.TryGetValue(texture, out var copy)) return false;
        var image = copy.Image;
        var kind = spec.Format is UnsizedRgb or Rgb8 ? SharedFormat.Rgba8 : SharedFormat.Of(spec.Format);
        return level < image.Levels && kind == image.Format && spec.Width == Math.Max(image.Width >> level, 1) &&
               spec.Height == Math.Max(image.Height >> level, 1);
    }

    public SharedImage? Current(int texture) =>
        Assert(texture >= 0) && Assert(_copies.Count <= MaxTextures) && _copies.TryGetValue(texture, out var copy) &&
        !copy.Dirty
            ? copy.Image
            : null;

    public bool Has(int texture) => Assert(texture >= 0) && Assert(_copies.Count <= MaxTextures) &&
                                    _copies.ContainsKey(texture);

    public Samplers.State? SamplingOf(int texture) =>
        Assert(texture >= 0) && Assert(_copies.Count <= MaxTextures) && _copies.TryGetValue(texture, out var copy)
            ? copy.Sampling
            : null;

    // Sampled in the frame, drawn into, or no room to retire it: forgetting it must wait for the open segment's end
    public bool InUse(int texture, long frame) =>
        Assert(frame >= 0) && _copies.TryGetValue(texture, out var copy) &&
        (copy.Image.Used >= frame || copy.Stand || copy.Written || _retired.Count >= MaxTextures - 1);

    // The GL name may come back (another texture: not hot); the copy goes once the last frame that may have sampled it is
    // done on the GPU
    public void Forget(int texture, long frame = long.MaxValue)
    {
        _ = _drawn.Remove(texture);
        _ = _hot.Remove(texture);
        Retire(texture, frame);
    }

    // OpenGL draws into the texture through a framebuffer: its copy is an exported one, which a blit refreshes. A private one
    // goes at once while no segment is open (frame: the one being drawn), else at the next Settle. The mark lasts while the
    // name does (an eviction keeps it; Forget drops it).
    public void DrawnByGl(int texture, long frame)
    {
        if (!Assert(texture > 0)) return;
        if (_drawn.Count >= MaxTextures && !_drawn.Contains(texture)) _drawn.Clear(); // long gone names: private once more
        _ = _drawn.Add(texture);
        if (frame < 0 || !_copies.TryGetValue(texture, out var copy) || copy.Image.Exported || !copy.Small) return;
        Retire(texture, frame);
        Moves++;
        _ = Assert(!_copies.ContainsKey(texture));
    }

    // The copy sampled longest ago goes, retired as of the frame, if that was before the frame before; none Vulkan drew into
    private bool Evicted(long before, long frame)
    {
        if (before < 0 || !Assert(frame >= before) || !Assert(_copies.Count <= MaxTextures)) return false;
        var (oldest, used) = (0, before);
        using var copies = _copies.GetEnumerator(); // no array: a frame may make room for a hundred icons
        for (var i = 0; i < MaxTextures && copies.MoveNext(); i++)
        {
            var (texture, copy) = copies.Current;
            if (copy.Image.Used < used && !copy.Stand && !copy.Written) (oldest, used) = (texture, copy.Image.Used);
        }

        if (oldest == 0) return false;
        _ = _hot.Remove(oldest);
        Retire(oldest, frame);
        Evictions++;
        return Assert(!_copies.ContainsKey(oldest));
    }

    // Between frames, near the cap: copies unsampled a while go, so the frame's new ones need not make room as it draws
    private void Trim(long frame)
    {
        _ = Assert(frame >= 0) && Assert(_cap <= MaxTextures);
        for (var i = 0; i < MaxTextures && _copies.Count > _cap - _cap / 8; i++)
            if (!Evicted(frame - IdleFrames, frame))
                return;
    }

    private void Retire(int texture, long frame)
    {
        if (!Assert(texture >= 0) || !_copies.Remove(texture, out var copy)) return;
        (Version, Shape) = (Version + 1, Shape + 1);
        if (copy.Dirty) _dirty--;
        if (copy.Written) _written--;
        if (!copy.Image.Exported) Private--;
        Unfilled(texture, frame == long.MaxValue ? 0 : frame);
        if (frame == long.MaxValue || _retired.Count >= MaxTextures)
        {
            _ = device.WaitIdle(); // a frame in flight may still sample it
            copy.Image.Dispose();
        }
        else _retired.Add((copy.Image, frame));

        _ = Assert(!_copies.ContainsKey(texture));
    }

    // A fill that will not be copied: its staging comes back with the frame
    private void Unfilled(int texture, long frame)
    {
        _ = Assert(_fills.Count <= MaxFills) && Assert(frame >= 0);
        var kept = 0;
        for (var i = 0; i < Math.Min(_fills.Count, MaxFills); i++)
            if (_fills[i].Texture != texture) _fills[kept++] = _fills[i];
            else if (!_fills[i].Host) staging?.Used(_fills[i].Staged.Ticket, frame);
        _fills.RemoveRange(kept, _fills.Count - kept);
    }

    private readonly List<(SharedImage Image, long Frame)> _retired = [];

    public void Collect(long done)
    {
        if (!Assert(_retired.Count <= MaxTextures) || _retired.Count == 0) return;
        var kept = 0;
        for (var i = 0; i < Math.Min(_retired.Count, MaxTextures); i++)
        {
            if (_retired[i].Frame <= done) _retired[i].Image.Dispose();
            else _retired[kept++] = _retired[i];
        }

        _retired.RemoveRange(kept, _retired.Count - kept);
        _ = Assert(kept <= MaxTextures);
    }

    // A parameter Samplers cannot say the effect of counts as a change: the copy is made again, the sampling read anew
    public void Tuned(int texture, uint name, float value)
    {
        if (!Assert(texture >= 0) || !_copies.TryGetValue(texture, out var copy)) return;
        if (name == MaxLevelName) copy.MaxLevel = (int)value;
        if (name is MaxLevelName or MinLod or MaxLod or LodBias) return; // the levels hold what they held; Samplers reads none
        if (name == BaseLevelName) copy.BaseLevel = (int)value;
        if (Samplers.Tuned(copy.Sampling, name, value) is not { } tuned)
        {
            copy.Resample = true;
            Changed(texture);
            return;
        }

        if (tuned == copy.Sampling) return;
        (copy.Sampling, Version, Shape) = (tuned, Version + 1, Shape + 1);
    }

    public void Changed(int texture)
    {
        if (!Assert(texture >= 0) || !Assert(_copies.Count <= MaxTextures) || !_copies.TryGetValue(texture, out var copy))
            return;
        if (!copy.Dirty) _dirty++;
        (copy.Dirty, Version, Stale) = (true, Version + 1, true);
    }

    // Some copy may have changed since the last Refresh
    public bool Stale { get; private set; }

    // Bumps when a copy is made or let go or its sampling changes, not when one only went dirty
    public long Shape { get; private set; }

    public bool AnyDirty => _dirty > 0;

    private int _dirty;

    // On OpenGL's timeline, before a handoff: every copy that changed (and the small ones, not Trusting). A private copy whose
    // levels found no staging stays dirty.
    public void Refresh(long frame)
    {
        _ = Assert(_copies.Count <= MaxTextures) && Assert(frame >= 0);
        var small = frame != _refreshed && !Trusting;
        (_refreshed, Stale) = (frame, false);
        using var copies = _copies.GetEnumerator(); // every frame: no array; nothing here adds or removes a copy
        for (var i = 0; i < MaxTextures && copies.MoveNext(); i++)
        {
            var (texture, copy) = copies.Current;
            if ((!copy.Dirty && !(copy.Small && small)) || copy.Written) continue; // one Vulkan drew into is ahead
            _ = Renewed(texture, copy, frame, out _);
        }

        _packing.Restore();
    }

    // Only this copy: one segment is open, which may have sampled the others as they are
    public bool Refresh(int texture, long frame, out string why)
    {
        why = $"texture {texture} has no copy";
        if (!Assert(frame >= 0) || !_copies.TryGetValue(texture, out var copy)) return false;
        if (copy.Written || !copy.Dirty) return Assert(texture > 0);
        var renewed = Renewed(texture, copy, frame, out why);
        _packing.Restore();
        return renewed;
    }

    private bool Renewed(int texture, Copy copy, long frame, out string why)
    {
        why = "";
        var image = copy.Image;
        if (image.Exported)
            for (var level = 0; level < Math.Min(image.Levels, MaxLevels); level++) Blit(texture, image, level, false);
        else if (!Packed(texture, copy, frame, out why)) return false;
        (copy.Recent, copy.Refreshed) = (Shifted(copy, frame) | 1, frame);
        if (copy.Dirty) _dirty--;
        copy.Dirty = false;
        if (!copy.Resample) return Assert(_dirty >= 0);
        (copy.Sampling, copy.Resample, Version, Shape) = (GlTexture.Sampling(texture), false, Version + 1, Shape + 1);
        return Assert(_dirty >= 0);
    }

    // Every level into one staging range, rows as GL_PACK_ALIGNMENT lays them out
    private bool Packed(int texture, Copy copy, long frame, out string why)
    {
        why = "";
        var image = copy.Image;
        if (staging is null || Packing.Of(image.Format) is not { } pack || !Assert(frame >= 0)) return false;
        var (levels, bytes, layers) = (new Vk.BufferImageCopy[image.Levels], 0UL, image.Layers);
        var alignment = (ulong)_packing.Alignment;
        for (var level = 0; level < Math.Min(levels.Length, MaxLevels); level++)
        {
            var (width, height) = (Math.Max(image.Width >> level, 1), Math.Max(image.Height >> level, 1));
            var row = ((ulong)(width * pack.Bytes) + alignment - 1) / alignment * alignment;
            levels[level] = new Vk.BufferImageCopy
            {
                Offset = bytes, RowLength = (uint)(row / (ulong)pack.Bytes), Aspect = image.Format.Aspect, Level = (uint)level,
                Layers = (uint)layers, Width = (uint)width, Height = (uint)height, Depth = 1
            };
            bytes += (row * (ulong)height * (ulong)layers + 15) / 16 * 16;
        }

        if (staging.Take(bytes, long.MaxValue, out why) is not { } staged) return false;
        var through = Through(image, frame);
        for (var level = 0; level < Math.Min(levels.Length, MaxLevels); level++)
        {
            for (var face = 0; face < Math.Min(layers, Faces); face++)
                _copier.Copy(texture, through, level, ((int)levels[level].Width, (int)levels[level].Height),
                    image.Format.Aspect == Vk.AspectDepth, image.Cube ? face : -1);
        }
        _packing.Into(staged.Gl);
        for (var level = 0; level < Math.Min(levels.Length, MaxLevels); level++)
        {
            var (width, height) = ((int)levels[level].Width, (int)levels[level].Height);
            var size = (int)(levels[level].RowLength * (ulong)pack.Bytes * (ulong)height * (ulong)layers);
            var into = new IntPtr((long)(staged.At + levels[level].Offset));
            if (image.Cube) GL.GetTextureSubImage(through, level, 0, 0, 0, width, height, layers, pack.Format, pack.Type, size, into);
            else GL.GetTextureImage(through, level, pack.Format, pack.Type, size, into);
            levels[level].Offset += staged.At;
        }

        if ((long)bytes > ThroughBytes) Dropped(Key(image)); // OpenGL keeps it until its pack is done
        Unfilled(texture, frame); // an older fill is overtaken
        _fills.Add(new Fill(texture, image, staged, levels));
        (Packs, PackedBytes) = (Packs + levels.Length, PackedBytes + (long)bytes);
        Tally(image, (long)bytes);
        return Assert(_fills.Count <= MaxFills);
    }

    // For the report: the kinds of texture packed most, by size (16 bits each), format (16) and levels (8)
    private readonly Dictionary<ulong, (long Packs, long Bytes)> _packed = [];

    private void Tally(SharedImage image, long bytes)
    {
        var kind = (ulong)(ushort)image.Width << 40 | (ulong)(ushort)image.Height << 24 | (image.Format.Vulkan & 0xFFFF) << 8 |
                   (byte)image.Levels;
        if (_packed.Count >= MaxTextures && !_packed.ContainsKey(kind)) return;
        ref var tally = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_packed, kind, out _);
        tally = (tally.Packs + 1, tally.Bytes + bytes);
        _ = Assert(bytes >= 0);
    }

    public string MostPacked()
    {
        if (_packed.Count == 0 || !Assert(_packed.Count <= MaxTextures)) return "";
        var most = _packed.OrderByDescending(p => p.Value.Bytes).Take(4)
            .Select(p => $"{(p.Key >> 40) & 0xFFFF}x{(p.Key >> 24) & 0xFFFF} {Formats((uint)(p.Key >> 8) & 0xFFFF)} " +
                         $"{p.Key & 0xFF} levels {p.Value.Packs}x {p.Value.Bytes >> 20} MB");
        return $" (most: {string.Join(", ", most)})";
    }

    private static string Formats(uint format) => format switch
    {
        Vk.FormatRgba8 => "RGBA8", Vk.FormatRgba16F => "RGBA16F", Vk.FormatD32F => "depth", _ => $"format {format}"
    };

    private static (int, int, int, int, int) Key(SharedImage image) =>
        Assert(image.Levels > 0) && Assert(image.Format.Gl > 0)
            ? (image.Format.Gl, image.Width, image.Height, image.Levels, image.Layers)
            : default;

    private int Through(SharedImage image, long frame)
    {
        var key = Key(image);
        if (_through.TryGetValue(key, out var known))
        {
            _through[key] = (known.Texture, frame);
            return known.Texture;
        }

        if (_through.Count >= MaxThrough) Dropped(_through.MinBy(t => t.Value.Used).Key);
        GL.CreateTextures(image.Cube ? TextureTarget.TextureCubeMap : TextureTarget.Texture2D, 1, out int made);
        GL.TextureStorage2D(made, image.Levels, (SizedInternalFormat)image.Format.Gl, image.Width, image.Height);
        _through[key] = (made, frame);
        _ = Assert(_through.Count <= MaxThrough) && Assert(made > 0);
        return made;
    }

    private void Dropped((int, int, int, int, int) key)
    {
        if (!_through.Remove(key, out var through) || !Assert(through.Texture > 0)) return;
        GL.DeleteTexture(through.Texture);
        _ = Assert(!_through.ContainsKey(key));
    }

    public bool Filling => _fills.Count > 0;

    // An upload the CPU staged in the frame's host memory while no segment was open, into a private copy that holds its
    // contents and waits for no other fill: copied in the next segment's setup. Whole (Overwritable): it replaces every texel
    // the copy has, so the copy may be stale, unfilled or waiting for a fill, which it overtakes; the copy is current then.
    // mipmaps: the levels made from it after (Mipmaps.Record). False when it cannot be (OpenGL copies then).
    public bool Queue(int texture, (ulong Buffer, long At, int Bytes) staged, Vk.BufferImageCopy region, long frame,
        bool whole = false, int mipmaps = 0)
    {
        if (!_copies.TryGetValue(texture, out var copy) || copy.Image.Exported || _fills.Count >= MaxFills ||
            !Assert(staged.At >= 0) || (whole ? copy.Resample || copy.Written : !copy.Filled || copy.Dirty)) return false;
        for (var i = 0; !whole && i < Math.Min(_fills.Count, MaxFills); i++)
            if (_fills[i].Texture == texture)
                return false;
        if (whole) Unfilled(texture, frame);
        var range = new GlStaging.Range(staged.Buffer, 0, (ulong)staged.At, -1);
        _fills.Add(new Fill(texture, copy.Image, range, [region]) { Frame = frame, Mipmaps = mipmaps, Bytes = staged.Bytes });
        if (whole) Overwritten(texture);
        return Assert(region.Offset == (ulong)staged.At);
    }

    // The copy's image when an upload of all of level 0 replaces every texel it has (one level, its sampling known, nothing of
    // Vulkan's to write back), stale or not
    public SharedImage? Overwritable(int texture) =>
        Assert(texture >= 0) && _copies.TryGetValue(texture, out var copy) && copy.Image.Levels == 1 && !copy.Resample &&
        !copy.Written
            ? copy.Image
            : null;

    // All its texels uploaded: current again
    public void Overwritten(int texture)
    {
        if (!Assert(texture >= 0) || !_copies.TryGetValue(texture, out var copy) || !copy.Dirty) return;
        (copy.Dirty, _dirty) = (false, _dirty - 1);
        _ = Assert(_dirty >= 0);
    }

    // A fill OpenGL packed waits: the segment it goes into must follow OpenGL's signal (VulkanFrame.Late); one from host
    // memory needs none
    public bool PacksWaiting
    {
        get
        {
            foreach (var fill in _fills.Bounded(MaxFills))
                if (!fill.Host)
                    return true;
            return false;
        }
    }

    // The levels glGenerateMipmap makes in the copy, base included: 0 when it makes them from another base, 1 when the copy
    // has none of them (it holds what it held: OpenGL's base is the same)
    public int Made(int texture) =>
        Assert(texture >= 0) && _copies.TryGetValue(texture, out var copy) && copy.BaseLevel == 0
            ? Math.Clamp(copy.MaxLevel, 0, copy.Image.Levels - 1) + 1
            : 0;

    // glGenerateMipmap on a private copy while no segment is open: the levels are made in the next segment's setup, after the
    // fill waiting for it (its base, from host memory), or on their own. False when the copy is not current.
    public bool Mipmapped(int texture, int levels, long frame)
    {
        if (!_copies.TryGetValue(texture, out var copy) || copy.Image.Exported || copy.Dirty ||
            !Assert(levels is > 1 and <= MaxLevels)) return false;
        for (var i = 0; i < Math.Min(_fills.Count, MaxFills); i++)
            if (_fills[i].Texture == texture)
            {
                _fills[i] = _fills[i] with { Mipmaps = levels };
                return true;
            }

        if (!copy.Filled || _fills.Count >= MaxFills) return false;
        _fills.Add(new Fill(texture, copy.Image, new GlStaging.Range(0, 0, 0, -1), [])
        {
            Frame = long.MaxValue, Mipmaps = levels // nothing in host memory: never expires
        });
        return Assert(frame >= 0);
    }

    // Uploads staged in host memory that a frame begun since took back: OpenGL copies those textures instead
    public void Expire(long done)
    {
        var kept = 0;
        for (var i = 0; i < Math.Min(_fills.Count, MaxFills); i++)
            if (_fills[i].Host && _fills[i].Frame <= done) Changed(_fills[i].Texture);
            else _fills[kept++] = _fills[i];
        _fills.RemoveRange(kept, _fills.Count - kept);
        _ = Assert(kept <= MaxFills);
    }

    // Into the setup of a segment that opened after OpenGL packed them (or that OpenGL signals late, VulkanFrame.Late): every
    // image wholly overwritten - from undefined the first time, else from the layout the frame keeps it in at rest - then left
    // at rest again. One barrier before all the copies and one after.
    public void Flush(IntPtr setup, long frame, Restaging? restage = null)
    {
        if (restage is not null) Restaged(frame, restage);
        var count = Math.Min(_fills.Count, MaxFills);
        if (count == 0 || !Assert(setup != IntPtr.Zero) || !NotNull(staging)) return;
        Span<Vk.ImageBarrier> barriers = count <= 64 ? stackalloc Vk.ImageBarrier[count] : new Vk.ImageBarrier[count];
        for (var i = 0; i < Math.Min(count, MaxFills); i++)
        {
            var fresh = _copies.TryGetValue(_fills[i].Texture, out var copy) && !copy.Filled;
            var from = fresh ? Vk.LayoutUndefined : Vk.LayoutShaderRead;
            barriers[i] = Barrier(_fills[i].Image, (from, Vk.LayoutTransferDst));
            if (copy is not null) copy.Filled = true;
        }

        fixed (Vk.ImageBarrier* before = barriers)
            VkApi.CmdPipelineBarrier(setup, Vk.StageAll, Vk.StageTransfer, 0, 0, null, 0, null, (uint)count, before);
        for (var i = 0; i < Math.Min(count, MaxFills); i++)
        {
            var fill = _fills[i];
            fixed (Vk.BufferImageCopy* levels = fill.Levels)
                if (fill.Levels.Length > 0)
                    VkApi.CmdCopyBufferToImage(setup, fill.Staged.Buffer, fill.Image.Image, Vk.LayoutTransferDst,
                        (uint)fill.Levels.Length, levels);
            if (fill.Mipmaps > 1) Vulkan.Mipmaps.Record(setup, fill.Image, fill.Mipmaps);
            barriers[i] = Barrier(fill.Image, (Vk.LayoutTransferDst, Vk.LayoutShaderRead));
            if (!fill.Host) staging.Used(fill.Staged.Ticket, frame);
        }

        fixed (Vk.ImageBarrier* after = barriers)
            VkApi.CmdPipelineBarrier(setup, Vk.StageTransfer, Vk.StageAll, 0, 0, null, 0, null, (uint)count, after);
        Fills += count;
        _fills.Clear();
    }

    // Moves bytes of an earlier frame's host memory into the frame's own: the new place, or null without room
    public delegate (ulong Buffer, long At)? Restaging((ulong Buffer, long At, int Bytes) from);

    // An earlier frame's host memory comes back to the CPU as its slot is begun again, while the segments of the frames after
    // it may still run on the GPU: a host fill flushed in a later frame copies from the frame's own memory instead. One that
    // finds no room there goes, its copy changed (OpenGL copies it).
    private void Restaged(long frame, Restaging restage)
    {
        var kept = 0;
        for (var i = 0; i < Math.Min(_fills.Count, MaxFills); i++)
        {
            var fill = _fills[i];
            if (fill.Host && fill.Bytes > 0 && fill.Frame != frame && Assert(fill.Levels.Length == 1))
            {
                if (restage((fill.Staged.Buffer, (long)fill.Staged.At, fill.Bytes)) is not var (buffer, at))
                {
                    Changed(fill.Texture);
                    continue;
                }

                fill = fill with
                {
                    Staged = new GlStaging.Range(buffer, 0, (ulong)at, -1), Levels = [fill.Levels[0] with { Offset = (ulong)at }],
                    Frame = frame
                };
            }

            _fills[kept++] = fill;
        }

        _fills.RemoveRange(kept, _fills.Count - kept);
    }

    private static Vk.ImageBarrier Barrier(SharedImage image, (int From, int To) layouts)
    {
        const uint all = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;
        _ = NotNull(image) && Assert(layouts.To != layouts.From);
        return new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = all, DstAccess = all, OldLayout = layouts.From,
            NewLayout = layouts.To, SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = image.Image,
            Range = new Vk.ColorRange { Aspect = image.Format.Aspect, Levels = (uint)image.Levels, Layers = (uint)image.Layers }
        };
    }

    // Its refreshes as of the frame: bit n, n frames before
    private static ulong Shifted(Copy copy, long frame) =>
        frame - copy.Refreshed is var gap and >= 0 and < 64 && Assert(copy.Refreshed >= -1) && NotNull(copy)
            ? copy.Recent << (int)gap
            : 0;

    // Between frames, no segment open: a private copy refreshed in HotFrames of the last 16 frames goes, to come back exported
    // (refreshed that often, one blit costs less than a pack and a copy, and the exported image a submission a microsecond); an
    // exported one not refreshed for ColdFrames goes, to come back private. The number moved.
    public int Settle(long frame)
    {
        if (!Assert(frame >= 0) || !Assert(_copies.Count <= MaxTextures)) return 0;
        Trim(frame);
        if (staging is null) return 0;
        _moving.Clear();
        using var copies = _copies.GetEnumerator(); // every frame: no array
        for (var i = 0; i < MaxTextures && copies.MoveNext(); i++)
        {
            var (texture, copy) = copies.Current;
            var drawn = copy.Small && _drawn.Contains(texture);
            var hot = !copy.Image.Exported && !copy.Image.Cube &&
                      (drawn || BitOperations.PopCount(Shifted(copy, frame) & HotWindow) >= HotFrames);
            var cold = copy.Image.Exported && !copy.Stand && !copy.Written && frame - copy.Refreshed > ColdFrames &&
                       _hot.Contains(texture) && !drawn;
            if (!hot && !cold) continue;
            if (hot) _ = _hot.Add(texture);
            else _ = _hot.Remove(texture);
            _moving.Add(texture);
        }

        foreach (var texture in _moving.Bounded(MaxTextures)) Retire(texture, frame); // hot or cold as it now is
        Moves += _moving.Count;
        if (_through.Count > 0) Unused(frame);
        return _moving.Count;
    }

    private void Unused(long frame)
    {
        _ = Assert(_through.Count <= MaxThrough) && Assert(frame >= 0);
        _unused.Clear();
        using var through = _through.GetEnumerator(); // every frame: no array
        for (var i = 0; i < MaxThrough && through.MoveNext(); i++)
            if (frame - through.Current.Value.Used > ThroughFrames)
                _unused.Add(through.Current.Key);
        foreach (var key in _unused.Bounded(MaxThrough)) Dropped(key);
    }

    public void Wrote(int texture)
    {
        if (!Assert(texture > 0) || !Assert(_copies.Count <= MaxTextures) || !_copies.TryGetValue(texture, out var copy))
            return;
        if (!copy.Written) _written++;
        if (copy.Dirty) _dirty--;
        (copy.Written, copy.Dirty) = (true, false);
    }

    public bool AnyWritten => _written > 0;

    private int _written;

    public bool Written(int texture) =>
        Assert(texture >= 0) && Assert(_copies.Count <= MaxTextures) && _copies.TryGetValue(texture, out var copy) &&
        copy.Written;

    // On OpenGL's timeline, no segment open
    public void WriteBack(int texture)
    {
        if (!Assert(texture > 0) || !_copies.TryGetValue(texture, out var copy) || !copy.Written) return;
        Blit(texture, copy.Image, 0, true);
        copy.Written = false;
        _written--;
        _ = Assert(!copy.Written);
    }

    private void Blit(int texture, SharedImage image, int level, bool back)
    {
        if (!NotNull(image) || !Assert(image.Exported)) return;
        var (from, to) = back ? (image.Texture, texture) : (texture, image.Texture);
        _copier.Copy(from, to, level, (Math.Max(image.Width >> level, 1), Math.Max(image.Height >> level, 1)),
            image.Format.Aspect == Vk.AspectDepth);
        _ = Assert(_copies.Count <= MaxTextures);
    }

    public void Dispose()
    {
        _ = Assert(_copies.Count <= MaxTextures) && device.WaitIdle();
        foreach (var fill in _fills.Bounded(MaxFills))
            if (!fill.Host)
                staging?.Used(fill.Staged.Ticket, 0);
        _fills.Clear();
        foreach (var copy in _copies.Values.Bounded(MaxTextures)) copy.Image.Dispose();
        foreach (var (image, _) in _retired.Bounded(MaxTextures)) image.Dispose();
        _copies.Clear();
        (_written, _dirty, Private) = (0, 0, 0);
        _retired.Clear();
        foreach (var key in _through.Keys.ToArray().Bounded(MaxThrough)) Dropped(key);
        _copier.Dispose();
    }

    // The engine's GL_PACK_* and pack buffer while Komet packs, put back as they were (Restore); rows packed with the engine's
    // alignment, no row length or skips
    private sealed class Packing
    {
        private ((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer)? _saved;

        public int Alignment => (_saved ??= Saved()).Store.Alignment is var a and (1 or 2 or 4 or 8) ? a : 4;

        // GL_RGBA/GL_UNSIGNED_BYTE and the like: what glGetTextureImage gives exactly as the image holds it
        public static (PixelFormat Format, PixelType Type, int Bytes)? Of(SharedFormat format) => format.Vulkan switch
        {
            Vk.FormatRgba8 => (PixelFormat.Rgba, PixelType.UnsignedByte, 4),
            Vk.FormatRgba16F => (PixelFormat.Rgba, PixelType.HalfFloat, 8),
            Vk.FormatD32F => (PixelFormat.DepthComponent, PixelType.Float, 4),
            Vk.FormatR16F => (PixelFormat.Red, PixelType.HalfFloat, 2),
            Vk.FormatRgba32F => (PixelFormat.Rgba, PixelType.Float, 16),
            Vk.FormatR8 => (PixelFormat.Red, PixelType.UnsignedByte, 1),
            Vk.FormatRg16F => (PixelFormat.Rg, PixelType.HalfFloat, 4),
            Vk.FormatR32F => (PixelFormat.Red, PixelType.Float, 4),
            Vk.FormatRgba16 => (PixelFormat.Rgba, PixelType.UnsignedShort, 8),
            _ => null
        };

        private static ((int RowLength, int SkipRows, int SkipPixels, int Alignment) Store, uint Buffer) Saved()
        {
            var saved = GlTap.Pack ?? (GlTap.Packing(), (uint)GL.GetInteger((GetPName)0x88ED));
            var (rows, skipRows, skipPixels, _) = saved.Store;
            if (rows != 0) GL.PixelStore(PixelStoreParameter.PackRowLength, 0);
            if (skipRows != 0) GL.PixelStore(PixelStoreParameter.PackSkipRows, 0);
            if (skipPixels != 0) GL.PixelStore(PixelStoreParameter.PackSkipPixels, 0);
            _ = Assert(rows >= 0) && Assert(skipRows >= 0);
            return saved;
        }

        public void Into(int buffer)
        {
            _saved ??= Saved();
            if (buffer == _bound || !Assert(buffer > 0)) return;
            GL.BindBuffer(BufferTarget.PixelPackBuffer, buffer);
            _bound = buffer;
            _ = Assert(_saved is not null);
        }

        private int _bound;

        public void Restore()
        {
            _bound = 0;
            if (_saved is not var (store, buffer)) return;
            GL.BindBuffer(BufferTarget.PixelPackBuffer, buffer);
            if (store.RowLength != 0) GL.PixelStore(PixelStoreParameter.PackRowLength, store.RowLength);
            if (store.SkipRows != 0) GL.PixelStore(PixelStoreParameter.PackSkipRows, store.SkipRows);
            if (store.SkipPixels != 0) GL.PixelStore(PixelStoreParameter.PackSkipPixels, store.SkipPixels);
            _saved = null;
            _ = Assert(store.Alignment > 0) && Assert(buffer < int.MaxValue);
        }
    }
}
