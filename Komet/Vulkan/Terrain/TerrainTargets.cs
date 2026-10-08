using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Each attachment texture is replaced by a new texture on a SharedImage, attached in its place in every framebuffer object
// the engine has. Moving a texture onto shared memory in place (Adopt, name kept) is only for what no one else names: done
// to the engine's framebuffers as a world started, it left the final image black. When Vulkan stops, the framebuffers keep
// the shared textures as plain OpenGL textures.
internal sealed class TerrainTargets(VulkanDevice device) : IDisposable
{
    public const int Primary = 0, Transparent = 1, LiquidDepth = 5, ShadowFar = 11, ShadowNear = 12;
    private const uint GlColor = 0x958E, GlDepth = 0x958F, GlShaderRead = 0x9591;
    private const int MaxImages = 80, MaxColors = 8;
    private const uint Usage = Vk.Sampled | Vk.TransferSrc | Vk.TransferDst;

    private readonly Dictionary<int, SharedImage> _images = [];
    private readonly GlTexture.Copier _copier = new();

    // Textures whose adoption failed, with the reason: not tried again until the engine deletes or respecifies them (Retry).
    // Each try copies the texture with all its levels first; the engine draws its item icons into the atlas thousands of times
    // while a world loads, and a try per draw filled VRAM and GTT with those copies until the system ran out of memory.
    private readonly Dictionary<int, string> _refused = [];
    private const int MaxRefused = 1024, MaxTries = 8;

    // Adopt's reason when the frame's tries are used up: tried again next frame, so nothing to log
    public const string Waits = "more than 8 textures to share in one frame; the rest wait for the next";
    private int _tries; // adoptions tried this frame: a texture respecified every frame (Retry) cannot loop them

    public void NewFrame()
    {
        _ = Assert(_tries is >= 0 and <= MaxTries) && Assert(_refused.Count <= MaxRefused);
        _tries = 0;
    }

    private readonly Dictionary<int, int> _resting = [];
    private readonly Dictionary<int, Samplers.State> _sampling = [];
    private readonly List<(FrameBufferRef Framebuffer, int Colors)> _swapped = [];
    private IReadOnlyList<FrameBufferRef> _all = [];

    public IReadOnlyList<FrameBufferRef> Framebuffers
    {
        get => _all;
        set => _all = NotNull(value) && Assert(value.Count < 64) ? value : [];
    }

    public long Version { get; private set; }

    public List<string> Adoptions { get; } = [];

    public IEnumerable<SharedImage> Images => _images.Values;

    public IEnumerable<VulkanFrame.Shared> Resting => _images.Values.Select(i =>
    {
        var layout = _resting.GetValueOrDefault(i.Texture, Vk.LayoutShaderRead);
        var gl = layout switch
        {
            Vk.LayoutColorAttachment => GlColor,
            Vk.LayoutDepthAttachment => GlDepth,
            _ => GlShaderRead
        };
        return new VulkanFrame.Shared(i, layout, gl);
    });

    public FrameBufferRef? Of(int fbo)
    {
        if (!Assert(fbo >= 0) || !Assert(_all.Count < 64) || fbo == 0) return null;
        for (var i = 0; i < Math.Min(_all.Count, 64); i++) // asked for every draw: no closure
            if (_all[i]?.FboId == fbo)
                return _all[i];
        return null;
    }

    public bool Swapped(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer) || !Assert(_swapped.Count <= MaxImages)) return false;
        foreach (var (swapped, _) in _swapped.Bounded(MaxImages))
            if (ReferenceEquals(swapped, framebuffer))
                return true;
        return false;
    }

    public SharedImage? Find(int texture) => Assert(texture >= 0) ? _images.GetValueOrDefault(texture) : null;

    public Samplers.State? Sampling(int texture) =>
        Assert(texture >= 0) && _sampling.TryGetValue(texture, out var s) ? s : null;

    // OpenGL's work: no segment may be open. budgeted: counted against the frame's tries; the engine's framebuffers at the
    // frame's start are not, they all go onto shared images before anything draws
    public SharedImage? Adopt(int texture, out string why, bool budgeted = true)
    {
        why = "";
        if (!Assert(texture > 0)) return null;
        if (_images.TryGetValue(texture, out var known)) return known;
        if (_refused.TryGetValue(texture, out var refusal))
        {
            why = refusal;
            return null;
        }

        if (budgeted && (!Assert(_tries <= MaxTries) || _tries >= MaxTries))
        {
            why = Waits;
            return null;
        }

        if (budgeted) _tries++;
        var adopted = Adopting(texture, out why);
        if (adopted is not null) return adopted;
        if (_refused.Count >= MaxRefused) _refused.Clear();
        _refused[texture] = why.Length > 0 ? why : "no shared image for it";
        why = _refused[texture];
        return null;
    }

    // The engine deleted or respecified the texture: a failed adoption may succeed now
    public void Retry(int texture)
    {
        if (Assert(texture >= 0) && Assert(_refused.Count <= MaxRefused)) _ = _refused.Remove(texture);
    }

    private SharedImage? Adopting(int texture, out string why)
    {
        if (!Assert(texture > 0) || _images.Count >= MaxImages)
        {
            why = $"more than {MaxImages} shared images";
            return null;
        }

        var gl = GlTexture.Of(texture);
        if (gl.Width <= 0 || gl.Height <= 0)
        {
            why = $"texture {texture} has no size";
            return null;
        }

        var rgb = gl.Format is 0x1907 or 0x8051; // RGB, RGB8: RGBA8 whose alpha reads 1 on both sides
        if ((rgb ? SharedFormat.Rgba8 : SharedFormat.Of(gl.Format)) is not { } format)
        {
            why = $"texture {texture}'s format 0x{gl.Format:X} has no shared kind";
            return null;
        }

        var layers = gl.Kind == GlTexture.Array2D ? Math.Max(gl.Depth, 1) : 1;
        var depth = format.Aspect == Vk.AspectDepth;
        var levels = depth || layers > 1 ? 1 : gl.LevelCount;
        var note = $"texture {texture} {gl.Width}x{gl.Height}, {levels} of {gl.LevelCount} levels, {layers} layers, " +
                   $"format 0x{gl.Format:X}, target 0x{gl.Kind:X}";
        VulkanWatch.Mark("adopting " + note);
        if (Adoptions.Count < 64) Adoptions.Add(note);
        var usage = Usage | (depth ? Vk.DepthAttachment : Vk.ColorAttachment);
        // The contents come along through a scratch copy; not a depth's (blitting depth wants one format on both sides, and the
        // engine clears its depths every frame) nor an array's (the transparent pass clears its layers every frame)
        var scratch = layers == 1 && !depth ? Scratch(texture, gl, levels, format) : 0;
        var shared = SharedImage.Adopt(device, texture, (gl.Width, gl.Height, levels, layers), format, usage,
            out why);
        if (scratch != 0)
        {
            for (var l = 0; l < Math.Min(levels, 16); l++)
                if (shared is not null)
                    _copier.Copy(scratch, texture, l, (gl.Width >> l, gl.Height >> l), depth);
            GL.DeleteTexture(scratch);
        }

        if (shared is null) return null;
        if (rgb)
        {
            GL.TextureParameter(texture, TextureParameterName.TextureSwizzleA, (int)All.One);
            shared.OpaqueAlpha = true;
        }

        (_images[texture], Version) = (shared, Version + 1);
        _resting[texture] = depth ? Vk.LayoutDepthAttachment : Vk.LayoutColorAttachment;
        var sampling = GlTexture.Sampling(texture);
        _sampling[texture] = levels == 1 ? sampling with { Mipmaps = 0 } : sampling;
        return shared;
    }

    private int Scratch(int texture, GlTexture.Description gl, int levels, SharedFormat format)
    {
        if (!Assert(texture > 0) || !Assert(gl.Width > 0 && levels > 0)) return 0;
        var depth = format.Aspect == Vk.AspectDepth;
        GL.CreateTextures(TextureTarget.Texture2D, 1, out int scratch);
        GL.TextureStorage2D(scratch, levels, (SizedInternalFormat)(depth ? 0x8CAC : format.Gl), gl.Width, gl.Height);
        for (var l = 0; l < Math.Min(levels, 16); l++) _copier.Copy(texture, scratch, l, (gl.Width >> l, gl.Height >> l), depth);
        return scratch;
    }

    public void Forget(int texture)
    {
        if (!Assert(texture >= 0) || !_images.ContainsKey(texture)) return;
        _ = _swapped.RemoveAll(s => (s.Framebuffer.ColorTextureIds ?? []).Contains(texture) ||
                                    s.Framebuffer.DepthTextureId == texture);
        _ = Free(texture, true);
    }

    public bool Swap(FrameBufferRef framebuffer, out string why)
    {
        why = "";
        if (!NotNull(framebuffer) || !Assert(framebuffer.FboId > 0)) return false;
        var colors = framebuffer.ColorTextureIds ?? [];
        if (!Assert(colors.Length <= MaxColors) || _images.Count + colors.Length + 1 > MaxImages) return false;
        if (_swapped.Exists(s => ReferenceEquals(s.Framebuffer, framebuffer))) return true;
        for (var i = 0; i < Math.Min(colors.Length, MaxColors); i++)
        {
            var shared = Swapped(framebuffer, colors[i], FramebufferAttachment.ColorAttachment0 + i, out why);
            if (shared is null) return false;
            colors[i] = shared.Texture;
        }

        if (framebuffer.DepthTextureId > 0)
        {
            var depth = Swapped(framebuffer, framebuffer.DepthTextureId, FramebufferAttachment.DepthAttachment,
                out why);
            if (depth is null) return false;
            framebuffer.DepthTextureId = depth.Texture;
            if (colors.Length == 0) _resting[depth.Texture] = Vk.LayoutShaderRead; // a shadow map: sampled more than drawn
        }

        _swapped.Add((framebuffer, colors.Length));
        return true;
    }

    private SharedImage? Swapped(FrameBufferRef framebuffer, int texture, FramebufferAttachment attachment,
        out string why)
    {
        why = "";
        if (!Assert(texture > 0) || !NotNull(framebuffer) || !Assert(attachment >= FramebufferAttachment.ColorAttachment0 ||
                                                                      attachment == FramebufferAttachment.DepthAttachment))
            return null;
        return _images.GetValueOrDefault(texture) ?? Replace(texture, out why);
    }

    // Who else names a texture the engine made (the transparent pass's reveal and accumulation layers, in statics): Owned says
    // whether it can be told of a new name, Renamed tells it
    public System.Func<int, bool>? Owned { get; set; }
    public Action<int, int>? Renamed { get; set; }

    public SharedImage? Share(int texture, out string why)
    {
        why = "";
        if (!Assert(texture > 0)) return null;
        if (_images.TryGetValue(texture, out var known)) return known;
        if (Owned?.Invoke(texture) != true)
        {
            why = $"texture {texture} belongs to no framebuffer and no owner Komet knows";
            return null;
        }

        var shared = Replace(texture, out why);
        if (shared is not null) Renamed?.Invoke(texture, shared.Texture);
        return shared;
    }

    // RGB becomes RGBA with alpha reading 1. The contents are not carried over: the engine clears what it draws into every
    // frame.
    private SharedImage? Replace(int texture, out string why)
    {
        var gl = GlTexture.Of(texture);
        var rgb = gl.Format is 0x1907 or 0x8051;
        if (!Assert(texture > 0) || gl.Width <= 0 || (rgb ? SharedFormat.Rgba8 : SharedFormat.Of(gl.Format)) is not { } f)
        {
            why = $"texture {texture} (format 0x{gl.Format:X}) has no shared kind";
            return null;
        }

        var layers = gl.Kind == GlTexture.Array2D ? Math.Max(gl.Depth, 1) : 1;
        _ = Assert(layers >= 1) && Assert(gl.Height > 0);
        var usage = Usage | (f.Aspect == Vk.AspectDepth ? Vk.DepthAttachment : Vk.ColorAttachment);
        var shared = SharedImage.Create(device, (gl.Width, gl.Height, 1, layers), f, usage, out why);
        if (shared is null) return null;
        if (Adoptions.Count < 64)
            Adoptions.Add($"texture {texture} as {shared.Texture} {gl.Width}x{gl.Height}, {layers} layers, format 0x{gl.Format:X}");
        _sampling[shared.Texture] = Parameters(texture, shared.Texture);
        if (rgb)
        {
            GL.TextureParameter(shared.Texture, TextureParameterName.TextureSwizzleA, (int)All.One);
            shared.OpaqueAlpha = true;
        }

        Rewire(texture, shared.Texture);
        GL.DeleteTexture(texture);
        (_images[shared.Texture], Version) = (shared, Version + 1);
        _resting[shared.Texture] = f.Aspect == Vk.AspectDepth ? Vk.LayoutDepthAttachment : Vk.LayoutColorAttachment;
        return shared;
    }

    private static Samplers.State Parameters(int from, int to)
    {
        var (min, mag) = (Copy(from, to, TextureParameterName.TextureMinFilter),
            Copy(from, to, TextureParameterName.TextureMagFilter));
        var wrap = Copy(from, to, TextureParameterName.TextureWrapS);
        _ = Copy(from, to, TextureParameterName.TextureWrapT);
        var compare = Copy(from, to, TextureParameterName.TextureCompareMode) != 0;
        _ = Copy(from, to, TextureParameterName.TextureCompareFunc);
        var border = new float[4];
        GL.GetTextureParameter(from, GetTextureParameter.TextureBorderColor, border);
        GL.TextureParameter(to, TextureParameterName.TextureBorderColor, border);
        _ = Assert(from > 0) && Assert(to > 0) && Finite(border[0]);
        return Samplers.FromGl(min, mag, wrap, compare) with { Mipmaps = 0, WhiteBorder = border[0] > 0.5f };
    }

    private void Rewire(int from, int to)
    {
        if (!Assert(from > 0 && to > 0) || !Assert(_all.Count < 64)) return;
        foreach (var framebuffer in _all.ToArray().Bounded(64))
        {
            if (framebuffer is not { FboId: > 0 } f) continue;
            var attached = GlFramebuffer.Attachments(f.FboId);
            for (var i = 0; i <= GlFramebuffer.Colors; i++)
            {
                var a = attached[i];
                if (a.Texture != from) continue;
                var point = i == GlFramebuffer.Depth
                    ? FramebufferAttachment.DepthAttachment
                    : FramebufferAttachment.ColorAttachment0 + i;
                if (GlTexture.Of(to).Kind == GlTexture.Array2D && !a.Layered)
                    GL.NamedFramebufferTextureLayer(f.FboId, point, to, a.Level, a.Layer);
                else GL.NamedFramebufferTexture(f.FboId, point, to, a.Level);
            }
        }
    }

    private static int Copy(int from, int to, TextureParameterName name)
    {
        if (!Assert(from > 0) || !Assert(to > 0)) return 0;
        GL.GetTextureParameter(from, (GetTextureParameter)name, out int value);
        GL.TextureParameter(to, name, value);
        return value;
    }

    // The engine deletes the textures itself; only their Vulkan side goes here
    public void Release(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer) || !Assert(_images.Count <= MaxImages)) return;
        var colors = framebuffer.ColorTextureIds ?? [];
        if (_images.ContainsKey(framebuffer.DepthTextureId) || Array.Exists(colors, _images.ContainsKey))
            _ = device.WaitIdle(); // once for all of them
        for (var i = 0; i < Math.Min(colors.Length, MaxColors); i++) _ = Free(colors[i], false);
        _ = Free(framebuffer.DepthTextureId, false);
        _ = _swapped.RemoveAll(s => ReferenceEquals(s.Framebuffer, framebuffer));
    }

    private bool Free(int texture, bool wait)
    {
        Version++;
        if (!_images.Remove(texture, out var shared) || !Assert(texture > 0) || !Assert(shared.Texture == texture))
            return false;
        if (wait) _ = device.WaitIdle();
        _ = _sampling.Remove(texture);
        _ = _resting.Remove(texture);
        shared.Abandon(); // the texture stays: its owner deletes it
        return true;
    }

    public void Dispose()
    {
        _ = Assert(_images.Count <= MaxImages) && device.WaitIdle();
        foreach (var image in _images.Values.Bounded(MaxImages)) image.Abandon();
        _images.Clear();
        _sampling.Clear();
        _swapped.Clear();
        _resting.Clear();
        _copier.Dispose();
    }
}
