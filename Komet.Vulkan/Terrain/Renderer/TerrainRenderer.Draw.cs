using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// A pool draw recorded in the open segment; what Vulkan cannot draw is missing from the picture (Miss).
internal sealed partial class TerrainRenderer
{
    private const int MaxRanges = 1 << 16, MaxSamplers = 32, LayoutDepthReadOnly = 4, MaxPrepared = 64, MaxSets = 1024;
    private const GlTap.Caps Refused = GlTap.Caps.Stencil | GlTap.Caps.Discard | GlTap.Caps.AlphaToCoverage |
                                       GlTap.Caps.LogicOp;

    private readonly int[] _starts = new int[MaxRanges];
    private readonly List<SharedImage> _sampled = [];
    private readonly TerrainDraw.Texture[] _resolved = new TerrainDraw.Texture[MaxSamplers];
    private readonly Context _reused = new();
    private readonly Dictionary<int, GlFramebuffer.Attached[]> _attached = [];

    // By target: passes alternate (shadow maps, screen, scene); a single remembered one was rebuilt every turn
    private readonly Dictionary<VulkanFrame.Target, (TerrainPipeline.Target Target, string Key, bool[]? Opaque)> _formats =
        new(ReferenceEqualityComparer.Instance);

    // State of Prepare's last run plus images sampled since: held and in sampling layout while the rendering lasts
    private readonly SharedImage[] _preparedImages = new SharedImage[MaxPrepared];
    private (long Serial, long Renderings, VulkanFrame.Target? Target, int Count) _prepared;

    private Context? _context;
    private ((ulong Buffer, ulong Offset) Commands, (ulong Buffer, ulong Offset) Count, int Max)? _counted;
    private (int Fbo, long Changes, bool ReadOnly, VulkanFrame.Target? Target) _lastTarget;
    private bool _replaced; // an attachment was replaced while a target was made: the names it had read may be stale
    private int _targetKeys;

    // Last draw's preparation, reused while state, samplers, formats and rendering are unchanged (a pass's draws differ
    // in pool and uniforms only). One instance: a draw reads its context only while recorded.
    private sealed class Context
    {
        public long Version, Renderings;
        public Program? Program;
        public int Samplers;
        public string Layout = "";
        public TerrainPipeline Pipeline = null!;
        public TerrainDraw.Program Recorded = null!;
    }

    public long Counted { get; private set; } // OcclusionCulling's draws taken

    public long PipelinesStarted => _pipelines.Started;

    // Another recorder (the scene) posted: the next pool draw prepares afresh
    public void Unprepared() => _context = null;

    // As MultiDrawElements takes them: starts are pointers, two ints each
    public bool Take(VAO vao, (int[] Indices, int[] Sizes, int Count, bool Ssbo) draw, System.Func<int, Samplers.State> samplers) =>
        Taken(vao, draw, samplers);

    public bool TakeCounted(VAO vao, bool ssbo, ((ulong, ulong) Commands, (ulong, ulong) Count, int Max) counted,
        System.Func<int, Samplers.State> samplers)
    {
        if (!Assert(counted.Max >= 0) || !NotNull(vao)) return false;
        _counted = counted;
        try
        {
            return Take(vao, ([], [], 0, ssbo), samplers);
        }
        finally
        {
            _counted = null;
        }
    }

    private bool Taken(VAO vao, (int[] Indices, int[] Sizes, int Count, bool Ssbo) draw,
        System.Func<int, Samplers.State> samplers)
    {
        var program = _programs.GetValueOrDefault(GlTap.Program);
        if (!NotNull(vao) || !NotNull(samplers) || program is null) return false;
        if (!Assert(draw.Indices.Length >= 2 * draw.Count && draw.Sizes.Length >= draw.Count) || draw.Count > MaxRanges)
            return Leave("more ranges than a draw takes");
        var why = "";
        var pool = Pools.Find(vao) ?? Adopted(vao, !draw.Ssbo, out why);
        if (pool is null) return Leave(why);
        if (_context is { } c && c.Version == GlTap.Version && ReferenceEquals(c.Program, program) &&
            c.Samplers == program.Mirror.SamplerWrites && c.Layout == pool.Layout && c.Renderings == Frame.Renderings &&
            Frame.Open)
            return Recorded(c, pool, draw);
        _context = null;
        var state = GlTap.State;
        var target = Target(state, out why); // Komet's distant shadow map: its copy
        if (target is null) return Leave(why);
        var recorded = Textures(program, target, samplers, out why);
        var fed = recorded is not null && Fed(target, _sampled); // a feedback draw: from snapshots, never kept
        if (fed) recorded = Snapshotted(program, recorded!, target, out why);
        if (recorded is null) return Leave(why);
        var pipeline = Pipeline(program, pool, target, state, out var waiting);
        if (pipeline is null && waiting) MissWaiting();
        if (pipeline is null) return !waiting && Leave(Reason("no pipeline for {0} in this state (see the log)", program.Name));
        if (!Segment(target, _sampled, out why)) return Leave(why);
        Frame.Viewport(GlTap.Viewport);
        Frame.Scissor((state.Caps & (uint)GlTap.Caps.Scissor) != 0 ? GlTap.Scissor : null); // a tile of the distant map
        var context = _reused;
        (context.Version, context.Program, context.Samplers, context.Layout, context.Renderings) =
            (GlTap.Version, program, program.Mirror.SamplerWrites, pool.Layout, Frame.Renderings);
        (context.Pipeline, context.Recorded) = (pipeline, recorded);
        Drawn(target);
        if (!Recorded(context, pool, draw)) return false;
        _context = fed ? null : context; // the next draw snapshots anew what this one drew
        return true;
    }

    private bool Recorded(Context context, TerrainPools.Pool pool, (int[] Indices, int[] Sizes, int Count, bool Ssbo) draw)
    {
        if (Frame.Uploads is not { } uploads) return false;
        var (pipeline, program) = (context.Pipeline, context.Recorded);
        for (var i = 0; i < Math.Min(draw.Count, MaxRanges); i++) _starts[i] = draw.Indices[2 * i];
        var recorded = _counted is { } counted
            ? Recorder.Counted(Frame, uploads, (pipeline, pool), program,
                (draw.Ssbo, counted.Commands, counted.Count, counted.Max))
            : Recorder.Draw(Frame, uploads, (pipeline, pool), program,
                (_starts, draw.Sizes, draw.Count, draw.Ssbo));
        if (!recorded)
        {
            _context = null;
            return Leave("the recorder refused the draw (a descriptor without its resource, or the uploads full)");
        }

        if (_counted is not null) Counted++;
        Draws++;
        return true;
    }

    // Pool made since Prepare: adopted with no OpenGL work
    private TerrainPools.Pool? Adopted(VAO vao, bool classic, out string why)
    {
        using var quiet = GlTap.Quietly();
        var pool = Pools.Adopt(vao, classic, out why);
        _context = null;
        return pool;
    }

    // A segment older than a new image closes first; Begin keeps held images. render: begin the target's rendering.
    public bool Segment(VulkanFrame.Target target, List<SharedImage> sampled, out string why, bool render = true)
    {
        why = "";
        if (!NotNull(target) || !NotNull(sampled) || !Assert(sampled.Count <= MaxSamplers)) return false;
        if (Again(target, sampled)) return true;
        Holding(target, sampled);
        var held = Frame.Open && Held(target, sampled);
        if (Frame.Open && !held) Frame.Close("an image new since the segment opened");
        if (!Frame.Begin(out why)) return false;
        if (!held) Holding(target, sampled);
        if (!held && !Held(target, sampled)) return Refuse(out why, "an image the draw uses is not held");
        if (!render) return Assert(Frame.Open);
        if (!Frame.Prepare(target, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sampled)))
            return Refuse(out why, "the draw samples what it draws into");
        Prepared(target, sampled);
        return Assert(Frame.Open);
    }

    private void Prepared(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        var (serial, renderings, last, count) = _prepared;
        var same = serial == Frame.Serial && renderings == Frame.Renderings && ReferenceEquals(last, target);
        if (!same || count + sampled.Count > MaxPrepared) count = 0;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (Array.IndexOf(_preparedImages, image, 0, count) < 0)
                _preparedImages[count++] = image;
        _prepared = (Frame.Serial, Frame.Renderings, target, count);
    }

    // Same target, segment and rendering, sampling only images already sampled: nothing to do
    private bool Again(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        var (serial, renderings, last, count) = _prepared;
        if (!Frame.Open || serial != Frame.Serial || renderings != Frame.Renderings || !ReferenceEquals(last, target) ||
            Frame.Fault.Length > 0) return false;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (Array.IndexOf(_preparedImages, image, 0, count) < 0)
                return false;
        return true;
    }

    private bool Held(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        if (!Assert(Frame.Open)) return false;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (!Frame.Holds(image))
                return false;
        foreach (var color in target.Colors.Bounded(GlTap.MaxBuffers))
            if (color is not null && !Frame.Holds(color))
                return false;
        return target.Depth is null || Frame.Holds(target.Depth);
    }

    private static bool Refuse(out string why, string reason)
    {
        why = reason;
        return false;
    }

    // readOnly: depth tested, not written. A non-engine framebuffer is drawn via texture copies; the window (0) only as
    // Vulkan's stand-in.
    public VulkanFrame.Target? Target(GlTap.Fixed state, out string why, int into = -1, bool? depthReadOnly = null)
    {
        why = "";
        var fbo = into >= 0 ? into : GlTap.DrawFramebuffer;
        var readOnly = depthReadOnly ?? (!state.DepthTest || !state.DepthWrite);
        if (_lastTarget is (var lastFbo, var changes, var lastReadOnly, { } last) && lastFbo == fbo &&
            changes == GlTap.BufferChanges && lastReadOnly == readOnly) return last;
        var ours = Ours(fbo);
        if (!ours && fbo == 0)
            return None<VulkanFrame.Target>(out why, "draws into a framebuffer Vulkan does not stand for");
        var buffers = GlTap.DrawBuffersOf(fbo);
        var key = (fbo, Packed(buffers), readOnly);
        if (_targets.TryGetValue(key, out var made))
        {
            _lastTarget = (fbo, GlTap.BufferChanges, readOnly, made);
            return made;
        }
        made = Assembled(fbo, buffers, readOnly, !ours, out why);
        for (var again = 0; again < GlFramebuffer.Colors && made is null && _replaced; again++)
            made = Assembled(fbo, buffers, readOnly, !ours, out why); // asked anew after each replacement
        return Remembered(key, made, readOnly);
    }

    // Five bits per output: attachment + 1 (DrawBuffersOf gives -1 to 15)
    private static ulong Packed(int[] buffers)
    {
        var packed = 0UL;
        for (var i = 0; i < Math.Min(buffers.Length, GlTap.MaxBuffers); i++)
            packed |= (ulong)((buffers[i] + 1) & 0x1F) << (5 * i);
        return Assert(buffers.Length <= GlTap.MaxBuffers) ? packed : ulong.MaxValue;
    }

    private VulkanFrame.Target? Remembered((int Fbo, ulong Buffers, bool ReadOnly) key, VulkanFrame.Target? made,
        bool readOnly)
    {
        if (made is null) return null;
        if (_targets.Count >= MaxTargets) ForgetTargets();
        _targets[key] = made;
        _lastTarget = (key.Fbo, GlTap.BufferChanges, readOnly, made);
        return made;
    }

    // The target's image, or for unknown framebuffers the texture's (GlFramebuffer.Depth for depth); at: attached level
    // and layer
    public SharedImage? ReadAt(int fbo, int attachment, out (int Level, int Layer) at, out string why)
    {
        (why, at) = ("", default);
        if (!Assert(fbo > 0) || !Index(attachment, GlFramebuffer.Colors + 1)) return null;
        var attached = Asked(fbo)[attachment];
        if (attached.Texture == 0) return None<SharedImage>(out why, "reads no attachment");
        at = (attached.Level, attached.Layer);
        if (attached.Layered) return None<SharedImage>(out why, "reads every layer at once");
        var image = Ours(fbo) ? Attachment(attached, out why) : Copy(attached.Texture, out why);
        if (image is not null && (attached.Level >= image.Levels || attached.Layer >= image.Layers))
            return None<SharedImage>(out why, "reads past the image's levels or layers");
        return image;
    }

    // 0: the image's own view; layers: all of a layered attachment
    private static (ulong View, int Width, int Height, int Layers)? Viewed(SharedImage image, GlFramebuffer.Attached attached)
    {
        if (attached.Level >= image.Levels || attached.Layer >= image.Layers || !Assert(attached.Level >= 0) ||
            !Assert(attached.Layer >= 0)) return null;
        var (width, height) = (Math.Max(image.Width >> attached.Level, 1), Math.Max(image.Height >> attached.Level, 1));
        if (attached.Layered) return (image.LayeredView(attached.Level), width, height, image.Layers);
        var view = image.Levels > 1 || image.Layers > 1 ? image.LevelView(attached.Level, attached.Layer) : 0;
        return (view, width, height, 1);
    }

    // Foreign framebuffers' images are now ahead of their GL textures
    public void Drawn(VulkanFrame.Target target)
    {
        if (!NotNull(target) || !Assert(Frame.Open) || target.Stands.Length == 0) return;
        foreach (var texture in target.Stands.Bounded(GlFramebuffer.Colors + 1)) Images.Wrote(texture);
    }

    // The image a texture is sampled from: a target's, else the texture's own
    internal SharedImage? SampledFind(int texture) =>
        Assert(texture >= 0) ? Targets.Find(texture) ?? Images.Find(texture) : null;

    private GlFramebuffer.Attached[] Asked(int fbo)
    {
        if (!Assert(_attached.Count <= MaxTargets * 4)) return [];
        if (_attached.TryGetValue(fbo, out var attached)) return attached;
        using (GlTap.Quietly()) attached = GlFramebuffer.Attachments(fbo);
        return _attached[fbo] = attached;
    }

    // Ours: an engine framebuffer on Vulkan's images (made for a texture first if needed, Attachment). Foreign: a mod's
    // framebuffer (FluffyClouds' cloud map) on its textures' images, any level or layer(s), drawn via texture copies
    private VulkanFrame.Target? Assembled(int fbo, int[] buffers, bool readOnly, bool foreign, out string why)
    {
        why = "";
        _replaced = false;
        if (!NotNull(buffers) || !Assert(buffers.Length <= GlTap.MaxBuffers) || !Assert(fbo > 0)) return null;
        var attached = Asked(fbo);
        var outputs = Array.FindLastIndex(buffers, b => b >= 0) + 1;
        if (!Assert(attached.Length == GlFramebuffer.Colors + 1)) return null;
        var (images, views, at) = (new SharedImage?[outputs], new ulong[outputs], new (int Level, int Layer)[outputs]);
        var stands = foreign ? new List<int>() : null; // the textures a foreign target's images stand for
        var (width, height, layers) = (int.MaxValue, int.MaxValue, 0);
        for (var i = 0; i < Math.Min(outputs, GlTap.MaxBuffers); i++)
        {
            if (buffers[i] is < 0 or >= GlFramebuffer.Colors || attached[buffers[i]].Texture == 0) continue;
            var a = attached[buffers[i]];
            if (Attached(a, foreign, out why) is not { } image) return null;
            if (_replaced) return None<VulkanFrame.Target>(out why, "an attachment was replaced"); // names moved: ask again
            if (Viewed(image, a) is not var (view, w, h, l) || (layers > 0 && l != layers))
                return None<VulkanFrame.Target>(out why, "draws past the image's levels or layers, or into layers unalike");
            (images[i], views[i], at[i], layers) = (image, view, (a.Level, a.Layer), l);
            stands?.Add(a.Texture);
            (width, height) = (Math.Min(width, w), Math.Min(height, h));
        }

        SharedImage? depth = null;
        var depthView = 0UL;
        if (attached[GlFramebuffer.Depth] is { Texture: > 0 } d)
        {
            if (!foreign && d.Level > 0) return None<VulkanFrame.Target>(out why, "draws into a mipmap level of a depth");
            if (Attached(d, foreign, out why) is not { } image) return null;
            if (_replaced) return None<VulkanFrame.Target>(out why, "an attachment was replaced");
            if ((image.Format.Aspect & Vk.AspectDepth) == 0) return None<VulkanFrame.Target>(out why, "draws into a depth of a color format");
            if (Viewed(image, d) is not var (view, w, h, l) || (layers > 0 && l != layers))
                return None<VulkanFrame.Target>(out why, "draws into a depth past its levels or layers, or of other layers");
            (depth, depthView, layers) = (image, foreign ? view : 0, l); // ours: the image's own view, level 0
            stands?.Add(d.Texture);
            (width, height) = (Math.Min(width, w), Math.Min(height, h));
        }

        if (width == int.MaxValue) return None<VulkanFrame.Target>(out why, "a framebuffer with nothing attached");
        return new VulkanFrame.Target(++_targetKeys, images, depth, readOnly, width, height)
        {
            Views = views, At = at, Stands = stands is null ? [] : [.. stands], DepthView = depthView, Layers = Math.Max(layers, 1)
        };
    }

    // Ours: the target's image (Attachment); foreign: the texture's own, any level or layer(s)
    private SharedImage? Attached(GlFramebuffer.Attached attached, bool foreign, out string why) =>
        foreign ? Copy(attached.Texture, out why, stand: true) : Attachment(attached, out why);

    // An image made for the texture first if needed (TerrainTargets.Share)
    private SharedImage? Attachment(GlFramebuffer.Attached attached, out string why)
    {
        why = "";
        if (attached.Layered) return None<SharedImage>(out why, "draws into every layer at once");
        if (Targets.Find(attached.Texture) is { } known) return known;
        Frame.Close("a texture to stand for");
        SharedImage? image;
        using (GlTap.Quietly()) image = Targets.Share(attached.Texture, out why);
        ForgetTargets(); // the attachments changed under every target made
        _replaced = image is not null;
        if (image is not null)
            _logger?.Notification("Komet: Vulkan stands for texture {0} ({1}x{2}, {3} layers)", attached.Texture, image.Width,
                image.Height, image.Layers);
        else if (_refusedSwaps.Add(-attached.Texture))
            _logger?.Notification("Komet: Vulkan cannot draw into texture {0}: {1}", attached.Texture, why);
        return image ?? None<SharedImage>(out why, why.Length > 0 ? why : "no image for it");
    }

    private static T? None<T>(out string why, string reason) where T : class
    {
        why = reason;
        return null;
    }

    private TerrainDraw.Program? Textures(Program program, VulkanFrame.Target target,
        System.Func<int, Samplers.State> samplers, out string why)
    {
        why = "";
        _sampled.Clear();
        var names = program.Ported.Samplers;
        var count = Math.Min(names.Length, MaxSamplers);
        for (var i = 0; i < Math.Min(count, MaxSamplers); i++)
        {
            var sampler = names[i];
            var (gl, samplerObject) = GlTap.Unit(program.Unit(i));
            if (gl <= 0 && Unbound(sampler.Type, samplerObject) is { } blank)
            {
                _resolved[i] = blank;
                continue;
            }

            if (gl <= 0) return Missing(out why, program, sampler.Name, "the engine bound no texture to it");
            var image = SampledFind(gl) ?? Copy(gl, out why);
            if (image is null) return Missing(out why, program, sampler.Name, why);
            var state = samplerObject > 0 ? samplers(samplerObject) : Targets.Sampling(gl) ?? SamplingOf(gl);
            if (state is null) return Missing(out why, program, sampler.Name, $"texture {gl} has no known sampling");
            var layout = image == target.Depth ? LayoutDepthReadOnly : Vk.LayoutShaderRead;
            _resolved[i] = new TerrainDraw.Texture(image.Sampled, Sampling.Get(state.Value), layout);
            if (!_sampled.Contains(image)) _sampled.Add(image);
        }

        return program.Recorded(_resolved.AsSpan(0, count));
    }

    private static TerrainDraw.Program? Missing(out string why, Program program, string sampler,
        string reason)
    {
        why = Reason("{0} reads {1}: {2}", program.Name, sampler, reason);
        return null;
    }

    // Built on a builder and waited for: a draw left out would be missing from the picture
    private TerrainPipeline? Pipeline(Program program, TerrainPools.Pool pool, VulkanFrame.Target target, GlTap.Fixed state,
        out bool waiting)
    {
        waiting = false;
        if ((state.Caps & (uint)Refused) != 0 || !Assert(target.Colors.Length <= GlTap.MaxBuffers)) return null;
        for (var i = 0; i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
            if (!TerrainPipeline.Blendable(state.Blend[i]))
                return null;
        var formats = Formats(target, ref state);
        var key = new PipelineKey(program.Id, pool.Layout, formats.Key, state);
        return _pipelines.TryMade(key, out var made) ? made : Build(key, program, pool.Attributes, (state, formats.Target), out waiting);
    }

    // Separate method: the lambda's captures would otherwise allocate on every Pipeline call
    private TerrainPipeline? Build(PipelineKey key, Program program, TerrainPools.Attribute[] attributes,
        (GlTap.Fixed State, TerrainPipeline.Target Target) made, out bool waiting)
    {
        var device = Device;
        if (program.Sources.Length > 0 && !_pipelines.Knows(key)) // kept for the next start, which begins it ahead (Warm)
            ShaderCache.Remember(program.Sources, PipelineRecipes.Packed((PipelineRecipes.Terrain + key.Layout, key.Formats,
                Vk.TopologyTriangles), key.State, attributes, made.Target));
        return _pipelines.Get(key, (program.Name, program.Ported),
            ported => TerrainPipeline.Started(device, ported, attributes, (made.State, made.Target, Vk.TopologyTriangles)),
            out waiting); // a draw left out would be missing from the picture
    }

    // Pipelines an earlier start used, begun when the port is taken
    public void Warm(Program program)
    {
        if (!NotNull(program) || program.Sources.Length == 0) return;
        var (device, ported) = (Device, program.Ported);
        foreach (var recipe in ShaderCache.Recipes(program.Sources).Bounded(MaxPipelines))
        {
            if (!recipe.Inputs.StartsWith(PipelineRecipes.Terrain, StringComparison.Ordinal) ||
                PipelineRecipes.Unpacked(recipe) is not var (state, attributes, target)) continue; // the scene's
            var key = new PipelineKey(program.Id, recipe.Inputs[PipelineRecipes.Terrain.Length..], recipe.Target, state);
            _pipelines.Start(key, (program.Name, ported),
                p => TerrainPipeline.Started(device, p, attributes, (state, target, Vk.TopologyTriangles)));
        }
    }

    // Formats plus state with alpha writes masked for RGB colors (alpha reads 1); cached per target
    internal (TerrainPipeline.Target Target, string Key) Formats(VulkanFrame.Target target, ref GlTap.Fixed state)
    {
        if (!NotNull(target)) return (new TerrainPipeline.Target([], 0), "");
        if (!_formats.TryGetValue(target, out var formats))
        {
            uint[] colors = [.. target.Colors.Select(static c => c?.Format.Vulkan ?? 0)];
            var depth = target.Depth?.Format.Vulkan ?? 0;
            bool[]? opaque = target.Colors.Any(static c => c?.OpaqueAlpha == true)
                ? [.. target.Colors.Select(static c => c?.OpaqueAlpha == true)]
                : null;
            if (_formats.Count >= 2 * MaxTargets) _formats.Clear();
            _formats[target] = formats = (new TerrainPipeline.Target(colors, depth), $"{string.Join(',', colors)}/{depth}", opaque);
        }

        if (formats.Opaque is { } masked) state = GlTap.WithoutAlpha(state, masked);
        return (formats.Target, formats.Key);
    }

    private bool Leave(string why)
    {
        if (NotNull(why)) Miss(why);
        return !Assert(why.Length > 0) && !Assert(Missed > 0);
    }
}
