namespace Komet.Vulkan;

// The opaque terrain's draws and the shadow passes' (terrain, entities, their clears) test and write a depth image of Komet's
// own instead of the engine's. The engine's depth lives in memory shared with OpenGL, which RADV gives no depth compression
// (HTILE): an alpha-tested draw that writes depth then gets no early depth test, and every fragment is shaded however far
// behind, each layer of foliage (TerrainBenchGpuTests: 54 fragments a pixel against 9). Komet's image is the device's own, so
// what lies behind is rejected before it is shaded. While Owning (RenderOpaque, the shadow stages: VulkanRenderer) a rendering
// into an engine depth begins on Komet's image of its size and format, the engine's copied in first - not at all when the
// rendering begins with a clear of every texel - and it goes back before anything else may read or write the engine's: the
// segment's end, a draw, clear or blit into it or from it that is not Owning's (Segment), another depth taken over, the end of
// RenderOpaque. Compute that reads the engine's depth meanwhile (OcclusionCulling's pyramid) reads Komet's (OwnFind). The same
// pipelines, draws, order and tests on the same depth values, of the same format: the same pixels and depth.
internal sealed partial class TerrainRenderer
{
    private const int MaxOwn = 4, OwnIdle = 600; // images of other sizes (the screen's, the maps'); frames unused before one goes

    private readonly List<SharedImage> _owns = [];
    private SharedImage? _own, _ownFor; // Komet's image holding the newer depth, the engine's depth it holds it for
    private readonly Dictionary<VulkanFrame.Target, VulkanFrame.Target> _ownTargets = new(ReferenceEqualityComparer.Instance);
    private long _ownVersion;
    private bool _intoOwn; // the rendering Segment last prepared is on Komet's depth

    // Renderings into an engine's depth go on Komet's (a draw prepared before is prepared anew)
    public bool Owning
    {
        get => _owning;
        set
        {
            if (value != _owning) _context = null;
            _owning = value;
        }
    }

    private bool _owning;

    // Pool draws into Komet's depth, copies into and back from it, clears that took it over without a copy in
    public long OwnDraws { get; private set; }
    public long OwnCopies { get; private set; }
    public long OwnClears { get; private set; }

    // A depth only written by whole renderings: the engine's single 2D one, drawn into as it is (not through a copy)
    private bool Ownable(VulkanFrame.Target target) =>
        Owning && target.Depth is { Exported: true, Levels: 1, Layers: 1 } && !target.DepthReadOnly &&
        target.Stands.Length == 0 && Assert(target.Width > 0);

    // The target with Komet's depth while that holds the newer depth of the target's
    private VulkanFrame.Target Current(VulkanFrame.Target target) =>
        _ownFor is { } depth && ReferenceEquals(target.Depth, depth) && Ownable(target) ? OwnTarget(target) : target;

    // In Segment, the segment open and holding the target: Komet's depth takes the engine's over, the engine's copied in unless
    // whole (a clear of every texel follows); the engine's own target when not Owning or no image could be had
    private VulkanFrame.Target Owned(VulkanFrame.Target target, bool whole) =>
        Ownable(target) && Assert(Frame.Open) && CopiedIn(target.Depth!, whole) ? OwnTarget(target) : target;

    private VulkanFrame.Target OwnTarget(VulkanFrame.Target target)
    {
        if (_ownTargets.TryGetValue(target, out var known)) return known;
        var owned = new VulkanFrame.Target(++_targetKeys, target.Colors, _own, false, target.Width, target.Height)
        {
            Views = target.Views
        };
        if (_ownTargets.Count >= MaxTargets) _ownTargets.Clear();
        _ownTargets[target] = owned;
        return Assert(_own is not null) && NotNull(owned) ? owned : target;
    }

    private bool CopiedIn(SharedImage depth, bool whole)
    {
        WriteBack();
        if (OwnImage(depth) is not { } own || !Frame.Holds(own)) return false;
        if (!whole && !Frame.Blit([(depth, own)], Whole(depth), false)) return false;
        (_ownFor, _own, _context, own.Used) = (depth, own, null, Frame.Number);
        if (whole) OwnClears++;
        else OwnCopies++;
        return Assert(Frame.Open) && Assert(own.Format == depth.Format);
    }

    // The newer depth back into the engine's, before anything else may touch it; Komet's image is not used again until copied in
    private void WriteBack()
    {
        if (_ownFor is not { } depth || _own is not { } own) return;
        _ownFor = null;
        _context = null;
        if (!Assert(Frame.Open) || !Frame.Blit([(own, depth)], Whole(depth), false)) return; // the frame lost them: faulted
        OwnCopies++;
        _ = Assert(OwnCopies > 0);
    }

    // The end of RenderOpaque
    public void Disown()
    {
        WriteBack();
        Owning = false;
        _ = Assert(_ownFor is null);
    }

    // Komet's image of the engine depth's size and format; null when MaxOwn others of other sizes are in use
    private SharedImage? OwnImage(SharedImage depth)
    {
        foreach (var known in _owns.Bounded(MaxOwn))
            if (known.Width == depth.Width && known.Height == depth.Height && known.Format == depth.Format)
                return known;
        if (_owns.Count >= MaxOwn) return null;
        var made = SharedImage.Private(Device, (depth.Width, depth.Height, 1, 1), depth.Format,
            Vk.DepthAttachment | Vk.Sampled | Vk.TransferSrc | Vk.TransferDst, out _);
        var handle = made?.Image ?? 0;
        if (made is null || !Device.Run(commands => Rested(commands, handle), []))
        {
            made?.Dispose();
            return null;
        }

        _owns.Add(made);
        _ownVersion++;
        if (Frame.Open) _ = Frame.Take(new VulkanFrame.Shared(made, Vk.LayoutDepthAttachment, 0));
        return made;
    }

    // As a frame starts, no segment open: an image no rendering used for OwnIdle frames goes (the window resized, the shadow
    // maps' size changed), the GPU long done with it
    private void CollectOwn()
    {
        _ = Assert(!Frame.Open) && Assert(_ownFor is null);
        for (var n = 0; n < MaxOwn; n++)
        {
            var i = _owns.Count - 1 - n;
            if (i < 0 || _owns[i].Used >= Frame.Done - OwnIdle) continue;
            _owns[i].Dispose();
            _owns.RemoveAt(i);
            _ownTargets.Clear();
            (_own, _ownVersion) = (null, _ownVersion + 1);
        }
    }

    private static Vk.ImageBlit Whole(SharedImage image) =>
        Assert(image.Width > 0)
            ? new Vk.ImageBlit
            {
                SourceX1 = image.Width, SourceY1 = image.Height, TargetX1 = image.Width, TargetY1 = image.Height
            }
            : default;

    private static unsafe void Rested(IntPtr commands, ulong image)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(image != 0)) return;
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, DstAccess = Vk.AccessMemoryRead | Vk.AccessMemoryWrite,
            OldLayout = Vk.LayoutUndefined, NewLayout = Vk.LayoutDepthAttachment, SrcFamily = Vk.QueueFamilyIgnored,
            DstFamily = Vk.QueueFamilyIgnored, Image = image,
            Range = new Vk.ColorRange { Aspect = Vk.AspectDepth, Levels = 1, Layers = 1 }
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.StageAll, 0, 0, null, 0, null, 1, &barrier);
    }

    // Before a segment's rendering, blit or copy: the engine's depth Komet's image holds the newer depth of goes back when it is
    // drawn into other than on Komet's or read. False for a draw into Komet's image sampling the engine's depth it stands for.
    private bool Touching(VulkanFrame.Target target, List<SharedImage> sampled)
    {
        if (_ownFor is not { } depth || !NotNull(target)) return true;
        var read = sampled.Contains(depth);
        if (read && ReferenceEquals(target.Depth, _own)) return false;
        if (read || ReferenceEquals(target.Depth, depth)) WriteBack();
        return Assert(sampled.Count <= MaxSamplers);
    }

    // What compute reads for an engine texture: Komet's image while it holds the newer depth
    internal SharedImage? OwnFind(int texture)
    {
        var found = Targets.Find(texture);
        return found is not null && ReferenceEquals(found, _ownFor) && Assert(_own is not null) ? _own : found;
    }

    private List<VulkanFrame.Shared> OwnShared()
    {
        _ = Assert(_owns.Count <= MaxOwn);
        return [.. _owns.Select(o => new VulkanFrame.Shared(o, Vk.LayoutDepthAttachment, 0))];
    }
}
