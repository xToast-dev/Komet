using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Adopting a pool moves each of its buffers into a SharedBuffer of the same size; the VAO's bindings and the engine's VAO
// fields name the new buffers, so the engine goes on filling them and drawing them with OpenGL. The engine's pools are
// persistently mapped (since 1.22) and written through the VAO's pointers, never the buffer names, so such a pool moves into
// mapped shared buffers and its pointers move with it once the copy of what it held is done on the GPU (a write through the
// new pointer must not be overwritten by the copy). Releasing leaves the engine's fields at zero, so its own dispose deletes
// nothing twice.
internal sealed class TerrainPools(VulkanDevice device) : IDisposable
{
    private const int MaxAttributes = 16, MaxPools = 4096;
    private const int BindingOffset = 0x82D7, BindingStride = 0x82D8, AttributeBuffer = 0x889F, Enabled = 0x8622;
    private const int GlSize = 0x8623, GlType = 0x8625, GlNormalized = 0x886A, GlInteger = 0x88FD, GlDivisor = 0x88FE;

    private readonly Dictionary<int, Pool> _pools = [];

    public readonly record struct Attribute(int Location, int Buffer, int Size, int Type, bool Normalized, bool Integer)
    {
        public int Stride { get; init; }
        public long Offset { get; init; }
        public int Divisor { get; init; }
    }

    public sealed class Pool(VAO vao, SharedBuffer? faces, SharedBuffer[] vertices, Attribute[] attributes)
    {
        public VAO Vao { get; } = vao;
        public SharedBuffer? Faces { get; } = faces;
        public SharedBuffer[] Vertices { get; } = vertices;
        public Attribute[] Attributes { get; } = attributes;
        // The classic path's own indices; the SSBO path draws the quad pattern
        public SharedBuffer? Indices { get; init; }

        // Cached per location list: the opaque, shadow and depth pipelines take turns drawing the pool
        public (uint Location, ulong Buffer, ulong Offset)[] Bindings(int[] locations)
        {
            if (!NotNull(locations) || !Assert(locations.Length <= MaxAttributes)) return [];
            for (var i = 0; i < Math.Min(_bound, MaxLists); i++)
                if (ReferenceEquals(_lists[i].For, locations))
                    return _lists[i].Bindings;
            var bindings = new (uint, ulong, ulong)[locations.Length];
            for (var i = 0; i < Math.Min(locations.Length, MaxAttributes); i++)
            {
                var location = locations[i];
                var a = Attributes.First(x => x.Location == location); // only when the list is new
                bindings[i] = ((uint)locations[i], Vertices[a.Buffer].Buffer, (ulong)a.Offset);
            }

            _lists[_bound++ % MaxLists] = (locations, bindings); // a pipeline made again replaces the oldest
            return Assert(bindings.Length == locations.Length) ? bindings : [];
        }

        private const int MaxLists = 8;
        private readonly (int[] For, (uint, ulong, ulong)[] Bindings)[] _lists = new (int[], (uint, ulong, ulong)[])[MaxLists];
        private int _bound;

        // The same for every pool with the same attributes
        public string Layout { get; } = string.Join(';', attributes.Select(a =>
            $"{a.Location}:{a.Size}:{a.Type}:{a.Normalized}:{a.Integer}:{a.Stride}:{a.Divisor}"));
    }

    private uint[]? _names;

    public int Count => _pools.Count;

    // What the adopted pools' buffers hold, for the report: the shared arena's ranges the pools have
    public ulong Bytes => Assert(_pools.Count <= MaxPools)
        ? (ulong)_pools.Values.SelectMany(Buffers).Distinct().Sum(b => (decimal)b.Size)
        : 0;

    // Every adopted pool's GL buffers, for the semaphores that hand them over
    public uint[] GlBuffers => _names ??= [.. _pools.Values.SelectMany(Buffers).Select(b => (uint)b.Gl).Distinct()];

    private static IEnumerable<SharedBuffer> Buffers(Pool pool)
    {
        if (!NotNull(pool) || !Assert(pool.Vertices.Length <= MaxAttributes)) return [];
        IEnumerable<SharedBuffer?> all = [.. pool.Vertices, pool.Faces, pool.Indices];
        return all.OfType<SharedBuffer>().Distinct();
    }

    public Pool? Find(VAO vao) =>
        NotNull(vao) && Assert(_pools.Count <= MaxPools) && _pools.TryGetValue(vao.VaoId, out var pool) ? pool : null;

    private sealed record Moving(VAO Vao, Attribute[] Attributes, Dictionary<int, SharedBuffer> Moved, int Indices);

    // Classic: the pool is drawn with its own index buffer, which moves too. Fresh: a pool the engine has just made and not yet
    // written (AllocateEmptyMesh stores nothing), so there is nothing to copy and no copy to wait for
    public Pool? Adopt(VAO vao, bool classic, out string why, bool fresh = false)
    {
        why = "";
        if (!NotNull(vao) || !Assert(vao.VaoId > 0)) return null;
        if (_pools.TryGetValue(vao.VaoId, out var known)) return known;
        var moving = Copied(vao, classic, fresh, out why);
        if (moving is null || !Assert(moving.Moved.Count > 0)) return null;
        if (vao.Persistent && !fresh) Settle();
        return Rebound(moving);
    }

    // Every copy first, one wait for all, then every pool rebound
    public int Adopt(List<(VAO Vao, bool Classic)> pools, out string why)
    {
        why = "";
        if (!NotNull(pools) || !Assert(pools.Count <= MaxPools)) return 0;
        var moving = new List<Moving>();
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var (vao, classic) = pools[i];
            if (vao.VaoId <= 0 || _pools.ContainsKey(vao.VaoId) || Listed(moving, vao.VaoId)) continue;
            if (Copied(vao, classic, false, out var refused) is { } made) moving.Add(made);
            else if (why.Length == 0) why = refused;
        }

        if (moving.Exists(m => m.Vao.Persistent)) Settle();
        foreach (var made in moving.ToArray().Bounded(MaxPools)) _ = Rebound(made);
        return moving.Count;
    }

    // Every pool every frame: a lambda capturing the pool made a closure for each, adopted or not
    private static bool Listed(List<Moving> moving, int vao)
    {
        foreach (var m in moving.Bounded(MaxPools))
            if (m.Vao.VaoId == vao)
                return true;
        return !Assert(vao > 0);
    }

    private Moving? Copied(VAO vao, bool classic, bool fresh, out string why)
    {
        why = "";
        if (!NotNull(vao) || !Assert(_pools.Count < MaxPools)) return null;
        VulkanWatch.Mark($"adopting pool {vao.VaoId}");
        var moved = new Dictionary<int, SharedBuffer>();
        var attributes = Formats(vao);
        var indices = classic ? vao.vboIdIndex : 0;
        var names = attributes.Select(a => a.Buffer).Append(vao.xyzVboId).Append(indices).Where(b => b > 0).Distinct()
            .ToArray();
        foreach (var name in names.Bounded(MaxAttributes + 2))
        {
            var shared = Moved(name, vao.Persistent, fresh, out why);
            if (shared is null)
            {
                foreach (var made in moved.Values.Bounded(MaxAttributes + 2)) made.Dispose();
                return null;
            }

            moved[name] = shared;
        }

        return new Moving(vao, attributes, moved, indices);
    }

    private Pool Rebound(Moving moving)
    {
        _ = Assert(!_pools.ContainsKey(moving.Vao.VaoId));
        var pool = Rebind(moving.Vao, moving.Attributes, moving.Moved, moving.Indices);
        _pools[moving.Vao.VaoId] = pool;
        _names = null;
        return pool;
    }

    // Until every copy given to OpenGL so far is done on the GPU: a pointer handed to the engine must not be written by one
    private static void Settle()
    {
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        if (!Assert(fence != IntPtr.Zero)) return;
        var waited = GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, SettleNs);
        GL.DeleteSync(fence);
        _ = Assert(waited is WaitSyncStatus.AlreadySignaled or WaitSyncStatus.ConditionSatisfied);
    }

    private const long SettleNs = 5_000_000_000;

    // Buffer is the GL name until Rebind
    private static Attribute[] Formats(VAO vao)
    {
        var found = new List<Attribute>();
        if (!NotNull(vao)) return [];
        for (var i = 0; i < Math.Min(vao.vaoSlotNumber, MaxAttributes); i++)
        {
            var at = (vao.VaoId, i);
            if (Parameter(at, Enabled) == 0) continue;
            var offset = new long[1];
            GL.GetVertexArrayIndexed64(vao.VaoId, i, (VertexArrayIndexed64Parameter)BindingOffset, offset);
            found.Add(new Attribute(i, Parameter(at, AttributeBuffer), Parameter(at, GlSize), Parameter(at, GlType),
                Parameter(at, GlNormalized) != 0, Parameter(at, GlInteger) != 0)
            {
                Stride = Parameter(at, BindingStride), Offset = offset[0], Divisor = Parameter(at, GlDivisor)
            });
        }

        _ = Assert(found.Count <= MaxAttributes);
        return [.. found];
    }

    private static int Parameter((int Vao, int Index) at, int name)
    {
        if (!Assert(at.Vao > 0) || !Index(at.Index, MaxAttributes)) return 0;
        var value = new int[1];
        GL.GetVertexArrayIndexed(at.Vao, at.Index, (VertexArrayIndexedParameter)name, value);
        return value[0];
    }

    private SharedBuffer? Moved(int name, bool mapped, bool fresh, out string why)
    {
        why = "";
        if (!Assert(name > 0)) return null;
        GL.GetNamedBufferParameter(name, BufferParameterName.BufferSize, out int size);
        _ = Assert(size >= 0);
        var shared = size > 0 ? SharedBuffer.Create(device, (ulong)size, out why, mapped) : null;
        if (shared is not null && !fresh) GL.CopyNamedBufferSubData(name, shared.Gl, IntPtr.Zero, IntPtr.Zero, size);
        else if (why.Length == 0) why = $"buffer {name} has no storage";
        return shared;
    }

    private static Pool Rebind(VAO vao, Attribute[] attributes, Dictionary<int, SharedBuffer> moved, int indices)
    {
        var vertices = moved.Where(m => (m.Key != vao.xyzVboId && m.Key != indices) ||
                                        attributes.Any(a => a.Buffer == m.Key))
            .Select(m => m.Value).ToArray();
        var rebound = new Attribute[attributes.Length];
        for (var i = 0; i < Math.Min(attributes.Length, MaxAttributes); i++)
        {
            var a = attributes[i];
            var shared = moved[a.Buffer];
            GL.VertexArrayVertexBuffer(vao.VaoId, a.Location, shared.Gl, new IntPtr(a.Offset), a.Stride);
            rebound[i] = a with { Buffer = Array.IndexOf(vertices, shared) };
        }

        var faces = moved.GetValueOrDefault(vao.xyzVboId);
        var elements = indices > 0 ? moved.GetValueOrDefault(indices) : null;
        if (elements is not null) GL.VertexArrayElementBuffer(vao.VaoId, elements.Gl);
        Rename(vao, name => moved.TryGetValue(name, out var shared) ? (shared.Gl, shared.Pointer) : null);
        foreach (var old in moved.Keys.Bounded(MaxAttributes + 2)) GL.DeleteBuffer(old); // unmapped with it
        _ = Assert(rebound.All(a => a.Buffer >= 0));
        return new Pool(vao, faces, vertices, rebound) { Indices = elements };
    }

    private const int Slots = 10;

    private static ref int Name(VAO vao, int slot)
    {
        _ = Assert(slot is >= 0 and < Slots);
        switch (slot)
        {
            case 0: return ref vao.xyzVboId;
            case 1: return ref vao.normalsVboId;
            case 2: return ref vao.uvVboId;
            case 3: return ref vao.rgbaVboId;
            case 4: return ref vao.flagsVboId;
            case 5: return ref vao.customDataFloatVboId;
            case 6: return ref vao.customDataIntVboId;
            case 7: return ref vao.customDataShortVboId;
            case 8: return ref vao.customDataByteVboId;
            default: return ref vao.vboIdIndex;
        }
    }

    private static ref nint Pointer(VAO vao, int slot)
    {
        _ = Assert(slot is >= 0 and < Slots);
        switch (slot)
        {
            case 0: return ref vao.xyzPtr;
            case 1: return ref vao.normalsPtr;
            case 2: return ref vao.uvPtr;
            case 3: return ref vao.rgbaPtr;
            case 4: return ref vao.flagsPtr;
            case 5: return ref vao.customDataFloatPtr;
            case 6: return ref vao.customDataIntPtr;
            case 7: return ref vao.customDataShortPtr;
            case 8: return ref vao.customDataBytePtr;
            default: return ref vao.indicesPtr;
        }
    }

    private static void Rename(VAO vao, System.Func<int, (int Name, nint Pointer)?> to)
    {
        if (!NotNull(vao) || !NotNull(to)) return;
        for (var slot = 0; slot < Slots; slot++)
        {
            ref var name = ref Name(vao, slot);
            if (name <= 0 || to(name) is not { } moved) continue;
            name = moved.Name;
            if (vao.Persistent) Pointer(vao, slot) = moved.Pointer;
        }

        _ = Assert(vao.VaoId > 0);
    }

    public void Release(VAO vao)
    {
        if (!NotNull(vao) || !Assert(_pools.Count <= MaxPools) || !_pools.Remove(vao.VaoId, out var pool)) return;
        _ = device.WaitIdle(); // a frame in flight may still read it
        Unhook(pool);
        _names = null;
    }

    private static void Unhook(Pool pool)
    {
        if (!NotNull(pool) || !NotNull(pool.Vao)) return;
        var shared = Buffers(pool).Select(b => b.Gl).Where(g => g > 0).ToHashSet();
        Rename(pool.Vao, name => shared.Contains(name) ? (0, 0) : null);
        foreach (var buffer in Buffers(pool).ToArray().Bounded(MaxAttributes + 2)) buffer.Dispose();
    }

    // The contents copied back are done on the GPU before a pointer is handed back
    public void Dispose()
    {
        _ = Assert(_pools.Count <= MaxPools) && device.WaitIdle();
        var back = new Dictionary<int, (int Name, nint Pointer)>();
        foreach (var pool in _pools.Values.ToArray().Bounded(MaxPools))
            foreach (var buffer in Buffers(pool).ToArray().Bounded(MaxAttributes + 2))
                back[buffer.Gl] = Fresh(buffer, pool.Vao.Persistent);
        if (_pools.Values.Any(p => p.Vao.Persistent)) Settle();
        foreach (var pool in _pools.Values.ToArray().Bounded(MaxPools)) Restored(pool, back);
        _pools.Clear();
        _names = null;
        device.Arena.Trim(); // the blocks the pools emptied
    }

    private const int PersistentStorage = 0x1C2, PersistentMap = 0xC2; // as the engine's GenArrayBuffer asks

    private static (int Name, nint Pointer) Fresh(SharedBuffer shared, bool persistent)
    {
        if (!NotNull(shared) || !Assert(shared.Size is > 0 and <= int.MaxValue)) return (0, 0);
        GL.CreateBuffers(1, out int buffer);
        var size = (int)shared.Size;
        nint pointer = 0;
        if (persistent)
        {
            GL.NamedBufferStorage(buffer, size, IntPtr.Zero, (BufferStorageFlags)PersistentStorage);
            pointer = GL.MapNamedBufferRange(buffer, IntPtr.Zero, size, (BufferAccessMask)PersistentMap);
        }
        else GL.NamedBufferData(buffer, size, IntPtr.Zero, BufferUsageHint.DynamicDraw);

        GL.CopyNamedBufferSubData(shared.Gl, buffer, IntPtr.Zero, IntPtr.Zero, size);
        return (buffer, pointer);
    }

    private static void Restored(Pool pool, Dictionary<int, (int Name, nint Pointer)> back)
    {
        if (!NotNull(pool) || !NotNull(back)) return;
        var vao = pool.Vao;
        foreach (var a in pool.Attributes.Bounded(MaxAttributes))
            GL.VertexArrayVertexBuffer(vao.VaoId, a.Location, back[pool.Vertices[a.Buffer].Gl].Name, new IntPtr(a.Offset),
                a.Stride);
        if (pool.Indices is { } indices) GL.VertexArrayElementBuffer(vao.VaoId, back[indices.Gl].Name);
        Rename(vao, name => back.TryGetValue(name, out var fresh) ? fresh : null);
        foreach (var buffer in Buffers(pool).ToArray().Bounded(MaxAttributes + 2)) buffer.Dispose();
    }
}
