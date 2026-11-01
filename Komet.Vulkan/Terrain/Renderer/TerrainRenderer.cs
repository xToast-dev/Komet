using Komet.Chunks;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Every resource is Vulkan's: targets (TerrainTargets), textures filled from the uploads (TextureImages), pools and
// persistent mappings (TerrainPools, PersistentMaps), pool uploads from client memory (PoolWritten), copies as blits.
// OpenGL keeps its objects and gets every upload so it can take over; resting (GlTap.Resting) it skips what Vulkan
// leaves, which is missing from the picture. Pools are adopted in Prepare or as they are born. Dispose hands everything
// back: the engine keeps every GL name, and OpenGL's persistent mappings get the engine's pointers again.
internal sealed partial class TerrainRenderer : IDisposable
{
    private const string AdoptMark = "komet-adopt";
    private const int MaxPipelines = 4096, MaxTargets = 64, MaxPrograms = 256, MaxShared = 32;

    private readonly PipelineSet<PipelineKey> _pipelines;
    private readonly Dictionary<(int Fbo, ulong Buffers, bool ReadOnly), VulkanFrame.Target> _targets = [];
    private readonly Dictionary<int, Program> _programs = []; // by GL program
    private readonly ILogger? _logger;
    private readonly HashSet<int> _refusedSwaps = [];
    private List<VulkanFrame.Shared>? _handed;
    private long _handedAt = -1;
    private VulkanBackend? _compute;

    private readonly record struct PipelineKey(int Program, string Layout, string Formats, GlTap.Fixed State);

    public sealed class Program(string name, int id, GlslPort.Ported ported)
    {
        public string Name { get; } = name;
        public int Id { get; } = id;
        public GlslPort.Ported Ported { get; } = ported;
        public UniformMirror Mirror { get; } = new();

        // ShaderCache key of its pipelines (PipelineRecipes), "" for none
        public string Sources { get; set; } = "";

        private readonly Dictionary<TextureSet, TerrainDraw.Program> _sets = [];
        private UniformMirror.Entry?[]? _units;

        // The texture unit of the program's sampler at, without a name lookup: Lay made the entries, the mirror never replaces one
        public int Unit(int at)
        {
            _units ??= [.. Ported.Samplers.Select(s => Mirror.EntryOf(s.Name))];
            return Index(at, _units.Length) && _units[at] is { Held.Length: > 0 } entry ? (int)entry.Held[0] : 0;
        }

        // Cached per texture set: textures are re-resolved on any OpenGL state change, nearly every pool draw
        public TerrainDraw.Program Recorded(ReadOnlySpan<TerrainDraw.Texture> textures)
        {
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

    // Four fields were too few for the chunk shaders: every draw built a dictionary and descriptors (~2 MB/s while
    // flying)
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

        public static TextureSet From(ReadOnlySpan<TerrainDraw.Texture> textures) => new(textures);

        public bool Equals(TextureSet other)
        {
            if (_count != other._count) return false;
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
            return hash.ToHashCode();
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
        (Device, Quads, Frame, Recorder, _logger) = (device, quads, frame, new TerrainDraw(quads), logger);
        _pipelines = new PipelineSet<PipelineKey>(device, frame, logger, MaxPipelines);
        (Pools, Targets) = (new TerrainPools(device, frame), new TerrainTargets(device));
        Sampling = new Samplers(device.Handle);
        frame.Images = Handed;
        frame.Filling = Filled;
        GlTap.Deleting = Deleted;
        GlTap.FramebufferGone = Unattached;
        (Images, Maps) = (new TextureImages(device, frame), new PersistentMaps(device));
        (Targets.Claimed, Pools.Mapped) = (Images.Forget, Maps.Of);
        (GlTap.Mapping, GlTap.FramebufferChanged, GlTap.Unmapping) = (Maps.Map, Unattached, Maps.Unmapped);
    }

    public VulkanDevice Device { get; }
    public HostBuffer Quads { get; private set; }
    public VulkanFrame Frame { get; }
    public TerrainDraw Recorder { get; private set; }
    public TerrainPools Pools { get; }
    public TerrainTargets Targets { get; }
    public Samplers Sampling { get; }
    public TextureImages Images { get; }
    public PersistentMaps Maps { get; }

    // GlTap.Taps at creation: the record it heard
    public long RecordTaps { get; set; } = -1;

    // Key for the scene's resolved textures: the images' changes
    public long TextureVersion => Images.Version;

    public VulkanBackend Compute => _compute ??= new VulkanBackend(Device, Frame, SampledFind);

    public long Draws { get; private set; }

    // Map tile framebuffers are made and deleted per tile; a reused name must not keep the old tile's state
    private void Unattached(uint framebuffer)
    {
        var fbo = (int)framebuffer;
        if (!_attached.Remove(fbo)) return;
        var targeted = _lastTarget.Fbo == fbo;
        using var targets = _targets.Keys.GetEnumerator();
        for (var i = 0; i < MaxTargets && !targeted && targets.MoveNext(); i++) targeted = targets.Current.Fbo == fbo;
        if (targeted) ForgetTargets();
    }

    // The targets' images, held by every segment from its start
    private List<VulkanFrame.Shared> Handed()
    {
        if (Targets.Version == _handedAt && _handed is { } known) return known;
        _handed = [.. Targets.Resting];
        _handedAt = Targets.Version;
        return _handed;
    }

    // Into the open segment's setup
    private void Filled()
    {
        if (Frame.Open) Images.Filled(Frame.Setup);
    }

    private void Deleted(uint texture)
    {
        Images.Forget((int)texture);
        if (Targets.Find((int)texture) is not null) Unshared((int)texture);
    }

    // quads: index pattern size
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

        VulkanFrame? frame = null;
        var kept = Owned.Kept(pattern, device, (_, device) =>
        {
            frame = VulkanFrame.Create(device, out var refused);
            return frame is null ? $"no frame: {refused}" : null;
        }, out why);
        return kept is null || frame is null ? null : new TerrainRenderer(device, (kept, frame), logger);
    }

    // measuring: count uniforms changing between draws (scene programs not yet settled). Null with MaxPrograms watched:
    // a program neither kept nor watched could never be forgotten
    public Program? Watch(string name, int program, GlslPort.Ported ported, bool measuring = false)
    {
        Forget(program);
        if (_programs.Count >= MaxPrograms) return null;
        var made = new Program(name, program, ported);
        made.Mirror.Measuring = measuring;
        made.Mirror.Lay(ported.VertexUniforms, ported.FragmentUniforms, ported.HotUniforms,
            ported.Samplers.Select(s => s.Name));
        _programs[program] = made;
        GlTap.Watch(program, ported, made.Mirror);
        return made;
    }

    public bool Ours(int fbo) =>
        fbo > 0 && Targets.Of(fbo) is { } framebuffer && Targets.Swapped(framebuffer);

    // Engine framebuffers on Vulkan's images, plus (while the scene hears every OpenGL call) unlisted ones such as
    // Komet's distant shadow map via their textures' images; never an engine framebuffer left out
    public bool Takes(int fbo) =>
        Ours(fbo) || (fbo > 0 && GlTap.Touching is not null && Targets.Of(fbo) is null && Assert(GlTap.Tapped));

    public Program? Find(int program) => Assert(program >= 0) ? _programs.GetValueOrDefault(program) : null;

    public void Forget(int program)
    {
        if (!Assert(program >= 0) || !_programs.Remove(program)) return;
        GlTap.Unwatch(program);
        _context = null;
        _pipelines.Forget(key => key.Program == program); // once no frame in flight uses them: no handoff, no wait
    }

    // Unheard, the engine may have deleted a texture and reused its name
    public void Rewatch()
    {
        foreach (var image in Targets.Images.ToArray().Bounded(MaxShared * 4))
            if (Stale(image.Texture, image))
                Unshared(image.Texture);
        ForgetTargets();
        foreach (var program in _programs.Values.ToArray().Bounded(MaxPrograms))
            GlTap.Watch(program.Id, program.Ported, program.Mirror);
    }

    public bool Start(IReadOnlyList<FrameBufferRef> framebuffers, ReadOnlySpan<int> shared, out string why)
    {
        why = "";
        if (!NotNull(framebuffers)) return false;
        Images.Started(); // a world's uploads before its first frame
        Frame.Fresh();
        CollectSnapshots(false);
        Maps.Collect(Frame.Done);
        using var quiet = GlTap.Quietly(); // asking framebuffers' textures is Komet's own OpenGL work
        Pools.Collect(Frame.Done); // released pools' buffers (their GL buffers too: Komet's own)
        Targets.Framebuffers = framebuffers;
        for (var i = 0; i < Math.Min(shared.Length, MaxShared); i++)
        {
            var at = shared[i];
            // Size from its textures (the engine leaves Width/Height 0 in some, e.g. BlurVerticalLowRes)
            if (at >= framebuffers.Count || framebuffers[at] is not { FboId: > 0 } framebuffer ||
                Targets.Swapped(framebuffer) || (framebuffer.ColorTextureIds is not { Length: > 0 } &&
                                                 framebuffer.DepthTextureId <= 0)) continue;
            var version = Targets.Version;
            var swapped = Targets.Swap(framebuffer, out var refused);
            // a refusal is retried every frame and mostly changes nothing: the targets and their attachment queries stay
            if (swapped || Targets.Version != version) ForgetTargets();
            if (swapped) continue;
            if (i == 0)
            {
                why = refused;
                return false;
            }

            if (_refusedSwaps.Add(framebuffer.FboId))
                _logger?.Notification("Komet: Vulkan leaves framebuffer {0} to OpenGL: {1}", framebuffer.FboId, refused);
        }

        if (Targets.Replaced.Count > 0)
            _logger?.Notification("Komet: Vulkan stands for {0}", string.Join("; ", Targets.Replaced));
        Targets.Replaced.Clear();
        return Assert(Frame.Number > 0);
    }

    // Adoption is Before-stage chunk work (ChunkBudget.Spent)
    public void Prepare(List<(VAO Vao, bool Classic)> pools)
    {
        if (!NotNull(pools)) return;
        using var quiet = GlTap.Quietly();
        var start = Stopwatch.GetTimestamp();
        if (Pools.Adopt(pools, out var why) > 0) ChunkBudget.Spent(Stopwatch.GetTimestamp() - start);
        Vintagestory.Client.ScreenManager.FrameProfiler?.Mark(AdoptMark);
        if (why.Length > 0) _ = Leave(why);
    }

    // Before its first write; false leaves it to Prepare
    public bool Born(VAO vao, bool classic)
    {
        if (!NotNull(vao) || !Assert(vao.VaoId > 0)) return false;
        using var quiet = GlTap.Quietly();
        return Pools.Adopt(vao, classic, out _, fresh: true) is not null;
    }

    private static bool Stale(int texture, SharedImage image)
    {
        if (!GlTexture.Exists(texture)) return true;
        var now = GlTexture.Of(texture);
        return now.Width != image.Width || now.Height != image.Height;
    }

    private void Unshared(int texture)
    {
        if (Targets.Find(texture) is null) return;
        Frame.Close("a shared texture deleted");
        ForgetTargets();
        _ = Device.WaitIdle(); // a frame in flight may still use it
        using var quiet = GlTap.Quietly();
        Targets.Forget(texture);
    }

    public void Release(VAO vao)
    {
        if (!NotNull(vao) || Pools.Find(vao) is not { } pool) return;
        _ = _poolCopies.RemoveAll(c => ReferenceEquals(c.To, pool.Faces) || ReferenceEquals(c.To, pool.Indices) ||
                                       Array.IndexOf(pool.Vertices, c.To) >= 0); // nothing more is copied into them
        _context = null;
        using var quiet = GlTap.Quietly();
        Pools.Release(vao, Frame.Number); // what this frame recorded still reads them: they go once it is done
    }

    public void Release(FrameBufferRef framebuffer)
    {
        if (!NotNull(framebuffer)) return;
        Frame.Close("a framebuffer disposed");
        ForgetTargets();
        using var quiet = GlTap.Quietly();
        Targets.Release(framebuffer);
    }

    public void ForgetTargets()
    {
        _targets.Clear();
        _attached.Clear();
        _formats.Clear();
        (_context, _lastTarget) = (null, default);
    }

    // Vulkan mappings of deleted buffers go once the frame being drawn is done
    public void BuffersGone(ReadOnlySpan<uint> buffers)
    {
        if (Assert(buffers.Length < 1 << 20)) Maps.Gone(buffers, Frame.Number);
    }

    // Frame ends with no segment open: waiting work runs on its own
    public void Ending()
    {
        PoolsAlone();
        Images.Ending();
    }

    // Renderers live from mod start: the quad pattern grows with the largest engine pool
    public void Requads(int quads)
    {
        if (!Assert(quads > 0) || (ulong)quads * 24 <= Quads.Size || quads > 1 << 26) return;
        if (TerrainDraw.Quads(Device, quads) is not { } grown) return;
        Frame.Close("the quad pattern grows");
        _ = Device.WaitIdle();
        var old = Quads;
        (Quads, Recorder, _context) = (grown, new TerrainDraw(grown), null);
        old.Dispose();
    }

    public void Dispose()
    {
        if (GlTap.Deleting == Deleted) GlTap.Deleting = null;
        if (GlTap.FramebufferGone == Unattached) GlTap.FramebufferGone = null;
        (GlTap.Mapping, GlTap.FramebufferChanged, GlTap.Unmapping) = (null, null, null);
        Unwindowed(); // the real window again before anything else goes
        Frame.Close("Vulkan let go");
        using var quiet = GlTap.Quietly(); // handing the pools and framebuffers back is Komet's own OpenGL work
        _ = Device.WaitIdle();
        _compute?.Dispose(); // what the passes still hold goes with it; their handles name nothing any more
        _compute = null;
        foreach (var program in _programs.Keys.ToArray().Bounded(MaxPrograms)) GlTap.Unwatch(program);
        _programs.Clear();
        Frame.Dispose();
        Targets.Dispose();
        Pools.Dispose();
        Maps.Dispose(); // after the pools: what OpenGL draws from again, the engine's pointers pointed back
        Images.Dispose();
        CollectSnapshots(true);
        _poolStaging?.Dispose();
        Sampling.Dispose();
        _blank?.Dispose();
        _pipelines.Dispose();
        Quads.Dispose();
    }
}

// OpenGL's picture is never shown, so calls Vulkan does not take are missing; each kind logged once.
internal sealed partial class TerrainRenderer
{
    private const int MaxMissing = 64, MaxReasons = 512;
    private const string OtherKinds = "other kinds";

    private static readonly Dictionary<(string, string, string, string), string> Reasons = [];

    private readonly Dictionary<string, long> _missing = new(StringComparer.Ordinal);

    public long Missed { get; private set; }

    // One more stands for all past MaxMissing
    public int MissedKind => _missing.Count;

    // Missing only for the frames their port or pipeline was building
    public long MissedWaiting { get; private set; }

    public void Miss(string what)
    {
        if (!NotNull(what) || what.Length == 0) return;
        Missed++;
        ref var count = ref CollectionsMarshal.GetValueRefOrNullRef(_missing, what);
        if (!Unsafe.IsNullRef(ref count))
        {
            count++;
            return;
        }

        var key = _missing.Count < MaxMissing ? what : OtherKinds;
        _missing[key] = _missing.GetValueOrDefault(key) + 1;
        if (key == what) _logger?.Warning("Komet: Vulkan cannot draw {0}: it is missing from the picture", what);
    }

    // A rejected draw comes again every frame with the same reason: its text is formatted once (keyed by its format and
    // parts), not allocated per draw. Render thread only, like Miss.
    public static string Reason(string format, string a) => !NotNull(format) ? "" :
        Made((format, a, "", ""), static k => string.Format(CultureInfo.InvariantCulture, k.Item1, k.Item2));

    public static string Reason(string format, string a, string b, string c) => !NotNull(format) ? "" :
        Made((format, a, b, c), static k => string.Format(CultureInfo.InvariantCulture, k.Item1, k.Item2, k.Item3, k.Item4));

    private static string Made((string, string, string, string) key, System.Func<(string, string, string, string), string> format)
    {
        if (!NotNull(key.Item2) || !NotNull(key.Item3) || !NotNull(key.Item4)) return "";
        if (Reasons.TryGetValue(key, out var made)) return made;
        made = format(key);
        if (Reasons.Count < MaxReasons) Reasons[key] = made;
        return made;
    }

    public void MissWaiting() => MissedWaiting++;

    public string MissedKinds(int most) => string.Join("; ", MissedTop(most));

    // The most frequent kinds with their counts, most first
    public string[] MissedTop(int most)
    {
        if (_missing.Count == 0 || !Assert(most > 0)) return [];
        return [.. _missing.OrderByDescending(static m => m.Value).Take(most).Select(static m => $"{m.Key} {m.Value}")];
    }
}

// VulkanOnlyLean lets go with the device alive: OpenGL holds 1x1 stand-ins of large textures and 16-byte ones of large
// buffers. Each gets its full size back after GPU idle, filled via host memory from Vulkan's copy (image, pool buffer,
// mirror); blank if none or inexact. Not after a device fault.
internal sealed unsafe partial class TerrainRenderer
{
    private const int MaxLeanObjects = 1 << 14;

    private readonly HashSet<int> _restored = []; // textures given back whole: HandBack writes them no more

    // Lean stand-ins for textures OpenGL got whole before lean (GlTap.LeanEarlier): Vulkan-held, drawn targets, or
    // mipmapped textures (atlas) with a current image
    public (int Count, long Bytes) LeanEarlier() =>
        Frame.Fault.Length == 0
            ? GlTap.LeanEarlier((texture, mipmapped) =>
                Targets.Find((int)texture) is not null || (mipmapped && Images.Find((int)texture) is not null))
            : (0, 0);

    // null after a fault (nothing given back)
    public (int Textures, int Blank, int Buffers, long Bytes)? Restore(BufferMirrors? mirrors)
    {
        var images = Images;
        if (Frame.Fault.Length > 0) return null;
        if (Frame.Open) Frame.Close("the lean textures and buffers given back to OpenGL");
        Ending(); // what waits for a segment (uploads, pool writes, mirror writes) lands before anything is read
        mirrors?.Ending();
        if (!Device.WaitIdle() || Frame.Fault.Length > 0) return null;
        var (whole, blank, buffers, bytes) = (0, 0, 0, 0L);
        using var quiet = GlTap.Quietly(); // Komet's own OpenGL work: not lean, not heard as the engine's
        foreach (var texture in GlTap.LeanTextureNames().Bounded(MaxLeanObjects))
        {
            // target images rest per RestingOf, textures sampled
            var image = Targets.Find((int)texture) ?? images.Held((int)texture);
            if (images.Restored((int)texture, image, Targets.RestingOf((int)texture))) whole++;
            else blank++;
            _ = _restored.Add((int)texture);
        }

        foreach (var (buffer, size, usage) in GlTap.LeanBufferList().Bounded(MaxLeanObjects))
        {
            var pool = Pools.BufferOf((int)buffer);
            var from = pool is not null ? (pool.Buffer, 0UL, (long)pool.Size) : mirrors?.Held(buffer);
            bytes += Restored(buffer, (size, usage), from);
            buffers++;
        }

        return (whole, blank, buffers, bytes);
    }

    // glNamedBufferData at full size with Vulkan's contents, else none
    private long Restored(uint buffer, (long Size, uint Usage) spec, (ulong Buffer, ulong Offset, long Size)? from)
    {
        if (!Assert(buffer > 0) || !Assert(spec.Size > 0)) return 0;
        var copied = from is { } f ? Math.Min(f.Size, spec.Size) : 0;
        using var host = copied > 0 && spec.Size <= int.MaxValue ? HostBuffer.Create(Device, (ulong)spec.Size, bulk: true) : null;
        var read = host is not null && host.Take((int)spec.Size, out _, 16) == 0 && Read(from!.Value, host.Buffer, copied);
        if (read && copied < spec.Size) new Span<byte>(host!.Pointer(copied), (int)(spec.Size - copied)).Clear();
        OpenTK.Graphics.OpenGL.GL.NamedBufferData((int)buffer, (int)Math.Min(spec.Size, int.MaxValue),
            read ? (IntPtr)host!.Pointer(0) : IntPtr.Zero, (OpenTK.Graphics.OpenGL.BufferUsageHint)spec.Usage);
        if (!read) copied = 0;

        GlTap.Restored(buffer, false);
        return copied;
    }

    private bool Read((ulong Buffer, ulong Offset, long Size) from, ulong into, long size)
    {
        if (!Assert(from.Buffer != 0) || !Assert(size > 0)) return false;
        return Device.Run(commands =>
        {
            var region = new Vk.BufferCopy { SourceOffset = from.Offset, Size = (ulong)size };
            VkApi.CmdCopyBuffer(commands, from.Buffer, into, 1, &region);
            VulkanFrame.HostReadable(commands);
        }, []);
    }
}
