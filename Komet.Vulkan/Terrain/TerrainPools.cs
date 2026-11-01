using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Adopting gives each pool buffer a Vulkan buffer of its own (Stood) named by its GL buffer, filled from OpenGL's
// mapping or read back once (Seeded); OpenGL keeps its own. Persistently mapped pools (since 1.22) are written through
// Vulkan's mapping (PersistentMaps), or OpenGL's mapping's engine pointer moves to Vulkan's until the pool goes. A pool
// refused is tried again only after a wait (Refusing): each try makes and frees device memory.
internal sealed unsafe class TerrainPools(VulkanDevice device, VulkanFrame frame) : IDisposable
{
    internal const int Slots = 10; // a VAO's buffers (PersistentMaps' too)
    private const int MaxAttributes = 16, MaxPools = 4096, MaxRetired = 1024;
    private const int RetryFrames = 60, MaxRetryFrames = 256; // doubling from a second to about 4 s at 60 fps

    private readonly Dictionary<int, Pool> _pools = [];
    private readonly Dictionary<int, (long At, int Wait, string Why)> _refused = []; // by VAO: tried again from At
    private readonly Dictionary<int, SharedBuffer> _byName = []; // each pool buffer by its GL buffer
    private readonly List<(VAO Vao, int Slot, nint Gl)> _repointed = []; // OpenGL's mappings moved to Vulkan's
    private readonly Retired<SharedBuffer> _retired = new(MaxRetired, static buffer => buffer.Dispose()); // released pools'

    // GL buffer the engine writes through a Vulkan mapping (PersistentMaps.Of)
    public System.Func<uint, (ulong Buffer, nint Pointer, long Size)?>? Mapped { get; set; }

    // Read back from OpenGL once (made before Vulkan, or not adopted at creation)
    public long Readbacks { get; private set; }

    public SharedBuffer? BufferOf(int gl) => _byName.TryGetValue(gl, out var b) ? b : null;

    // For BufferMirrors.Pooled
    public (ulong Buffer, ulong Offset, ulong Size)? Range(uint gl) =>
        gl < int.MaxValue && BufferOf((int)gl) is { } b && Assert(b.Buffer != 0) && Assert(b.Size > 0)
            ? (b.Buffer, 0, b.Size)
            : null;

    // Told of each GL buffer a pool now holds (the scene drops its mirror)
    public Action<uint>? Held { get; set; }

    public readonly record struct Attribute(int Location, int Buffer, int Size, int Type, bool Normalized, bool Integer)
    {
        public int Stride { get; init; }
        public long Offset { get; init; }
        public int Divisor { get; init; }
    }

    public sealed class Pool(VAO vao, SharedBuffer? faces, SharedBuffer[] vertices, Attribute[] attributes)
    {
        private const int MaxLists = 8;
        private readonly (int[] For, (uint, ulong, ulong)[] Bindings)[] _lists = new (int[], (uint, ulong, ulong)[])[MaxLists];
        private int _bound;

        public VAO Vao { get; } = vao;
        public SharedBuffer? Faces { get; } = faces;
        public SharedBuffer[] Vertices { get; } = vertices;
        public Attribute[] Attributes { get; } = attributes;

        // Classic path only; SSBO draws the quad pattern
        public SharedBuffer? Indices { get; init; }

        public string Layout { get; } = string.Join(';', attributes.Select(a =>
            $"{a.Location}:{a.Size}:{a.Type}:{a.Normalized}:{a.Integer}:{a.Stride}:{a.Divisor}"));

        // Cached per location list: opaque, shadow and depth pipelines take turns on the pool
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
                var at = Array.FindIndex(Attributes, x => x.Location == location); // only when the list is new
                // a location the pool lacks: none bound, the draw refused (Posted) rather than thrown mid-frame
                if (!Assert(at >= 0) || !Index(Attributes[at].Buffer, Vertices.Length)) return [];
                var a = Attributes[at];
                bindings[i] = ((uint)locations[i], Vertices[a.Buffer].Buffer, (ulong)a.Offset);
            }

            _lists[_bound++ % MaxLists] = (locations, bindings); // a pipeline made again replaces the oldest
            return bindings;
        }
    }

    public int Count => _pools.Count;

    public ulong Bytes => (ulong)_pools.Values.SelectMany(Buffers).Distinct().Sum(b => (decimal)b.Size);

    // For the report: SSBO faces, their vertex buffers (colours, custom data), classic pools (liquids)
    public string Breakdown()
    {
        var (faces, beside, classic, classicPools) = (0UL, 0UL, 0UL, 0);
        foreach (var pool in _pools.Values.ToArray().Bounded(MaxPools))
        {
            var bytes = Buffers(pool).Aggregate(0UL, static (sum, b) => sum + b.Size);
            if (pool.Indices is not null)
            {
                (classic, classicPools) = (classic + bytes, classicPools + 1);
                continue;
            }

            var face = pool.Faces?.Size ?? 0;
            (faces, beside) = (faces + face, beside + bytes - face);
        }

        return $"faces {faces >> 20} MB, colours and custom data {beside >> 20} MB, classic (liquids) {classic >> 20} MB in {classicPools} pools";
    }

    private static IEnumerable<SharedBuffer> Buffers(Pool pool)
    {
        IEnumerable<SharedBuffer?> all = [.. pool.Vertices, pool.Faces, pool.Indices];
        return all.OfType<SharedBuffer>().Distinct();
    }

    public Pool? Find(VAO vao) =>
        NotNull(vao) && _pools.TryGetValue(vao.VaoId, out var pool) ? pool : null;

    // classic: its index buffer moves too. fresh: just made, unwritten (AllocateEmptyMesh): nothing to fill
    public Pool? Adopt(VAO vao, bool classic, out string why, bool fresh = false)
    {
        why = "";
        if (!NotNull(vao) || !Assert(vao.VaoId > 0)) return null;
        if (_pools.TryGetValue(vao.VaoId, out var known)) return known;
        if (Waits(vao.VaoId, out why)) return null;
        var pool = Made(vao, classic, fresh, out why);
        if (pool is null) Refusing(vao.VaoId, why);
        else _ = _refused.Remove(vao.VaoId);
        return pool;
    }

    // The pools not adopted yet; why: the first refusal
    public int Adopt(List<(VAO Vao, bool Classic)> pools, out string why)
    {
        why = "";
        if (!NotNull(pools) || !Assert(pools.Count <= MaxPools)) return 0;
        var count = 0;
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var (vao, classic) = pools[i];
            if (vao.VaoId <= 0 || _pools.ContainsKey(vao.VaoId)) continue;
            if (Adopt(vao, classic, out var refused) is not null) count++;
            else if (why.Length == 0) why = refused;
        }

        return count;
    }

    // Refused lately: why, until its wait passed
    private bool Waits(int vao, out string why)
    {
        why = "";
        if (!_refused.TryGetValue(vao, out var refused) || frame.Number >= refused.At) return false;
        why = refused.Why;
        return true;
    }

    // Tried again after RetryFrames, each refusal in a row doubling the wait up to MaxRetryFrames
    private void Refusing(int vao, string why)
    {
        var wait = _refused.TryGetValue(vao, out var last) ? Math.Min(2 * last.Wait, MaxRetryFrames) : RetryFrames;
        if (_refused.Count < MaxPools || _refused.ContainsKey(vao))
            _refused[vao] = (frame.Number + wait, wait, why.Length > 0 ? why : "refused");
    }

    // Each of the pool's buffers stood for by one of Vulkan's, then the pool assembled from them
    private Pool? Made(VAO vao, bool classic, bool fresh, out string why)
    {
        why = "";
        if (!Assert(_pools.Count < MaxPools)) return null;
        VulkanWatch.Mark($"adopting pool {vao.VaoId}");
        var moved = new Dictionary<int, SharedBuffer>();
        var attributes = Formats(vao);
        var unbuffered = Array.FindIndex(attributes, static a => a.Buffer <= 0);
        if (unbuffered >= 0)
        {
            // enabled before its buffer is attached: Assembled would find no buffer for it; tried again (Refusing)
            why = $"attribute {attributes[unbuffered].Location} has no buffer";
            return null;
        }

        var indices = classic ? vao.vboIdIndex : 0;
        var names = attributes.Select(a => a.Buffer).Append(vao.xyzVboId).Append(indices).Where(b => b > 0).Distinct()
            .ToArray();
        foreach (var name in names.Bounded(MaxAttributes + 2))
        {
            var shared = Stood(vao, name, fresh, out why);
            if (shared is null)
            {
                foreach (var made in moved.Values.Bounded(MaxAttributes + 2)) made.Dispose();
                return null;
            }

            moved[name] = shared;
        }

        if (!Assert(moved.Count > 0)) return null;
        return _pools[vao.VaoId] = Assembled(vao, attributes, moved, indices);
    }

    // GlTap's vertex array tracking (asked of OpenGL once for pre-tap arrays); Buffer is the GL name until Assembled
    private static Attribute[] Formats(VAO vao)
    {
        var found = new List<Attribute>();
        if (GlTap.ArrayAt((uint)vao.VaoId) is not { } array) return [];
        for (var i = 0; i < Math.Min(vao.vaoSlotNumber, MaxAttributes); i++)
        {
            var a = array.Attributes[i];
            if (!a.Enabled) continue;
            found.Add(new Attribute(i, (int)a.Buffer, a.Size, (int)a.Type, a.Normalized, a.Integer)
            {
                Stride = a.Stride, Offset = a.Offset, Divisor = (int)a.Divisor
            });
        }

        return [.. found];
    }

    // Attributes point at the pool's vertex buffers by index; each GL name finds its buffer (BufferOf)
    private Pool Assembled(VAO vao, Attribute[] attributes, Dictionary<int, SharedBuffer> moved, int indices)
    {
        var vertices = moved.Where(m => (m.Key != vao.xyzVboId && m.Key != indices) ||
                                        attributes.Any(a => a.Buffer == m.Key))
            .Select(m => m.Value).ToArray();
        var rebound = new Attribute[attributes.Length];
        for (var i = 0; i < Math.Min(attributes.Length, MaxAttributes); i++)
        {
            var a = attributes[i];
            rebound[i] = a with { Buffer = Array.IndexOf(vertices, moved[a.Buffer]) };
        }

        var faces = moved.GetValueOrDefault(vao.xyzVboId);
        var elements = indices > 0 ? moved.GetValueOrDefault(indices) : null;
        foreach (var (name, buffer) in moved.ToArray().Bounded(MaxAttributes + 2))
        {
            _byName[name] = buffer;
            Held?.Invoke((uint)name);
        }

        return new Pool(vao, faces, vertices, rebound) { Indices = elements };
    }

    // The Vulkan mapping answered for it, else a buffer made here, filled unless fresh
    private SharedBuffer? Stood(VAO vao, int name, bool fresh, out string why)
    {
        why = "";
        if (Mapped?.Invoke((uint)name) is { } mapped) return SharedBuffer.Wrapping(device, mapped, name);
        var size = (int)GlTap.BufferSize((uint)name);
        var mapping = MappingOf(vao, name);
        var made = size > 0 ? SharedBuffer.Private(device, (ulong)size, name, mapping.Gl != 0, out why) : null;
        if (made is null)
        {
            why = why.Length > 0 ? why : $"buffer {name} has no storage";
            return null;
        }

        return fresh ? made : Owned.Kept(made, (pools: this, vao, mapping, size, name), static (made, s) =>
            s.pools.Seeded(s.vao, made, s.mapping, s.size) ? null : $"buffer {s.name} could not be read back", out why);
    }

    // OpenGL's own persistent mapping, made before Vulkan answered the engine's
    private static (int Slot, nint Gl) MappingOf(VAO vao, int name)
    {
        if (!vao.Persistent) return (-1, 0);
        for (var slot = 0; slot < Slots; slot++)
            if (Name(vao, slot) == name && Pointer(vao, slot) != 0)
                return (slot, Pointer(vao, slot));
        return (-1, 0);
    }

    // From OpenGL's mapping, whose engine pointer then moves to Vulkan's (back on Dispose), else read back via host
    // memory and waited for (adoption is rare)
    private bool Seeded(VAO vao, SharedBuffer made, (int Slot, nint Gl) mapping, long size)
    {
        if (mapping.Gl != 0 && Assert(made.Pointer != 0))
        {
            System.Buffer.MemoryCopy((void*)mapping.Gl, (void*)made.Pointer, size, size);
            Pointer(vao, mapping.Slot) = made.Pointer;
            _repointed.Add((vao, mapping.Slot, mapping.Gl));
            return true;
        }

        if (GlTap.LeanBuffer((uint)made.Gl)) return false; // lean: OpenGL holds none of it to read back
        using var host = HostBuffer.Create(device, (ulong)size, bulk: true);
        if (host is null || host.Take((int)size, out _, 16) is not 0) return false;
        using (GlTap.Quietly()) GL.GetNamedBufferSubData(made.Gl, IntPtr.Zero, (int)size, (IntPtr)host.Pointer(0));
        Readbacks++;
        var (from, to, copied) = (host.Buffer, made.Buffer, (long)made.Size);
        return device.Run(commands =>
        {
            var region = new Vk.BufferCopy { Size = (ulong)copied };
            VkApi.CmdCopyBuffer(commands, from, to, 1, &region);
        }, []);
    }

    internal static ref int Name(VAO vao, int slot)
    {
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

    internal static ref nint Pointer(VAO vao, int slot)
    {
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

    // Buffers go once frame is done (Collect; -1: at once); PoolTrim deletes empty pools every few seconds
    public void Release(VAO vao, long at)
    {
        if (!NotNull(vao)) return;
        _ = _refused.Remove(vao.VaoId); // its name may come back for another pool
        if (_pools.Remove(vao.VaoId, out var pool)) Unstood(pool, false, at);
    }

    private void Retire(SharedBuffer buffer, long at)
    {
        if (at >= 0 && _retired.Add(buffer, at)) return;
        if (at >= 0) _ = device.WaitIdle(); // too many waiting: this one goes now
        buffer.Dispose();
    }

    public void Collect(long done) => _retired.Collect(done);

    // Buffers go (PersistentMaps' stay theirs); back: OpenGL's mappings get the contents and pointers again
    private void Unstood(Pool pool, bool back, long at = -1)
    {
        if (!Assert(_repointed.Count <= MaxPools * Slots)) return;
        var count = _repointed.Count;
        for (var k = 0; k < Math.Min(count, MaxPools * Slots); k++)
        {
            var i = count - 1 - k; // from the last: removal keeps the rest where they are
            var (vao, slot, gl) = _repointed[i];
            if (!ReferenceEquals(vao, pool.Vao)) continue;
            var size = GlTap.BufferSizeOf((uint)Name(vao, slot)) ?? 0;
            if (back && size > 0) System.Buffer.MemoryCopy((void*)Pointer(vao, slot), (void*)gl, size, size);
            Pointer(vao, slot) = gl;
            _repointed.RemoveAt(i);
        }

        foreach (var buffer in Buffers(pool).ToArray().Bounded(MaxAttributes + 2))
        {
            _ = _byName.Remove(buffer.Gl);
            Retire(buffer, at);
        }
    }

    // Pointers handed back once the GPU is done with the buffers
    public void Dispose()
    {
        _ = device.WaitIdle();
        _retired.Clear();
        foreach (var pool in _pools.Values.ToArray().Bounded(MaxPools)) Unstood(pool, true);
        _repointed.Clear();
        _pools.Clear();
        _byName.Clear();
        _refused.Clear();
    }
}
