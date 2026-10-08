using System.Diagnostics;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Everything OpenGL has to do for it (adopting a pool, making or refreshing a copy, moving a framebuffer onto shared
// memory) happens in Prepare, before the frame's first segment opens; what shows up later is done between segments.
// Disposing hands everything back: the engine keeps every GL name.
internal sealed partial class TerrainRenderer : IDisposable
{
    private const string AdoptMark = "komet-adopt";
    private const int MaxPipelines = 4096, MaxTargets = 64, MaxPrograms = 256, MaxShared = 32, MaxPools = 4096;

    private readonly PipelineSet<PipelineKey> _pipelines;
    private readonly Dictionary<(int Fbo, ulong Buffers, bool ReadOnly), VulkanFrame.Target> _targets = [];
    private readonly Dictionary<int, Program> _programs = []; // by GL program
    private readonly ILogger? _logger;
    private readonly HashSet<int> _refusedSwaps = [];

    private readonly record struct PipelineKey(int Program, string Layout, string Formats, GlTap.Fixed State);

    public sealed class Program(string name, int id, GlslPort.Ported ported)
    {
        public string Name { get; } = name;
        public int Id { get; } = id;
        public GlslPort.Ported Ported { get; } = ported;
        public UniformMirror Mirror { get; } = new();

        private readonly Dictionary<TextureSet, TerrainDraw.Program> _sets = [];
        private UniformMirror.Entry?[]? _units;

        // The unit the port's sampler at reads, as the mirror holds it (Mirror.Int) but without a lookup by name: Lay made the
        // entries, and the mirror never replaces one
        public int Unit(int at)
        {
            _units ??= [.. Ported.Samplers.Select(s => Mirror.EntryOf(s.Name))];
            return Index(at, _units.Length) && _units[at] is { Held.Length: > 0 } entry ? (int)entry.Held[0] : 0;
        }

        // Cached per texture set: a draw works its textures out anew whenever any OpenGL state changed, nearly every pool's draw
        public TerrainDraw.Program Recorded(ReadOnlySpan<TerrainDraw.Texture> textures)
        {
            _ = Assert(textures.Length <= Ported.Samplers.Length) && Assert(_sets.Count <= MaxSets);
            var set = textures.Length <= TextureSet.Max ? TextureSet.From(textures) : (TextureSet?)null;
            if (set is { } known && _sets.TryGetValue(known, out var seen)) return seen;
            var named = new Dictionary<string, TerrainDraw.Texture>(StringComparer.Ordinal);
            for (var i = 0; i < Math.Min(textures.Length, MaxSamplers); i++) named[Ported.Samplers[i].Name] = textures[i];
            var made = new TerrainDraw.Program(Ported, Mirror, named);
            if (set is not { } fresh) return made;
            if (_sets.Count >= MaxSets) _sets.Clear();
            _sets[fresh] = made;
            return made;
        }
    }

    // A program's textures as a dictionary key. Four fields were too few: the chunk shaders bind more, so every draw built a new
    // dictionary and descriptor array (about 2 MB/s of garbage while flying)
    public readonly struct TextureSet : IEquatable<TextureSet>
    {
        public const int Max = 8;
        private readonly int _count;
        private readonly Textures _items;

        private TextureSet(ReadOnlySpan<TerrainDraw.Texture> textures)
        {
            var items = new Textures();
            for (var i = 0; i < Math.Min(textures.Length, Max); i++) items[i] = textures[i];
            (_count, _items) = (textures.Length, items);
        }

        public static TextureSet From(ReadOnlySpan<TerrainDraw.Texture> textures) =>
            Assert(textures.Length <= Max) && Assert(Max > 0) ? new TextureSet(textures) : default;

        public bool Equals(TextureSet other)
        {
            if (_count != other._count || !Assert(_count <= Max)) return false;
            for (var i = 0; i < Math.Min(_count, Max); i++)
                if (_items[i] != other._items[i]) return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is TextureSet other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_count);
            for (var i = 0; i < Math.Min(_count, Max); i++) hash.Add(_items[i]);
            return Assert(_count <= Max) ? hash.ToHashCode() : 0;
        }

        public static bool operator ==(TextureSet left, TextureSet right) => left.Equals(right);

        public static bool operator !=(TextureSet left, TextureSet right) => !left.Equals(right);

        [System.Runtime.CompilerServices.InlineArray(Max)]
        private struct Textures
        {
            private TerrainDraw.Texture _first;
        }
    }

    private TerrainRenderer(VulkanDevice device, (HostBuffer Quads, VulkanFrame Frame) made, ILogger? logger)
    {
        var (quads, frame) = made;
        _ = Assert(quads.Size > 0) && NotNull(frame);
        (Device, Quads, Frame, Recorder, _logger) = (device, quads, frame, new TerrainDraw(quads), logger);
        _pipelines = new PipelineSet<PipelineKey>(device, frame, logger, MaxPipelines);
        (Pools, Targets) = (new TerrainPools(device), new TerrainTargets(device));
        Staging = new GlStaging(device);
        (Copies, Sampling) = (new TerrainTextures(device, Staging), new Samplers(device.Handle));
        frame.Images = Handed;
        frame.Buffers = Buffers;
        frame.Filling = Filled;
        frame.Attached = Attachments;
        frame.Closes = WriteBack;
        GlTap.Deleting = Deleted;
        GlTap.FramebufferGone = Unattached;
    }

    // The engine makes a framebuffer object for each map tile it draws into and deletes it after: the name comes back with
    // another tile attached, so what was asked of it goes (else OpenGL's draws marked the first tile changed, never the next)
    private void Unattached(uint framebuffer)
    {
        var fbo = (int)framebuffer;
        var asked = Assert(fbo > 0) && _attached.Remove(fbo);
        if (!_textures.Remove(fbo) && !asked) return;
        var targeted = _lastTarget.Fbo == fbo;
        using var targets = _targets.Keys.GetEnumerator();
        for (var i = 0; i < MaxTargets && !targeted && targets.MoveNext(); i++) targeted = targets.Current.Fbo == fbo;
        if (targeted) ForgetTargets();
    }

    private List<VulkanFrame.Shared> Handed()
    {
        var key = (Targets.Version, Copies.Shape, _ownVersion); // Shape: the copies made, retired or resampled, not merely dirtied
        if (key == _handedAt && _handed is { } known) return known;
        _handed = [.. Targets.Resting, .. Copies.Images.Select(i =>
            new VulkanFrame.Shared(i, Vk.LayoutShaderRead, GlInterop.LayoutShaderRead)), .. OwnShared()];
        _handedAt = key;
        _ = Assert(_handed.Count <= VulkanFrame.MaxImages) && Assert(Copies.Count >= 0);
        return _handed;
    }

    private List<VulkanFrame.Shared>? _handed;
    private (long, long, long) _handedAt = (-1, -1, -1);

    // What OpenGL's signals name: the pools' buffers and the staging OpenGL writes for Vulkan's copies
    private uint[] Buffers()
    {
        var (pools, staged) = (Pools.GlBuffers, Staging.Buffers);
        if (!ReferenceEquals(pools, _buffers.Pools) || !ReferenceEquals(staged, _buffers.Staged))
            _buffers = (pools, staged, [.. pools, .. staged]);
        _ = Assert(_buffers.All.Length == pools.Length + staged.Length) && Assert(staged.Length <= 1 + Staging.Owned);
        return _buffers.All;
    }

    private (uint[]? Pools, uint[]? Staged, uint[] All) _buffers = (null, null, []);

    // The fills go into the open segment's setup: it opened after those OpenGL packed, or OpenGL signals it late
    private void Filled()
    {
        if (Copies.Filling && Frame.Open && Assert(Frame.Setup != IntPtr.Zero))
            Copies.Flush(Frame.Setup, Frame.Number, Frame.Restaged);
        _ = Assert(!Copies.Filling || !Frame.Open);
    }

    private void Deleted(uint texture)
    {
        _ = _specs.Remove((int)texture);
        if (!Assert(texture > 0) || (Targets.Find((int)texture) is null && !Copies.Has((int)texture))) return;
        Unshared((int)texture);
        if (!Copies.Has((int)texture)) return;
        // Sampled or drawn into in this frame: the open segment may use it, it hands it back first. Else it only goes once
        // the frames in flight are done with it (a GUI text made anew sampled its last frames ago)
        if (Copies.InUse((int)texture, Frame.Number))
        {
            Frame.Close("a copied texture deleted");
            ForgetTargets(); // a framebuffer drawn into through the copy has other textures now
        }

        Copies.Forget((int)texture, Frame.Number);
    }

    public VulkanDevice Device { get; }
    public HostBuffer Quads { get; }
    public VulkanFrame Frame { get; }
    public TerrainDraw Recorder { get; }
    public TerrainPools Pools { get; }
    public TerrainTargets Targets { get; }
    public TerrainTextures Copies { get; }
    public GlStaging Staging { get; }
    public Samplers Sampling { get; }
    public IReadOnlyCollection<Program> Programs => _programs.Values;

    public VulkanBackend Compute => _compute ??= new VulkanBackend(Device, Frame, OwnFind);

    private VulkanBackend? _compute;

    public long Draws { get; private set; }
    public long Left { get; private set; }
    public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);

    // quads: how many the index pattern covers
    public static TerrainRenderer? Create(VulkanDevice device, int quads, ILogger? logger, out string why)
    {
        why = "";
        if (!NotNull(device) || !Assert(quads > 0)) return null;
        var pattern = TerrainDraw.Quads(device, quads);
        if (pattern is null)
        {
            why = "no memory for the quad index pattern";
            return null;
        }

        var frame = VulkanFrame.Create(device, out why);
        if (frame is not null) return new TerrainRenderer(device, (pattern, frame), logger);
        pattern.Dispose();
        return null;
    }

    // measuring: count which uniforms change between draws (scene programs whose hot uniforms are not settled yet)
    public Program Watch(string name, int program, GlslPort.Ported ported, bool measuring = false)
    {
        _ = Assert(program > 0) && NotNull(ported);
        Forget(program);
        var made = new Program(name, program, ported);
        made.Mirror.Measuring = measuring;
        made.Mirror.Lay(ported.VertexUniforms, ported.FragmentUniforms, ported.HotUniforms,
            ported.Samplers.Select(s => s.Name));
        if (_programs.Count < MaxPrograms) _programs[program] = made;
        GlTap.Watch(program, ported, made.Mirror);
        return made;
    }

    // What Vulkan drew into the textures' copies goes back first: OpenGL may blend with it
    public void Rendered(int fbo)
    {
        if (!Assert(fbo > 0) || Ours(fbo)) return; // the shared ones have no copies: Vulkan samples them as they are
        var (colors, depth) = Attachments(fbo);
        foreach (var texture in colors.Bounded(GlFramebuffer.Colors))
        {
            Overdrawn(texture);
            if (texture > 0) Copies.DrawnByGl(texture, Frame.Open ? -1 : Frame.Number);
        }

        Overdrawn(depth);
        _ = Assert(colors.Length <= GlFramebuffer.Colors);
    }

    private void Overdrawn(int texture)
    {
        if (texture <= 0 || !Assert(texture < int.MaxValue)) return;
        WriteBack(texture);
        Changed(texture);
    }

    public bool Ours(int fbo) =>
        Assert(fbo >= 0) && fbo > 0 && Targets.Of(fbo) is { } framebuffer && Targets.Swapped(framebuffer);

    // The terrain's draws go into the engine's shared framebuffers and, while the scene hears of every OpenGL call (which
    // writes the copies back before OpenGL reads them), into framebuffer objects the engine does not list (Komet's distant
    // shadow map) through the copies of their textures; never into one of the engine's left to OpenGL
    public bool Takes(int fbo) =>
        Ours(fbo) || (fbo > 0 && GlTap.Touching is not null && Targets.Of(fbo) is null && Assert(GlTap.Scene));

    public Program? Find(int program) => Assert(program >= 0) ? _programs.GetValueOrDefault(program) : null;

    public void Forget(int program)
    {
        if (!Assert(program >= 0) || !_programs.Remove(program)) return;
        GlTap.Unwatch(program);
        _context = null;
        _pipelines.Forget(key => key.Program == program); // once no frame in flight uses them: no handoff, no wait
    }

    // While nothing listened, the engine may have deleted a texture and made another under the same name
    public void Rewatch()
    {
        foreach (var image in Targets.Images.ToArray().Bounded(MaxShared * 4))
            if (Stale(image.Texture, image))
                Unshared(image.Texture);
        foreach (var (texture, copy) in Copies.Copied.ToArray().Bounded(MaxShared * 4))
            if (Stale(texture, copy))
                Copies.Forget(texture);
        ForgetTargets();
        foreach (var program in _programs.Values.ToArray().Bounded(MaxPrograms))
            GlTap.Watch(program.Id, program.Ported, program.Mirror);
        _ = Assert(GlTap.Tapped) && Assert(_programs.Count <= MaxPrograms);
    }

    public bool Start(IReadOnlyList<FrameBufferRef> framebuffers, ReadOnlySpan<int> shared, out string why)
    {
        why = "";
        if (!NotNull(framebuffers)) return false;
        Frame.Next();
        CollectOwn();
        Copies.Collect(Frame.Done);
        Copies.Expire(Frame.Done);
        Staging.Collect(Frame.Done);
        Device.Arena.Collect(Frame.Number, Frame.Done);
        using var quiet = GlTap.Quietly(); // moving framebuffers onto shared memory is Komet's own OpenGL work
        Targets.Framebuffers = framebuffers;
        Targets.NewFrame();
        for (var i = 0; i < Math.Min(shared.Length, MaxShared); i++)
        {
            var at = shared[i];
            // The size is its textures' (the engine leaves Width and Height 0 in some, BlurVerticalLowRes's)
            if (at >= framebuffers.Count || framebuffers[at] is not { FboId: > 0 } framebuffer ||
                Targets.Swapped(framebuffer) || (framebuffer.ColorTextureIds is not { Length: > 0 } &&
                                                 framebuffer.DepthTextureId <= 0)) continue;
            ForgetTargets();
            if (Targets.Swap(framebuffer, out var refused)) continue;
            if (i == 0)
            {
                why = refused;
                return false;
            }

            if (_refusedSwaps.Add(framebuffer.FboId))
                _logger?.Notification("Komet: Vulkan leaves framebuffer {0} to OpenGL: {1}", framebuffer.FboId, refused);
        }

        if (Targets.Adoptions.Count > 0)
            _logger?.Notification("Komet: Vulkan shares {0}", string.Join("; ", Targets.Adoptions));
        Targets.Adoptions.Clear();
        return Assert(Frame.Number > 0);
    }

    // OpenGL's work, done while no segment is open, so none waits for it. Adopting pools, and taking in the arena block made
    // ahead for the next ones, is chunk work of the Before stage (ChunkBudget.Spent).
    public void Prepare(List<(VAO Vao, bool Classic)> pools)
    {
        if (!NotNull(pools) || !Assert(!Frame.Open)) return;
        using var quiet = GlTap.Quietly();
        var start = Stopwatch.GetTimestamp();
        _ = Assert(start > 0);
        var adopted = Pools.Adopt(pools, out var why) > 0;
        if (Device.Arena.Reserve(SharedArena.BlockBytes / 2) || adopted) ChunkBudget.Spent(Stopwatch.GetTimestamp() - start);
        Vintagestory.Client.ScreenManager.FrameProfiler?.Mark(AdoptMark);
        if (why.Length > 0) _ = Leave(why);
        _ = Copies.Settle(Frame.Number);
        Copies.Refresh(Frame.Number);
        if (Copies.PacksWaiting) _ = Frame.Late(); // the next segment's signal orders what OpenGL packed
        _ = Assert(Pools.Count <= MaxPools);
    }

    // A pool the engine has just made, before its first write; false leaves it to Prepare
    public bool Born(VAO vao, bool classic)
    {
        if (!NotNull(vao) || Frame.Open || !Assert(vao.VaoId > 0)) return false;
        using var quiet = GlTap.Quietly();
        return Pools.Adopt(vao, classic, out _, fresh: true) is not null;
    }

    private static bool Stale(int texture, SharedImage image)
    {
        if (!Assert(texture > 0) || !NotNull(image)) return true;
        if (!GL.IsTexture(texture)) return true;
        var now = GlTexture.Of(texture);
        return now.Width != image.Width || now.Height != image.Height;
    }

    public void Unshared(int texture)
    {
        Targets.Retry(texture);
        if (Targets.Find(texture) is null || !Assert(texture > 0)) return;
        Frame.Close("a shared texture deleted");
        ForgetTargets();
        _ = Device.WaitIdle(); // a frame in flight may still use it
        using var quiet = GlTap.Quietly();
        Targets.Forget(texture);
    }

    public void Release(VAO vao)
    {
        if (!NotNull(vao) || Pools.Find(vao) is null || !Assert(_programs.Count <= MaxPrograms)) return;
        Frame.Close("a pool disposed");
        _context = null;
        using var quiet = GlTap.Quietly();
        Pools.Release(vao);
    }

    public void Release(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer) || !Assert(_targets.Count <= MaxTargets)) return;
        Frame.Close("a framebuffer disposed");
        ForgetTargets();
        using var quiet = GlTap.Quietly();
        Targets.Release(framebuffer);
    }

    public void ForgetTargets()
    {
        _targets.Clear();
        _ownTargets.Clear();
        _attached.Clear();
        _textures.Clear();
        (_context, _lastTarget) = (null, default);
        _ = Assert(_targets.Count == 0);
    }

    public void Dispose()
    {
        if (GlTap.Deleting == Deleted) GlTap.Deleting = null;
        if (GlTap.FramebufferGone == Unattached) GlTap.FramebufferGone = null;
        Owning = false;
        Unwindowed(); // the real window again before anything else goes
        Frame.Close("Vulkan let go");
        Frame.Lazy = false; // OpenGL waits for the last segment and makes the copies into pools that still wait
        using var quiet = GlTap.Quietly(); // handing the pools and framebuffers back is Komet's own OpenGL work
        _ = Assert(_pipelines.Count <= MaxPipelines) && Device.WaitIdle();
        _compute?.Dispose(); // what the passes still hold goes with it; their handles name nothing any more
        _compute = null;
        foreach (var program in _programs.Keys.ToArray().Bounded(MaxPrograms)) GlTap.Unwatch(program);
        _programs.Clear();
        Frame.Dispose();
        Targets.Dispose();
        Pools.Dispose();
        Copies.Dispose();
        Staging.Dispose();
        Sampling.Dispose();
        _blank?.Dispose();
        foreach (var own in _owns.Bounded(MaxOwn)) own.Dispose();
        _pipelines.Dispose();
        Quads.Dispose();
    }
}
