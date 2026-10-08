using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// A draw of a deferred pass that does not commute with OpenGL's work (it writes color, samples a shared image, tests other
// than LESS or LEQUAL) is left to OpenGL after the open segment closed: taken as ordered instead, OpenGL's calls between its
// signal and the segment's end (a shadow map cleared) would race Vulkan's.
internal sealed partial class TerrainRenderer
{
    private const int MaxRanges = 1 << 16, MaxSamplers = 32, LayoutDepthReadOnly = 4;
    private const GlTap.Caps Refused = GlTap.Caps.Stencil | GlTap.Caps.Discard | GlTap.Caps.AlphaToCoverage |
                                       GlTap.Caps.LogicOp;

    private readonly int[] _starts = new int[MaxRanges];
    private readonly List<SharedImage> _sampled = [];
    private Context? _context;

    // What the last draw was prepared with, taken again while OpenGL's state, the program's samplers, the pool's formats and the
    // frame's rendering are the same: the draws of one pass differ in their pool and uniforms only
    private sealed record Context(long Version, Program Program, int Samplers, string Layout, long Renderings,
        TerrainPipeline Pipeline, TerrainDraw.Program Recorded, SegmentSync Sync);

    public long TakeTicks { get; private set; } // probe: the time the draws took to record

    // Another recorder (the scene) posted to the frame: the next pool draw prepares afresh
    public void Unprepared()
    {
        _context = null;
        _ = Assert(Frame.Number >= 0) && Assert(_targets.Count <= MaxTargets);
    }

    public long Counted { get; private set; } // OcclusionCulling's draws taken

    // The ranges as the engine hands them to MultiDrawElements: the starts are pointers, two ints each
    public bool Take(VAO vao, (int[] Indices, int[] Sizes, int Count, bool Ssbo) draw, SegmentSync sync,
        System.Func<int, Samplers.State> samplers)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var taken = Taken(vao, draw, sync, samplers);
        if (!taken) Yield("a draw left to OpenGL");
        TakeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
        return taken;
    }

    // OpenGL draws next, into what Vulkan's work may hold: an ordered segment closes first, so OpenGL's draw lands after it; a
    // deferred one stays open, as whatever OpenGL draws beside it must commute with it
    public void Yield(string why)
    {
        if (Frame.Open && !Frame.Pending && Assert(why.Length > 0) && Assert(Frame.Number > 0)) Frame.Close(why);
    }

    public bool TakeCounted(VAO vao, bool ssbo, ((ulong, ulong) Commands, (ulong, ulong) Count, int Max) counted,
        System.Func<int, Samplers.State> samplers)
    {
        if (!Assert(counted.Max >= 0) || !NotNull(vao)) return false;
        _counted = counted;
        try
        {
            return Take(vao, ([], [], 0, ssbo), SegmentSync.Ordered, samplers);
        }
        finally
        {
            _counted = null;
        }
    }

    private ((ulong Buffer, ulong Offset) Commands, (ulong Buffer, ulong Offset) Count, int Max)? _counted;

    private bool Taken(VAO vao, (int[] Indices, int[] Sizes, int Count, bool Ssbo) draw, SegmentSync sync,
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
            c.Samplers == program.Mirror.Samplers && c.Layout == pool.Layout && c.Renderings == Frame.Renderings &&
            Frame.Open && (c.Sync == SegmentSync.Deferred || !Frame.Pending))
            return Recorded(c, pool, draw);
        _context = null;
        var state = GlTap.State;
        var target = Target(state, out why, foreign: true); // Komet's distant shadow map: through the copy of its depth
        if (target is null) return Leave(why);
        var recorded = Textures(program, target, samplers, out why);
        if (recorded is null) return Leave(why);
        var pipeline = Pipeline(program, pool, target, state, out var waiting);
        if (pipeline is null) return !waiting && Leave($"no pipeline for {program.Name} in this state (see the log)");
        if (sync == SegmentSync.Deferred && !Commuting(target, state, _sampled))
        {
            Frame.Close("a draw that does not commute");
            return Leave($"a {program.Name} draw that does not commute with OpenGL's");
        }

        if (!Segment(target, sync, _sampled, out why)) return Leave(why);
        Frame.Viewport(GlTap.Viewport);
        Frame.Scissor((state.Caps & (uint)GlTap.Caps.Scissor) != 0 ? GlTap.Scissor : null); // a tile of the distant map
        var context = new Context(GlTap.Version, program, program.Mirror.Samplers, pool.Layout, Frame.Renderings, pipeline,
            recorded, sync);
        Frame.Wrote(target, sync == SegmentSync.Deferred);
        Drawn(target);
        if (!Recorded(context, pool, draw)) return false;
        _context = context;
        return true;
    }

    private bool Recorded(Context context, TerrainPools.Pool pool, (int[] Indices, int[] Sizes, int Count, bool Ssbo) draw)
    {
        if (!NotNull(context) || !Assert(draw.Count <= MaxRanges) || Frame.Uploads is not { } uploads) return false;
        for (var i = 0; i < Math.Min(draw.Count, MaxRanges); i++) _starts[i] = draw.Indices[2 * i];
        var recorded = _counted is { } counted
            ? Recorder.Counted(Frame, uploads, (context.Pipeline, pool), context.Recorded,
                (draw.Ssbo, counted.Commands, counted.Count, counted.Max))
            : Recorder.Draw(Frame, uploads, (context.Pipeline, pool), context.Recorded,
                (_starts, draw.Sizes, draw.Count, draw.Ssbo));
        if (!recorded)
        {
            _context = null;
            return Leave("the recorder refused the draw (a descriptor without its resource, or the uploads full)");
        }

        if (_counted is not null) Counted++;
        if (_intoOwn) OwnDraws++;
        Draws++;
        return true;
    }

    // A pool not adopted in Prepare (made since): adopting copies it with OpenGL, so an open ordered segment closes first
    private TerrainPools.Pool? Adopted(VAO vao, bool classic, out string why)
    {
        if (Frame.Open && !Frame.Pending) Frame.Close("a pool to adopt");
        using var quiet = GlTap.Quietly();
        var pool = Pools.Adopt(vao, classic, out why);
        _ = Assert(pool is not null || why.Length > 0) && Assert(!Frame.Open || Frame.Pending);
        _context = null;
        return pool;
    }

    // A depth-only draw that tests and writes the nearest depth, reading no image a framebuffer holds: the same before or after
    // OpenGL's depth-only draws into the same depth, so it may wait in a segment OpenGL has not signalled yet
    internal bool Commuting(VulkanFrame.Target target, GlTap.Fixed state, List<SharedImage> sampled)
    {
        if (!NotNull(target) || target.Depth is null || target.DepthReadOnly || !target.DepthOnly)
            return false;
        if (state.DepthCompare is not (1 or 3)) return false;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (Targets.Find(image.Texture) is not null)
                return false;
        return true;
    }

    // One opened before an image was new closes first. Begin keeps an open segment's images, so what it held it still holds.
    // The images sampled are marked used in this frame: no copy of them makes room for another in it. render: the target's
    // rendering begins (a blit moves the images itself), while Owning on Komet's depth (Owned); whole: with a clear of every
    // texel of the depth, which then needs no copy in.
    public bool Segment(VulkanFrame.Target target, SegmentSync sync, List<SharedImage> sampled, out string why,
        bool render = true, bool whole = false)
    {
        why = "";
        if (!NotNull(target) || !NotNull(sampled) || !Assert(sampled.Count <= MaxSamplers)) return false;
        foreach (var image in sampled.Bounded(MaxSamplers)) image.Used = Frame.Number;
        var into = render ? Current(target) : target;
        if (!Touching(into, sampled)) return Refuse(out why, "the draw samples the depth it draws into");
        var owning = render && ReferenceEquals(into, target) && Ownable(target); // Komet's depth to take over: not Again
        if (!owning && Again(into, sync, sampled)) return Into(into, target);
        if (!Frame.Open && Copies.Stale) Refreshed();
        var held = Frame.Open && Held(into, sampled);
        if (Frame.Open && !held) Frame.Close("an image new since the segment opened"); // Komet's depth went back as it closed
        if (!held) into = render ? Current(target) : target;
        if (!Frame.Begin(sync, out why)) return false;
        if (!held && !Held(into, sampled)) return Refuse(out why, "an image the draw uses is not shared");
        if (!render) return Assert(Frame.Open);
        if (ReferenceEquals(into, target)) into = Owned(target, whole);
        if (!Frame.Prepare(into, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sampled)))
            return Refuse(out why, "the draw samples what it draws into");
        Prepared(into, sync, sampled);
        return Into(into, target);
    }

    private bool Into(VulkanFrame.Target into, VulkanFrame.Target target)
    {
        _intoOwn = !ReferenceEquals(into, target);
        return Assert(Frame.Open) && Assert(!_intoOwn || ReferenceEquals(into.Depth, _own));
    }

    private const int MaxPrepared = 64;

    // The segment, rendering, target and sync Prepare last ran for, and the images sampled through it since: each was held and
    // moved for sampling then, and stays so while the rendering does (every layout move ends it first, which counts)
    private (long Serial, long Renderings, VulkanFrame.Target? Target, SegmentSync Sync, int Count) _prepared;
    private readonly SharedImage[] _preparedImages = new SharedImage[MaxPrepared];

    private void Prepared(VulkanFrame.Target target, SegmentSync sync, List<SharedImage> sampled)
    {
        var (serial, renderings, last, was, count) = _prepared;
        var same = serial == Frame.Serial && renderings == Frame.Renderings && ReferenceEquals(last, target) && was == sync;
        if (!same || count + sampled.Count > MaxPrepared) count = 0;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (Array.IndexOf(_preparedImages, image, 0, count) < 0)
                _preparedImages[count++] = image;
        _prepared = (Frame.Serial, Frame.Renderings, target, sync, count);
        _ = Assert(count <= MaxPrepared) && Assert(Frame.Open);
    }

    // Into the target Prepare last ran for, sampling only images sampled through it since, in the same segment and rendering:
    // Begin, Held and Prepare would change nothing
    private bool Again(VulkanFrame.Target target, SegmentSync sync, List<SharedImage> sampled)
    {
        var (serial, renderings, last, was, count) = _prepared;
        if (!Frame.Open || serial != Frame.Serial || renderings != Frame.Renderings || !ReferenceEquals(last, target) ||
            was != sync || Frame.Fault.Length > 0 || (sync == SegmentSync.Ordered && Frame.Pending)) return false;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (Array.IndexOf(_preparedImages, image, 0, count) < 0)
                return false;
        return Assert(count <= MaxPrepared);
    }

    // Copies changed while no segment was open (an upload with none open) are refreshed before one opens, as a draw sampling
    // one would refresh them all anyway, but only after closing the segment it opened
    private void Refreshed()
    {
        using var quiet = GlTap.Quietly();
        Copies.Refresh(Frame.Number);
        if (Copies.PacksWaiting) _ = Frame.Late();
        _ = Assert(!Frame.Open) && Assert(!Copies.Stale);
    }

    private bool Held(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        if (!NotNull(target) || !Assert(Frame.Open)) return false;
        foreach (var image in sampled.Bounded(MaxSamplers))
            if (!Frame.Holds(image))
                return false;
        foreach (var color in target.Colors.Bounded(GlTap.MaxBuffers))
            if (color is not null && !Frame.Holds(color))
                return false;
        return target.Depth is null || Frame.Holds(target.Depth);
    }

    private bool Refuse(out string why, string reason)
    {
        _ = Assert(reason.Length > 0) && Assert(_sampled.Count <= MaxSamplers);
        why = reason;
        return false;
    }

    // readOnly: the depth is only tested, not written; foreign: a framebuffer object that is not the engine's is drawn into
    // through copies of its textures (the scene's)
    public VulkanFrame.Target? Target(GlTap.Fixed state, out string why, int into = -1, bool? depthReadOnly = null,
        bool foreign = false)
    {
        why = "";
        var fbo = into >= 0 ? into : GlTap.DrawFramebuffer;
        var readOnly = depthReadOnly ?? (!state.DepthTest || !state.DepthWrite);
        if (_lastTarget is (var lastFbo, var changes, var lastReadOnly, { } last) && lastFbo == fbo &&
            changes == GlTap.BufferChanges && lastReadOnly == readOnly) return last;
        var ours = Ours(fbo);
        if (!ours && (!foreign || fbo == 0))
            return None<VulkanFrame.Target>(out why, "draws into a framebuffer that is not shared");
        var buffers = GlTap.DrawBuffersOf(fbo);
        var key = (fbo, Packed(buffers), readOnly);
        if (_targets.TryGetValue(key, out var made))
        {
            _lastTarget = (fbo, GlTap.BufferChanges, readOnly, made);
            return made;
        }
        if (!ours) return Remembered(key, Foreign(fbo, buffers, readOnly, out why), readOnly);
        made = Made(fbo, buffers, readOnly, out why);
        for (var again = 0; again < GlFramebuffer.Colors && made is null && _replaced; again++)
            made = Made(fbo, buffers, readOnly, out why); // asked anew after each replacement
        return Remembered(key, made, readOnly);
    }

    // Five bits per output: its attachment plus one (DrawBuffersOf gives -1 to 15)
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
        if (made is null || !Assert(key.Fbo > 0)) return null;
        if (_targets.Count >= MaxTargets) ForgetTargets();
        _targets[key] = made;
        _lastTarget = (key.Fbo, GlTap.BufferChanges, readOnly, made);
        return made;
    }

    // A mod's framebuffer object (FluffyClouds' cloud map): the copies of its level-0 2D attachments as the target
    private VulkanFrame.Target? Foreign(int fbo, int[] buffers, bool readOnly, out string why)
    {
        why = "";
        if (!NotNull(buffers) || !Assert(buffers.Length <= GlTap.MaxBuffers) || !Assert(fbo > 0)) return null;
        var attached = Asked(fbo);
        var outputs = Array.FindLastIndex(buffers, b => b >= 0) + 1;
        var (images, stands) = (new SharedImage?[outputs], new List<int>());
        for (var i = 0; i < Math.Min(outputs, GlTap.MaxBuffers); i++)
        {
            if (buffers[i] is < 0 or >= GlFramebuffer.Colors || attached[buffers[i]].Texture == 0) continue;
            if (Stand(attached[buffers[i]], out why) is not { } image) return null;
            (images[i], stands) = (image, [.. stands, attached[buffers[i]].Texture]);
        }

        SharedImage? depth = null;
        if (attached[GlFramebuffer.Depth] is { Texture: > 0 } d && (depth = Stand(d, out why)) is null) return null;
        if (depth is not null) stands.Add(attached[GlFramebuffer.Depth].Texture);
        var sized = images.Append(depth).OfType<SharedImage>().ToArray();
        if (sized.Length == 0) return None<VulkanFrame.Target>(out why, "a framebuffer with nothing attached");
        return new VulkanFrame.Target(++_targetKeys, images, depth, readOnly, sized.Min(s => s.Width),
            sized.Min(s => s.Height)) { Views = new ulong[outputs], Stands = [.. stands] };
    }

    // The image a blit reads from the framebuffer's attachment (GlFramebuffer.Depth for the depth): the shared one, or in a
    // framebuffer of no one Vulkan knows, the texture's copy
    public SharedImage? Read(int fbo, int attachment, out string why)
    {
        why = "";
        if (!Assert(fbo > 0) || !Index(attachment, GlFramebuffer.Colors + 1)) return null;
        var attached = Asked(fbo)[attachment];
        if (attached.Texture == 0) return None<SharedImage>(out why, "reads no attachment");
        if (attached.Level != 0 || attached.Layered || attached.Layer > 0)
            return None<SharedImage>(out why, "reads a layer or a mipmap level");
        if (Ours(fbo)) return Attachment(attached, out why);
        return GlTap.TargetOf((uint)attached.Texture) == 0x0DE1
            ? Copy(attached.Texture, out why)
            : None<SharedImage>(out why, "reads no 2D texture");
    }

    private SharedImage? Stand(GlFramebuffer.Attached attached, out string why)
    {
        _ = Assert(attached.Texture > 0) && Assert(attached.Level >= 0);
        if (attached.Level != 0 || attached.Layered || attached.Layer > 0 ||
            GlTap.TargetOf((uint)attached.Texture) != 0x0DE1)
            return None<SharedImage>(out why, "draws into a layer, a mipmap level or no 2D texture");
        return Copy(attached.Texture, out why, stand: true);
    }

    // Vulkan drew into the target: the copies of a foreign framebuffer are ahead of their GL textures now
    public void Drawn(VulkanFrame.Target target)
    {
        if (!NotNull(target) || !Assert(Frame.Open) || target.Stands.Length == 0) return;
        foreach (var texture in target.Stands.Bounded(GlFramebuffer.Colors + 1)) Copies.Wrote(texture);
        _ = Assert(target.Stands.Length <= GlFramebuffer.Colors + 1);
    }

    // OpenGL is about to read or write the texture: what Vulkan drew into its copy goes back first, after the open segment
    // (which may still draw into the copy) closed
    public void WriteBack(int texture)
    {
        if (!Assert(texture >= 0) || !Copies.Written(texture)) return;
        Frame.Close("OpenGL reads what Vulkan drew for it");
        using var quiet = GlTap.Quietly();
        Copies.WriteBack(texture);
    }

    private (int Fbo, long Changes, bool ReadOnly, VulkanFrame.Target? Target) _lastTarget;

    private GlFramebuffer.Attached[] Asked(int fbo)
    {
        if (!Assert(fbo > 0) || !Assert(_attached.Count <= MaxTargets * 4)) return [];
        if (_attached.TryGetValue(fbo, out var attached)) return attached;
        using (GlTap.Quietly()) attached = GlFramebuffer.Attachments(fbo);
        return _attached[fbo] = attached;
    }

    private (int[] Colors, int Depth) Attachments(int fbo)
    {
        if (!Assert(fbo > 0) || !Assert(_attached.Count <= MaxTargets * 4)) return ([], 0);
        if (_textures.TryGetValue(fbo, out var known)) return known;
        var attached = Asked(fbo);
        known = ([.. attached.Take(GlFramebuffer.Colors).Select(a => a.Texture)], attached[GlFramebuffer.Depth].Texture);
        _textures[fbo] = known;
        return known;
    }

    private readonly Dictionary<int, (int[] Colors, int Depth)> _textures = [];

    private VulkanFrame.Target? Made(int fbo, int[] buffers, bool readOnly, out string why)
    {
        why = "";
        _replaced = false;
        if (!NotNull(buffers) || !Assert(buffers.Length <= GlTap.MaxBuffers)) return null;
        var attached = Asked(fbo);
        var outputs = Array.FindLastIndex(buffers, b => b >= 0) + 1;
        if (!Assert(outputs <= GlTap.MaxBuffers) || !Assert(attached.Length == GlFramebuffer.Colors + 1)) return null;
        var (images, views) = (new SharedImage?[outputs], new ulong[outputs]);
        var (width, height) = (int.MaxValue, int.MaxValue);
        for (var i = 0; i < Math.Min(outputs, GlTap.MaxBuffers); i++)
        {
            if (buffers[i] is < 0 or >= GlFramebuffer.Colors || attached[buffers[i]].Texture == 0) continue;
            if (Attachment(attached[buffers[i]], out why) is not { } image) return null;
            if (_replaced) return None<VulkanFrame.Target>(out why, "an attachment was replaced"); // names moved: ask again
            (images[i], views[i]) = (image, image.Layers > 1 ? image.LayerView(attached[buffers[i]].Layer) : 0);
            (width, height) = (Math.Min(width, image.Width), Math.Min(height, image.Height));
        }

        SharedImage? depth = null;
        if (attached[GlFramebuffer.Depth].Texture != 0 &&
            (depth = Attachment(attached[GlFramebuffer.Depth], out why)) is null) return null;
        if (_replaced) return None<VulkanFrame.Target>(out why, "an attachment was replaced");
        if (depth is not null) (width, height) = (Math.Min(width, depth.Width), Math.Min(height, depth.Height));
        if (width == int.MaxValue) return None<VulkanFrame.Target>(out why, "a framebuffer with nothing attached");
        return new VulkanFrame.Target(++_targetKeys, images, depth, readOnly, width, height) { Views = views };
    }

    private readonly Dictionary<int, GlFramebuffer.Attached[]> _attached = [];
    private bool _replaced; // an attachment was replaced while a target was made: the names it had read may be stale

    // The shared image of an attachment, the texture replaced by one first if it has none (its owner told the new name).
    // OpenGL's work, so the open segment goes to the GPU before it
    private SharedImage? Attachment(GlFramebuffer.Attached attached, out string why)
    {
        why = "";
        if (attached.Level != 0) return None<SharedImage>(out why, "draws into a mipmap level");
        if (attached.Layered) return None<SharedImage>(out why, "draws into every layer at once");
        if (Targets.Find(attached.Texture) is { } known) return known;
        Frame.Close("a texture to share");
        SharedImage? image;
        using (GlTap.Quietly()) image = Targets.Share(attached.Texture, out why);
        ForgetTargets(); // the attachments changed under every target made
        _replaced = image is not null;
        if (image is not null)
            _logger?.Notification("Komet: Vulkan shares texture {0} ({1}x{2}, {3} layers)", attached.Texture, image.Width,
                image.Height, image.Layers);
        else if (why != TerrainTargets.Waits && _refusedSwaps.Add(-attached.Texture))
            _logger?.Notification("Komet: Vulkan leaves drawing into texture {0} to OpenGL: {1}", attached.Texture, why);
        return image ?? None<SharedImage>(out why, why.Length > 0 ? why : "no shared image for it");
    }

    private int _targetKeys;

    private static T? None<T>(out string why, string reason) where T : class
    {
        why = reason;
        _ = Assert(reason.Length > 0) && Assert(why.Length > 0);
        return null;
    }

    private TerrainDraw.Program? Textures(Program program, VulkanFrame.Target target,
        System.Func<int, Samplers.State> samplers, out string why)
    {
        why = "";
        _sampled.Clear();
        if (!NotNull(program) || !NotNull(target)) return null;
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
            var image = Targets.Find(gl) ?? Copy(gl, out why);
            if (image is null) return Missing(out why, program, sampler.Name, why);
            var state = samplerObject > 0 ? samplers(samplerObject) : Targets.Sampling(gl) ?? Copies.SamplingOf(gl);
            if (state is null) return Missing(out why, program, sampler.Name, $"texture {gl} has no known sampling");
            var layout = image == target.Depth ? LayoutDepthReadOnly : Vk.LayoutShaderRead;
            _resolved[i] = new TerrainDraw.Texture(image.Sampled, Sampling.Get(state.Value), layout);
            if (!_sampled.Contains(image)) _sampled.Add(image);
        }

        return program.Recorded(_resolved.AsSpan(0, count));
    }

    private const int MaxSets = 1024;
    private readonly TerrainDraw.Texture[] _resolved = new TerrainDraw.Texture[MaxSamplers];

    // A texture no framebuffer holds, as its copy. Filling a new one is OpenGL's work: beside a deferred segment, which takes
    // the copy over in its setup, else between segments - a private copy's fill also after the open segment's signal, which
    // OpenGL then gives once more as the segment closes; refreshing one OpenGL changed since, after the open segment (which may
    // have read the old contents) closed. stand: Vulkan draws into it, so it must be exported.
    public SharedImage? Copy(int texture, out string why, bool stand = false)
    {
        why = "";
        if (!Assert(texture > 0)) return null;
        if (stand && Copies.Movable(texture)) Exporting(texture);
        var (known, dirty) = (Copies.Has(texture), Copies.Dirty(texture));
        if (known && !dirty) return Copies.Get(texture, out why, frame: Frame.Number);
        var seeded = !known && !stand && !Copies.Exports(texture, false) && Seedable(texture); // no OpenGL work for it
        if (Frame.Open && (dirty || (!seeded && !Frame.Pending && (Copies.Exports(texture, stand) || !Frame.Late()))))
            Frame.Close(dirty ? $"copied texture {texture} changed" : "a texture to copy");
        using var quiet = GlTap.Quietly();
        var copy = Copies.Get(texture, out why, stand, Frame.Number);
        if (copy is null) return null;
        if (seeded) _ = Seeded(texture, copy);
        if (!Frame.Open) Copies.Refresh(Frame.Number);
        else if (!Copies.Refresh(texture, Frame.Number, out var refused))
            return None<SharedImage>(out why, refused.Length > 0 ? refused : $"texture {texture} not copied");
        if (Copies.Dirty(texture)) return None<SharedImage>(out why, $"texture {texture} not copied: no staging");
        if (Copies.PacksWaiting) _ = Frame.Late();
        Filled();
        if (Frame.Open) _ = Frame.Take(new VulkanFrame.Shared(copy, Vk.LayoutShaderRead, GlInterop.LayoutShaderRead));
        return copy;
    }

    // A private copy a framebuffer of the scene's own draws into: made again exported, as OpenGL takes back what Vulkan drew
    private void Exporting(int texture)
    {
        Frame.Close("a copy to export");
        Copies.Forget(texture, Frame.Number);
        _ = Assert(!Copies.Has(texture)) && Assert(!Frame.Open);
    }

    private static TerrainDraw.Program? Missing(out string why, Program program, string sampler,
        string reason)
    {
        why = $"{program.Name} reads {sampler}: {reason}";
        _ = Assert(why.Length > 0) && NotNull(program);
        return null;
    }

    // Made on a builder; meanwhile the pool stays OpenGL's (waiting), but a counted draw, which OpenGL would not draw,
    // waits for it
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

    // Its own method: the lambda's captures would otherwise be allocated on every call of Pipeline, found or not
    private TerrainPipeline? Build(PipelineKey key, Program program, TerrainPools.Attribute[] attributes,
        (GlTap.Fixed State, TerrainPipeline.Target Target) made, out bool waiting)
    {
        var device = Device;
        _ = Assert(made.Target.Colors.Length <= GlTap.MaxBuffers) && NotNull(program);
        return _pipelines.Get(key, (program.Name, program.Ported),
            ported => TerrainPipeline.Started(device, ported, attributes, (made.State, made.Target, Vk.TopologyTriangles)),
            _counted is not null, out waiting);
    }

    public long PipelineWaits => _pipelines.Waits;

    // The target's formats as a pipeline is made for them, and the state with the alpha writes masked where a color is RGB
    // (alpha reads 1): worked out once per target, as a pool's draw asks again whenever OpenGL's state changed
    internal (TerrainPipeline.Target Target, string Key) Formats(VulkanFrame.Target target, ref GlTap.Fixed state)
    {
        if (!NotNull(target)) return (new TerrainPipeline.Target([], 0), "");
        if (!ReferenceEquals(_formats.Of, target))
        {
            uint[] colors = [.. target.Colors.Select(c => c?.Format.Vulkan ?? 0)];
            var depth = target.Depth?.Format.Vulkan ?? 0;
            bool[]? opaque = target.Colors.Any(c => c?.OpaqueAlpha == true)
                ? [.. target.Colors.Select(c => c?.OpaqueAlpha == true)]
                : null;
            _formats = (target, new TerrainPipeline.Target(colors, depth), $"{string.Join(',', colors)}/{depth}", opaque);
        }

        if (_formats.Opaque is { } masked) state = GlTap.WithoutAlpha(state, masked);
        _ = Assert(_formats.Target.Colors.Length == target.Colors.Length);
        return (_formats.Target, _formats.Key);
    }

    private (VulkanFrame.Target? Of, TerrainPipeline.Target Target, string Key, bool[]? Opaque) _formats;

    private bool Leave(string why)
    {
        Left++;
        if (NotNull(why) && why.Length > 0 && Reasons.Count < 16 && Reasons.Add(why))
            _logger?.Warning("Komet: Vulkan terrain leaves a pool to OpenGL: {0}", why);
        _ = Assert(Left > 0);
        return false;
    }
}
