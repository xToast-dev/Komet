using System.Runtime.InteropServices;

namespace Komet.Vulkan;

// A rendering continues while draws keep the target and move no image; barriers between renderings order memory.
internal sealed unsafe partial class VulkanFrame
{
    private const int LayoutDepthReadOnly = 4, MaxColors = 8;

    private readonly List<Shared> _held = [];
    private readonly Dictionary<SharedImage, int> _layouts = [];
    private readonly List<(SharedImage Image, int To)> _moves = [];
    private readonly List<int> _moved = []; // in _held, the images whose layout a barrier changed
    private Rest _rest = new(null, [], []);
    private bool _restless = true; // _held and _layouts are not the list's: made anew at the next segment
    private Target? _rendering;
    private (int X, int Y, int Width, int Height) _viewport, _scissor;
    private bool _rendered; // whether a rendering ended in this segment, so the next one needs a barrier
    private ulong _showing; // the swapchain image's semaphore the next submission signals (Show)

    // Key names the framebuffer, so a target is the same as the rendering under way without comparing its images
    public sealed record Target(int Key, SharedImage?[] Colors, SharedImage? Depth, bool DepthReadOnly, int Width,
        int Height)
    {
        // The view each color attaches (a layer of an array image), 0 for the image's own
        public ulong[] Views { get; init; } = [];

        // Non-engine framebuffer: the GL textures whose images stand in for its attachments; empty for the engine's
        public int[] Stands { get; init; } = [];

        // The level and layer each color attaches, which a blit into it writes
        public (int Level, int Layer)[] At { get; init; } = [];

        // Depth view for a level/layer (0: the image's own); layers a layered framebuffer renders (all)
        public ulong DepthView { get; init; }
        public int Layers { get; init; } = 1;
    }

    // The list Images last gave (Of): each image released as it rests (Taken), and its place in the list (At)
    private sealed record Rest(IReadOnlyList<Shared>? Of, Handoff.Image[] Taken, Dictionary<SharedImage, int> At)
    {
        public static Rest Made(IReadOnlyList<Shared> images, int count)
        {
            var taken = new Handoff.Image[count];
            var at = new Dictionary<SharedImage, int>(count);
            for (var i = 0; i < Math.Min(count, MaxImages); i++)
            {
                var shared = images[i];
                taken[i] = shared.Image.In(shared.Resting);
                at[shared.Image] = i;
            }

            return new Rest(images, taken, at);
        }
    }

    // Bumps whenever a rendering begins or ends: a draw prepared at the same number needs no Prepare
    public long Renderings { get; private set; }

    // The last segment closed showed a frame (Show), with the frame not faulted
    public bool Shown { get; private set; }

    public bool Holds(SharedImage image) => NotNull(image) && _layouts.ContainsKey(image);

    // Every segment: no list copy, no LINQ. The images held from the start (the targets) are rebuilt only for a new
    // list. Only layouts the last segment moved go back.
    private void Acquire()
    {
        var images = Images();
        _ = Assert(images.Count <= MaxImages);
        var count = Math.Min(images.Count, MaxImages);
        if (!ReferenceEquals(images, _rest.Of) || _rest.Taken.Length != count)
            (_rest, _restless) = (Rest.Made(images, count), true);
        if (_restless) Rested(images, count);
        foreach (var at in _moved.Bounded(MaxImages)) _layouts[_held[at].Image] = _held[at].Resting;
        _moved.Clear();
        (_rendering, _rendered) = (null, false);
    }

    private void Rested(IReadOnlyList<Shared> images, int count)
    {
        _held.Clear();
        _layouts.Clear();
        _moved.Clear();
        for (var i = 0; i < Math.Min(count, MaxImages); i++)
        {
            var shared = images[i];
            _held.Add(shared);
            _layouts[shared.Image] = shared.Resting;
        }

        _restless = false;
    }

    // An image new since the segment opened, held from now on. False when full: the draw closes it, the next holds only
    // current ones.
    public bool Take(Shared shared)
    {
        if (_open is null || !NotNull(shared.Image)) return false;
        if (_layouts.ContainsKey(shared.Image) || _held.Count >= MaxHeld) return _layouts.ContainsKey(shared.Image);
        _held.Add(shared);
        _layouts[shared.Image] = shared.Resting;
        _restless = true;
        return true;
    }

    // Into the segment's own array, which the recording thread read before the slot comes round again
    private void Release(Segment segment)
    {
        var images = segment.Released.Length == _held.Count ? segment.Released : new Handoff.Image[_held.Count];
        segment.Released = images;
        if (_restless)
            for (var i = 0; i < Math.Min(images.Length, MaxHeld); i++)
                images[i] = _held[i].Image.In(_layouts[_held[i].Image]) with { To = _held[i].Resting };
        else
        {
            _rest.Taken.CopyTo(images, 0); // unmoved: leaving as they came
            foreach (var at in _moved.Bounded(MaxImages))
                images[at] = _held[at].Image.In(_layouts[_held[at].Image]) with { To = _held[at].Resting };
        }

        Post(new Op { Kind = OpKind.Release, Ref = images });
    }

    public bool Prepare(Target target, ReadOnlySpan<SharedImage> sampled)
    {
        if (_open is null || !NotNull(target) || !Assert(target.Colors.Length <= MaxColors)) return false;
        _moves.Clear();
        for (var i = 0; i < Math.Min(sampled.Length, MaxImages); i++)
        {
            var image = sampled[i];
            if (Array.IndexOf(target.Colors, image) >= 0 || (image == target.Depth && !target.DepthReadOnly))
                return false;
            Want(image, image == target.Depth ? LayoutDepthReadOnly : Vk.LayoutShaderRead);
        }

        foreach (var color in target.Colors.Bounded(MaxColors))
            if (color is not null)
                Want(color, Vk.LayoutColorAttachment);
        if (target.Depth is { } depth)
            Want(depth, target.DepthReadOnly ? LayoutDepthReadOnly : Vk.LayoutDepthAttachment);
        if (_moves.Count == 0 && _rendering is { } now && now.Key == target.Key &&
            now.DepthReadOnly == target.DepthReadOnly) return true;
        EndRendering();
        if (_moves.Count > 0 || _rendered) Barrier();
        BeginRendering(target);
        return true;
    }

    // Ordered as compute is; no closure a fill
    public bool Fill(ulong buffer, ulong offset, ulong size)
    {
        if (!Assert(buffer != 0 && size > 0) || !Begin(out _)) return false;
        EndRendering();
        Post(new Op { Kind = OpKind.Fill, Indirect = buffer, IndirectOffset = offset, CountOffset = size });
        return true;
    }

    public bool Compute(Action<IntPtr> record)
    {
        if (!NotNull(record) || !Begin(out _)) return false;
        VulkanWatch.Mark("compute posted");
        EndRendering();
        Post(record);
        return true;
    }

    // The next rendering moves it back
    public bool Sample(SharedImage image, int layout)
    {
        if (!NotNull(image) || !Begin(out _) || !Holds(image)) return false;
        EndRendering();
        _moves.Clear();
        Want(image, layout);
        Barrier();
        return true;
    }

    // E.g. a GL upload to a texture whose copy is sampled; the next rendering sampling it moves it back after it
    public bool Transfer(SharedImage image, Action<IntPtr> record)
    {
        if (!NotNull(image) || !NotNull(record) || _open is null || !Holds(image)) return false;
        EndRendering();
        _moves.Clear();
        Want(image, Vk.LayoutTransferDst);
        Barrier(); // with no move too: an earlier transfer into it, or a draw that sampled it, comes first
        Post(record);
        return true;
    }

    // glBlitFramebuffer: sources moved for reading, targets for writing, one region; levels/layers in Source and Target.
    // aspect: what of depth-stencil is blitted (0: all)
    public bool Blit(ReadOnlySpan<(SharedImage From, SharedImage To)> pairs, Vk.ImageBlit region, bool linear, uint aspect = 0)
    {
        if (_open is null || !Assert(pairs.Length is > 0 and <= MaxColors + 1)) return false;
        foreach (var (from, to) in pairs.Bounded(MaxColors + 1))
            if (!Holds(from) || !Holds(to) || from == to)
                return false;
        EndRendering();
        _moves.Clear();
        foreach (var (from, _) in pairs.Bounded(MaxColors + 1)) Want(from, Vk.LayoutTransferSrc);
        foreach (var (_, to) in pairs.Bounded(MaxColors + 1)) Want(to, Vk.LayoutTransferDst);
        Barrier(); // with no move too: what wrote the source or the target before comes first
        foreach (var (from, to) in pairs.Bounded(MaxColors + 1))
            Post(new Op
            {
                Kind = OpKind.Blit, Pipeline = from.Image, Layout = to.Image, Count = aspect != 0 ? aspect : to.Format.Aspect,
                First = linear ? 1u : 0,
                X = region.SourceX0, Y = region.SourceY0, Width = region.SourceX1, Height = region.SourceY1,
                Index = (uint)region.TargetX0, IndexOffset = (uint)region.TargetY0, Indirect = (uint)region.TargetX1,
                IndirectOffset = (uint)region.TargetY1, Descriptors = (int)region.Source.Level,
                DescriptorCount = (int)region.Source.BaseLayer, Push = (int)region.Target.Level,
                PushBytes = (int)region.Target.BaseLayer
            });
        return true;
    }

    // The op Blit posted: no closure, no array a blit
    private static void Blitting(IntPtr commands, ref Op op)
    {
        if (!Assert(op.Pipeline != 0 && op.Layout != 0)) return;
        var from = new Vk.Layers { Aspect = op.Count, Level = (uint)op.Descriptors, BaseLayer = (uint)op.DescriptorCount, Count = 1 };
        var to = new Vk.Layers { Aspect = op.Count, Level = (uint)op.Push, BaseLayer = (uint)op.PushBytes, Count = 1 };
        var blit = new Vk.ImageBlit
        {
            Source = from, SourceX0 = (int)op.X, SourceY0 = (int)op.Y, SourceX1 = (int)op.Width, SourceY1 = (int)op.Height,
            SourceZ1 = 1, Target = to, TargetX0 = (int)(uint)op.Index, TargetY0 = (int)(uint)op.IndexOffset,
            TargetX1 = (int)(uint)op.Indirect, TargetY1 = (int)(uint)op.IndirectOffset, TargetZ1 = 1
        };
        VkApi.CmdBlitImage(commands, op.Pipeline, Vk.LayoutTransferSrc, op.Layout, Vk.LayoutTransferDst, 1, &blit,
            op.First == 1 ? Vk.FilterLinear : Vk.FilterNearest);
    }

    // Flipped: GL's rows go up, a swapchain's down. The segment's submission signals the swapchain image's semaphore.
    public bool Show(SharedImage from, (ulong Image, ulong Rendered) into, (int Width, int Height) size)
    {
        if (_open is null || !Holds(from) || !Assert(into.Image != 0 && into.Rendered != 0)) return false;
        EndRendering();
        _moves.Clear();
        Want(from, Vk.LayoutTransferSrc);
        Barrier();
        Post(new Op
        {
            Kind = OpKind.Present, Pipeline = from.Image, Layout = into.Image, First = (uint)from.Width,
            Instances = (uint)from.Height, Count = (uint)size.Width, BaseInstance = (uint)size.Height
        });
        (_rendered, _showing) = (true, into.Rendered);
        return Assert(size.Width > 0 && size.Height > 0);
    }

    private static void Blitted(IntPtr commands, (ulong Image, int Width, int Height) from,
        (ulong Image, int Width, int Height) into)
    {
        var linear = (from.Width, from.Height) != (into.Width, into.Height);
        Moved(commands, (into.Image, Vk.AspectColor, 1, 1, Vk.LayoutUndefined, Vk.LayoutTransferDst));
        var layer = new Vk.Layers { Aspect = Vk.AspectColor, Count = 1 };
        var blit = new Vk.ImageBlit
        {
            Source = layer, SourceX1 = from.Width, SourceY1 = from.Height, SourceZ1 = 1, Target = layer,
            TargetY0 = into.Height, TargetX1 = into.Width, TargetZ1 = 1
        };
        VkApi.CmdBlitImage(commands, from.Image, Vk.LayoutTransferSrc, into.Image, Vk.LayoutTransferDst, 1, &blit,
            linear ? Vk.FilterLinear : Vk.FilterNearest);
        Moved(commands, (into.Image, Vk.AspectColor, 1, 1, Vk.LayoutTransferDst, Vk.LayoutPresentSrc));
    }

    private void Want(SharedImage image, int layout)
    {
        if (!NotNull(image) || !_layouts.TryGetValue(image, out var now) || !Assert(layout > 0)) return;
        if (now == layout) return;
        foreach (var move in _moves.Bounded(MaxImages))
            if (move.Image == image)
                return;
        _moves.Add((image, layout));
    }

    private void Barrier()
    {
        if (!Assert(_moves.Count <= MaxImages)) return;
        var moves = Spared(_spareMoves);
        if (moves.Images.Length < _moves.Count) moves.Images = new (ulong, uint, int, int, int, int)[Math.Max(_moves.Count, 16)];
        moves.Count = _moves.Count;
        for (var i = 0; i < Math.Min(_moves.Count, MaxImages); i++)
        {
            var (image, to) = _moves[i];
            ref var layout = ref CollectionsMarshal.GetValueRefOrNullRef(_layouts, image); // Want moves only held images
            moves.Images[i] = (image.Image, image.Format.Aspect, image.Levels, image.Layers, layout, to);
            layout = to;
            if (_rest.At.TryGetValue(image, out var at) && _moved.Count < MaxImages) _moved.Add(at);
            else _restless = true;
        }

        Post(new Op { Kind = OpKind.Barrier, Ref = moves });
    }

    private void BeginRendering(Target target)
    {
        if (!Assert(target.Width > 0 && target.Height > 0)) return;
        var rendering = Spared(_spareRenderings);
        rendering.Count = Math.Min(target.Colors.Length, MaxColors);
        for (var i = 0; i < Math.Min(rendering.Count, MaxColors); i++)
        {
            rendering.Colors[i] = 0;
            if (target.Colors[i] is { } color)
                rendering.Colors[i] = i < target.Views.Length && target.Views[i] != 0 ? target.Views[i] : color.View;
        }
        (rendering.Depth, rendering.DepthLayout) = (target.DepthView != 0 ? target.DepthView : target.Depth?.View ?? 0,
            target.DepthReadOnly ? LayoutDepthReadOnly : Vk.LayoutDepthAttachment);
        (rendering.Stencil, rendering.Layers) =
            ((target.Depth?.Format.Aspect & Vk.AspectStencil) is > 0, (uint)Math.Clamp(target.Layers, 1, 16));
        (rendering.Width, rendering.Height) = ((uint)target.Width, (uint)target.Height);
        Post(new Op { Kind = OpKind.BeginRendering, Ref = rendering });
        (_rendering, _viewport, _scissor) = (target, default, (0, 0, target.Width, target.Height));
        Renderings++;
    }

    private static Vk.Attachment Kept(ulong view, int layout) =>
        new()
        {
            SType = Vk.RenderingAttachmentInfo, View = view, Layout = layout, Load = Vk.LoadOpLoad, Store = Vk.StoreOpStore
        };

    public bool CopyBuffer((ulong Buffer, ulong Offset) from, (ulong Buffer, ulong Offset) to, ulong size)
    {
        if (_open is null || !Assert(size > 0) || !Assert(from.Buffer != 0 && to.Buffer != 0)) return false;
        EndRendering();
        Post(new Op
        {
            Kind = OpKind.Copy, Index = from.Buffer, IndexOffset = from.Offset, Indirect = to.Buffer,
            IndirectOffset = to.Offset, CountOffset = size
        });
        return true;
    }

    private static void Copied(IntPtr commands, (ulong Buffer, ulong Offset) from, (ulong Buffer, ulong Offset) to,
        ulong size)
    {
        var before = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessMemoryRead | Vk.AccessMemoryWrite, DstAccess = Vk.AccessTransferWrite
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageTransfer, 0, 1, &before, 0, null, 0, null);
        var region = new Vk.BufferCopy { SourceOffset = from.Offset, TargetOffset = to.Offset, Size = size };
        VkApi.CmdCopyBuffer(commands, from.Buffer, to.Buffer, 1, &region);
        var after = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite, DstAccess = Vk.AccessMemoryRead | Vk.AccessMemoryWrite
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageAll, 0, 1, &after, 0, null, 0, null);
    }

    private void EndRendering()
    {
        if (_rendering is null) return;
        Post(new Op { Kind = OpKind.EndRendering });
        (_rendering, _rendered) = (null, true);
        Renderings++;
    }

    public void Scissor((int X, int Y, int Width, int Height)? box)
    {
        if (_open is null || _rendering is not { } target) return;
        var (x, y) = (Math.Clamp(box?.X ?? 0, 0, target.Width), Math.Clamp(box?.Y ?? 0, 0, target.Height));
        var right = Math.Clamp(box is { } b ? b.X + b.Width : target.Width, x, target.Width);
        var top = Math.Clamp(box is { } c ? c.Y + c.Height : target.Height, y, target.Height);
        var clamped = (x, y, right - x, top - y);
        if (clamped == _scissor) return;
        _scissor = clamped;
        Post(new Op { Kind = OpKind.Scissor, X = x, Y = y, Width = right - x, Height = top - y });
    }

    public void Viewport((int X, int Y, int Width, int Height) viewport)
    {
        if (_open is null || _rendering is null || viewport == _viewport) return;
        if (!Assert(viewport.Width >= 0 && viewport.Height >= 0)) return;
        _viewport = viewport;
        Post(new Op
        {
            Kind = OpKind.Viewport, X = viewport.X, Y = viewport.Y, Width = viewport.Width, Height = viewport.Height
        });
    }
}
