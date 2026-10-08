using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// OpenGL's glBufferSubData into a pool's shared buffers was OpenGL work on shared memory in nearly every frame (chunks
// stream in), so every handoff waited for OpenGL; Vulkan copies the faces instead, where OpenGL would have written them, in
// its order. Before OpenGL may read or write a pool's buffers (PoolsFirst), the waiting copies go through OpenGL from the
// staging, a segment holding some is closed, and OpenGL waits for it.
internal sealed partial class TerrainRenderer
{
    private const int MaxPoolCopies = 1024, MaxPoolBuffers = 16;

    private readonly List<PoolCopy> _poolCopies = [];
    private long _poolCopiedIn = -1; // the segment (VulkanFrame.Serial) that holds copies into pools

    private readonly record struct PoolCopy(HostBuffer From, long At, SharedBuffer To, long Offset, int Size);


    public bool Faces(VAO vao, int buffer, long offset, ReadOnlySpan<byte> faces)
    {
        if (!NotNull(vao) || buffer <= 0 || Pools.Find(vao) is not { } pool || Of(pool, buffer) is not { } to ||
            faces.IsEmpty || offset < 0 || offset + faces.Length > (long)to.Size || _poolCopies.Count >= MaxPoolCopies)
            return false;
        if (Frame.Staging is not { } staging) return false;
        var at = staging.Take(faces.Length, out var into, 16);
        if (at < 0) return false; // the frame's staging is full: OpenGL's
        faces.CopyTo(into);
        var copy = new PoolCopy(staging, at, to, offset, faces.Length);
        if (Frame.Open)
        {
            if (!Frame.CopyBuffer((staging.Buffer, (ulong)at), (to.Buffer, (ulong)offset), (ulong)faces.Length))
                return false;
            _poolCopiedIn = Frame.Serial;
        }
        else _poolCopies.Add(copy);

        return Assert(_poolCopies.Count <= MaxPoolCopies);
    }

    private static SharedBuffer? Of(TerrainPools.Pool pool, int buffer)
    {
        if (!NotNull(pool) || !Assert(buffer > 0)) return null;
        if (pool.Faces is { } faces && faces.Gl == buffer) return faces;
        foreach (var vertices in pool.Vertices.Bounded(MaxPoolBuffers))
            if (vertices.Gl == buffer)
                return vertices;
        return Assert(pool.Vertices.Length <= MaxPoolBuffers) && pool.Indices?.Gl == buffer ? pool.Indices : null;
    }

    // Written in place by FacePacking, then made by Committed; null leaves the upload to Through or OpenGL
    public unsafe byte* Room(VAO vao, int buffer, long offset, int bytes)
    {
        _room = null;
        if (!NotNull(vao) || buffer <= 0 || bytes <= 0 || Pools.Find(vao) is not { } pool || Of(pool, buffer) is not { } to ||
            offset < 0 || offset + bytes > (long)to.Size || _poolCopies.Count >= MaxPoolCopies ||
            Frame.Staging is not { } staging) return null;
        var at = staging.Take(bytes, out _, 16);
        if (at < 0) return null;
        _room = new PoolCopy(staging, at, to, offset, bytes);
        return Assert(at >= 0) ? staging.Pointer(at) : null;
    }

    public void Committed()
    {
        if (_room is not { } copy || !Assert(copy.Size > 0)) return;
        _room = null;
        if (!Frame.Open) _poolCopies.Add(copy);
        else if (Frame.CopyBuffer((copy.From.Buffer, (ulong)copy.At), (copy.To.Buffer, (ulong)copy.Offset), (ulong)copy.Size))
            _poolCopiedIn = Frame.Serial;
        else _poolCopies.Add(copy); // the segment refused it: waits for the next, or OpenGL
    }

    private PoolCopy? _room;

    public unsafe void PoolsOpened()
    {
        if (_poolCopies.Count == 0 || !Assert(Frame.Open)) return;
        var setup = Frame.Setup;
        var written = 0; // the copies since the last barrier: _poolCopies[first..]
        var first = 0;
        for (var c = 0; c < Math.Min(_poolCopies.Count, MaxPoolCopies); c++)
        {
            var copy = _poolCopies[c];
            if (Overlaps(copy, first, written))
            {
                var memory = new Vk.GlobalBarrier
                {
                    SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessTransferWrite, DstAccess = Vk.AccessTransferWrite
                };
                VkApi.CmdPipelineBarrier(setup, Vk.StageTransfer, Vk.StageTransfer, 0, 1, &memory, 0, null, 0, null);
                (first, written) = (c, 0); // the same range written twice: the later copy after the earlier one
            }

            var region = new Vk.BufferCopy
            {
                SourceOffset = (ulong)copy.At, TargetOffset = (ulong)copy.Offset, Size = (ulong)copy.Size
            };
            VkApi.CmdCopyBuffer(setup, copy.From.Buffer, copy.To.Buffer, 1, &region); // the setup's barriers order the rest
            written++;
        }

        _poolCopies.Clear();
        _poolCopiedIn = Frame.Serial; // the setup is the segment's: OpenGL reading a pool waits for it
    }

    private bool Overlaps(PoolCopy copy, int first, int written)
    {
        if (!NotNull(copy.To) || !Assert(first + written <= _poolCopies.Count)) return false;
        for (var i = first; i < Math.Min(first + written, MaxPoolCopies); i++)
        {
            var w = _poolCopies[i];
            if (ReferenceEquals(w.To, copy.To) && w.Offset < copy.Offset + copy.Size && copy.Offset < w.Offset + w.Size)
                return true;
        }

        return false;
    }

    public void PoolsFirst()
    {
        if (Frame.Open && _poolCopiedIn == Frame.Serial) Frame.Close("OpenGL reads or writes a pool Vulkan uploaded into");
        PoolsToGl();
        _ = Assert(_poolCopies.Count == 0);
    }

    public void PoolsEnding()
    {
        _ = Assert(!Frame.Open) && Assert(_poolCopies.Count <= MaxPoolCopies);
        PoolsToGl();
    }

    // The waiting copies made by OpenGL from their staging, in order, after OpenGL waited for Vulkan's last segment (its first
    // quiet call settles the frame, GlQuiet; without the judge, the caller settled first)
    private unsafe void PoolsToGl()
    {
        if (_poolCopies.Count == 0 || !Assert(_poolCopies.Count <= MaxPoolCopies)) return;
        Rendering.FacePacking.Settle(); // the faces packed into the staging in the background are there
        using (GlTap.Quietly())
            foreach (var copy in _poolCopies.Bounded(MaxPoolCopies)) // nothing adds copies while OpenGL makes these
                if (copy.From.Pointer(copy.At) is var data && data != null)
                    GL.NamedBufferSubData(copy.To.Gl, new IntPtr(copy.Offset), copy.Size, (IntPtr)data);
        _poolCopies.Clear();
    }
}
