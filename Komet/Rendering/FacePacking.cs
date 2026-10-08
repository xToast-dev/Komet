using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// UpdateSSBOMesh packs every quad into a FaceData one after the other on the main thread; a chunk part has thousands, and a frame
// pooling new chunks spends most of its upload time there. The workers pack through the engine's own FaceData constructor after
// its UV checks.
internal delegate bool FacesThrough(VAO vao, int buffer, long offset, ReadOnlySpan<byte> data);

// Room for an upload of bytes at a byte offset into the pool's buffer, written in place, then committed (FacePacking.Committed):
// faces packed straight into the memory the upload copies from; null leaves the upload to Through or OpenGL
internal unsafe delegate byte* FacesRoom(VAO vao, int buffer, long offset, int bytes);

internal static class FacePacking
{
    private const int ShaderStorage = 37074, ArrayBuffer = 34962, MaxFaces = 1 << 22;
    private const int ParallelFaces = 4096, FacesPerPart = 2048, MaxParts = 32;
    private const float Low = -1.5E-05f, High = 1.000015f;
    private const int MaxPackMs = 5000;

    private static readonly Action<int> PackJob = PackPart;
    private static FaceData[] _faces = [];
    private static int[] _pruned = [];

    private static float[] _xyz = [], _uv = [];
    private static int[] _flags = [];
    private static int[]? _ints;
    private static int _stride, _count, _parts;

    public static bool Enabled { get; set; } = true;

    // Takes the uploads into the pool instead of OpenGL (Vulkan's renderer): true when it did
    public static FacesThrough? Through { get; set; }

    private static VAO? _uploading;

    // Gives room for the faces and makes the upload once packed (with Through, by the same hand)
    public static FacesRoom? Room { get; set; }
    public static Action? Committed { get; set; }

    private static unsafe FaceData* _into; // where the parts pack: the room given, or _faces pinned
    public static long Parallel { get; private set; } // uploads the workers helped pack

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, float[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, int[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, short[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "updateVAO")]
    private static extern void UpdateVao(ClientPlatformWindows platform, byte[] data, int offset, int count, int vboId,
        nint vboPtr, bool pers);

    public static void Install(Harmony harmony)
    {
        var upload = AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), nameof(ClientPlatformWindows.UpdateSSBOMesh));
        var prefix = new HarmonyMethod(typeof(FacePacking), nameof(Uploading)) { priority = Priority.Last };
        if (!NotNull(harmony) || !NotNull(upload) || !Assert(Il.Binds(upload, prefix.method)) ||
            !Assert(Marshal.SizeOf<FaceData>() == 64)) return;
        _ = NotNull(harmony.Patch(upload, prefix));
        var recycling = AccessTools.DeclaredMethod(typeof(MeshDataRecycler), nameof(MeshDataRecycler.DoRecycling));
        if (NotNull(recycling))
            _ = harmony.Patch(recycling, postfix: new HarmonyMethod(typeof(FacePacking), nameof(Recycling)));
    }

    internal static bool Uploading(ClientPlatformWindows __instance, MeshRef modelRef, MeshData data)
    {
        if (!Enabled || data?.xyz is null || modelRef is not VAO vao || !NotNull(__instance) ||
            (__instance.GlErrorChecking && __instance.GlDebugMode)) return true;
        var vertices = data.VerticesCount;
        var faces = vertices / 4;
        if (!Readable(data, faces) || !Assert(vertices >= 0)) return true; // the engine throws as it would
        var (offset, bytes) = (data.XyzOffset / 12 * 16, 16 * vertices);
        FaceSorting.Uploaded(vao.VaoId, data);
        _uploading = vao;
        var bound = false;
        if (!PackedInPlace(vao, data, (offset, bytes), faces))
        {
            Pack(data, faces);
            if (!Taken(vao.xyzVboId, offset, MemoryMarshal.AsBytes(_faces.AsSpan())[..bytes]))
            {
                GL.BindBuffer((BufferTarget)ShaderStorage, vao.xyzVboId);
                GL.BufferSubData((BufferTarget)ShaderStorage, offset, bytes, _faces);
                bound = true;
            }
        }

        // As the engine's upload leaves it, but only after a bind of ours: the unbind alone held the main thread in the driver
        // for 9 % of the frame while chunks streamed in, and an upload taken by Vulkan never touched the binding
        if (bound) GL.BindBuffer((BufferTarget)ShaderStorage, 0);
        bool opengl;
        try
        {
            opengl = Attributes(__instance, vao, data);
        }
        finally
        {
            Release(); // counted in _packing already: a pack never queued would hold Settle for MaxPackMs
            _uploading = null;
        }

        if (opengl) GL.BindBuffer((BufferTarget)ArrayBuffer, 0); // bound only by an upload OpenGL made
        vao.IndicesCount = data.IndicesCount;
        return false;
    }

    // Every array the packing reads is long enough for the faces; else the engine's loop throws where it would
    private static bool Readable(MeshData data, int faces)
    {
        if (!Assert(faces >= 0) || faces > MaxFaces || data.Uv is not { } uv || data.Flags is not { } flags) return false;
        var (ints, stride) = data.CustomInts is { Count: > 0 } c ? (c.Values, c.InterleaveStride / 4) : (null, 1);
        var last = 4 * faces - 4;
        return faces == 0 || (data.xyz.Length >= 3 * last + 12 && uv.Length >= 2 * last + 6 && flags.Length >= last + 4 &&
                              (ints is null || (stride > 0 && ints.Length > last * stride)));
    }

    // The copy is recorded now and runs only after Settle (which whoever submits it calls first). The mesh's arrays stay its own
    // until then: a disposed mesh goes to the recycler, which hands out nothing before Settle (Recycling); its custom ints, which a
    // clone may take over at once (MeshRecycle), are copied here.
    private static unsafe bool PackedInPlace(VAO vao, MeshData data, (int Offset, int Bytes) at, int faces)
    {
        if (Room is not { } room || Committed is not { } committed || !Assert(faces * 64 <= at.Bytes) ||
            !NotNull(data.xyz)) return false;
        var into = room(vao, vao.xyzVboId, at.Offset, at.Bytes);
        if (into == null) return false;
        var ints = Spared(faces);
        if (data.CustomInts is { Count: > 0 } custom)
        {
            var (values, stride) = (custom.Values, custom.InterleaveStride / 4);
            for (var f = 0; f < Math.Min(faces, MaxFaces); f++) ints[f] = values[4 * f * stride];
        }
        else Array.Clear(ints, 0, faces);

        _ = Interlocked.Increment(ref _packing);
        _pending = new Job(data.xyz, data.Uv, data.Flags, ints, faces, (nint)into); // queued once Attributes added its colours
        committed();
        return Assert(faces >= 0);
    }

    // Rgba rides along: a basic array like xyz, so it stays the mesh's until Settle. The main thread's copy of it into the staging
    // was 6 % of the frame while chunks streamed in.
    private sealed class Job(float[] xyz, float[] uv, int[] flags, int[] maps, int quads, nint destination)
    {
        public float[] Xyz { get; } = xyz;
        public float[] Uv { get; } = uv;
        public int[] Flags { get; } = flags;
        public int[] Maps { get; } = maps;
        public int Quads { get; } = quads;
        public nint Destination { get; } = destination;
        public (byte[]? From, int Bytes, nint Into) Rgba { get; set; }
    }

    private static Job? _pending; // the pack Uploading queues once its colours are in, main thread only

    // Packs no thread has started: the shared pool runs them, and whoever waits in Settle takes them first. In the pool alone they
    // waited behind the integrated server's world generation (Parallel.For) while the main thread spun.
    private static readonly System.Collections.Concurrent.ConcurrentQueue<Job> Queued = new();
    private static readonly Action<int> RunQueued = static _ => RunOne();
    private const int MaxQueued = 4096;

    private static void Release()
    {
        if (_pending is not { } job || !Assert(Volatile.Read(ref _packing) > 0)) return;
        _pending = null;
        Queued.Enqueue(job);
        _ = ThreadPool.UnsafeQueueUserWorkItem(RunQueued, 0, false);
    }

    private static void RunOne()
    {
        if (Queued.TryDequeue(out var job) && NotNull(job) && Assert(job.Quads >= 0)) Background(job);
    }

    // The colours into the pending pack's staging; false leaves them to Send
    private static unsafe bool Colours(VAO vao, byte[] rgba, int offset, int count)
    {
        if (_pending is not { } job || Room is not { } room || Committed is not { } committed || count <= 0 ||
            !Assert(count <= rgba.Length)) return false;
        var into = room(vao, vao.rgbaVboId, offset, count);
        if (into == null) return false;
        job.Rgba = (rgba, count, (nint)into);
        committed();
        return Assert(offset >= 0);
    }

    private static int _packing, _failures; // background packs under way; those that found their mesh changed

    // Colour map indices of finished packs for the next ones: rented on the engine's thread, returned on a pool thread
    private static readonly System.Collections.Concurrent.ConcurrentQueue<int[]> Spare = new();
    private const int MaxSpares = 64;

    private static int[] Spared(int faces)
    {
        var length = Math.Max(faces, 1);
        for (var i = 0; i < Math.Min(Spare.Count, MaxSpares) && Spare.TryDequeue(out var spare); i++)
        {
            if (spare.Length >= length) return spare;
            if (!Assert(spare.Length > 0)) break;
            Spare.Enqueue(spare); // kept for a smaller mesh: dropped, it left the pool allocating anew
        }

        return new int[Math.Max((int)BitOperations.RoundUpToPowerOf2((uint)length), 4096)];
    }

    private static unsafe void Background(Job job)
    {
        try
        {
            var into = (FaceData*)job.Destination;
            if (!Assert(into != null) || !Assert(job.Maps.Length >= job.Quads)) return;
            for (var f = 0; f < Math.Min(job.Quads, MaxFaces); f++)
                into[f] = Face(job.Xyz, job.Uv, job.Flags, job.Maps[f], f);
            if (job.Rgba is ({ } rgba, var bytes, var at)) rgba.AsSpan(0, bytes).CopyTo(new Span<byte>((void*)at, bytes));
        }
        catch (Exception e) when (e is IndexOutOfRangeException or NullReferenceException)
        {
            _ = Interlocked.Increment(ref _failures); // Readable checked the arrays: the mesh changed under the pack
            _ = Assert(Volatile.Read(ref _failures) == 0);
        }
        finally
        {
            if (Assert(job.Maps.Length > 0) && Spare.Count < MaxSpares) Spare.Enqueue(job.Maps);
            _ = Interlocked.Decrement(ref _packing);
        }
    }

    // Waits for every background pack: before its copy is submitted, the staging is read, or the recycler hands out a mesh
    public static void Settle()
    {
        var packing = Volatile.Read(ref _packing);
        if (packing == 0 || !Assert(packing > 0)) return;
        for (var i = 0; i < MaxQueued && Queued.TryDequeue(out var job); i++) Background(job);
        var done = SpinWait.SpinUntil(() => Volatile.Read(ref _packing) == 0, MaxPackMs);
        _ = Assert(done);
    }

    // No mesh a pack still reads is handed out again. After DoRecycling's drain, not before: the main thread uploads and disposes
    // while the tessellation thread drains, so a mesh can join the lists after a wait at the start; it was disposed after its upload
    // counted its pack, so a wait at the end covers every mesh the drain took.
    internal static void Recycling()
    {
        Settle();
        _ = Assert(Volatile.Read(ref _packing) >= 0);
    }

    private static unsafe void Pack(MeshData data, int faces)
    {
        var size = (16 * data.VerticesCount + 63) / 64; // what the upload reads
        if (_faces.Length < size) _faces = new FaceData[Math.Max(size, _faces.Length * 2)];
        fixed (FaceData* into = _faces)
        {
            if (!Assert(into != null) || !NotNull(data)) return;
            _into = into;
            (_xyz, _uv, _flags, _count) = (data.xyz, data.Uv, data.Flags, faces);
            (_ints, _stride) = data.CustomInts is { Count: > 0 } c ? (c.Values, c.InterleaveStride / 4) : (null, 1);
            _parts = Math.Clamp(faces / FacesPerPart, 1, MaxParts);
            var helped = faces >= ParallelFaces && _parts > 1 && WorkerPool.Free &&
                         WorkerPool.RunFrame(PackJob, _parts, _parts - 1) == WorkerPool.FrameResult.Done;
            if (helped && Counting.Hud) Parallel++;
            if (!helped)
                for (var part = 0; part < Math.Min(_parts, MaxParts); part++) PackPart(part); // a failed batch runs again
            (_xyz, _uv, _flags, _ints) = ([], [], [], null);
            _into = null;
        }

        _ = Assert(_faces.Length >= faces);
    }

    private static unsafe void PackPart(int part)
    {
        var faces = _into;
        if (!Index(part, _parts) || !Assert(faces != null)) return;
        var (from, to) = (_count * part / _parts, _count * (part + 1) / _parts);
        var (xyz, uv, flags, ints, stride) = (_xyz, _uv, _flags, _ints, _stride);
        for (var f = from; f < Math.Min(to, MaxFaces); f++)
            faces[f] = Face(xyz, uv, flags, ints is not null ? ints[4 * f * stride] : 0, f);

        _ = Assert(to <= _count);
    }

    // One face as the engine's loop packs it, after its UV checks
    private static FaceData Face(float[] xyz, float[] uv, int[] flags, int colorMap, int f)
    {
        var i = 4 * f;
        float u1 = uv[i * 2], v1 = uv[i * 2 + 1], v1b = uv[i * 2 + 3], u2 = uv[i * 2 + 4], v2 = uv[i * 2 + 5];
        if (u1 < Low || u1 > High || v1 < Low || v1 > High) (u1, v1) = (0, 0);
        if (u2 < Low || u2 > High || v2 < Low || v2 > High) (u2, v2) = (0, 0);
        var rotate = Same(v1, v1b) && Same(u2, uv[i * 2 + 2]);
        return new FaceData(xyz, i * 3, u1, v1, u2 - u1, v2 - v1, flags, i, colorMap, rotate);
    }

    // As the engine compares the UV corners
    private static bool Same(float a, float b) => a.CompareTo(b) == 0 && !float.IsNaN(a);

    // The engine's uploads of the other attributes; a persistent mapping is filled as a block. True when OpenGL made one.
    private static bool Attributes(ClientPlatformWindows platform, VAO vao, MeshData data)
    {
        if (!NotNull(vao) || !NotNull(data)) return false;
        var (persistent, opengl) = (vao.Persistent, false);
        var colours = data.Rgba is { } rgba && data.RgbaCount > 0 && !Colours(vao, rgba, data.RgbaOffset, data.RgbaCount);
        Release(); // before any upload OpenGL makes, the colours' own included: the tap settles every pack first, this one too
        if (colours) opengl |= Send(platform, data.Rgba!, (data.RgbaOffset, data.RgbaCount), (vao.rgbaVboId, vao.rgbaPtr), persistent);
        if (data.CustomFloats is { Count: > 0 } floats)
            opengl |= Send(platform, floats.Values, (floats.BaseOffset, floats.Count),
                (vao.customDataFloatVboId, vao.customDataFloatPtr), persistent);
        if (data.CustomShorts is { Count: > 0 } shorts)
            opengl |= Send(platform, shorts.Values, (shorts.BaseOffset, shorts.Count),
                (vao.customDataShortVboId, vao.customDataShortPtr), persistent);
        if (data.CustomInts is { Count: > 0, InterleaveStride: > 4 } ints) opengl |= Pruned(platform, vao, ints);
        if (data.CustomBytes is { Count: > 0 } bytes)
            opengl |= Send(platform, bytes.Values, (bytes.BaseOffset, bytes.Count),
                (vao.customDataByteVboId, vao.customDataBytePtr), persistent);
        return opengl;
    }

    // Two custom ints per vertex: the second of each goes up, at half the offset
    private static bool Pruned(ClientPlatformWindows platform, VAO vao, CustomMeshDataPartInt ints)
    {
        if (ints.InterleaveStride / 4 != 2) throw new InvalidOperationException("We are assuming 2 customInts per vertex if it is not 1");
        var n = ints.Count / 2;
        if (!Assert(n >= 0) || !NotNull(ints.Values)) return false;
        if (_pruned.Length < n) _pruned = new int[n];
        var values = ints.Values;
        for (var j = 0; j < Math.Min(n, MaxFaces * 4); j++) _pruned[j] = values[j * 2 + 1];
        return Send(platform, _pruned, (ints.BaseOffset / 2, n), (vao.customDataIntVboId, vao.customDataIntPtr), vao.Persistent);
    }

    // True when OpenGL made the upload (and bound GL_ARRAY_BUFFER for it)
    private static bool Send<T>(ClientPlatformWindows platform, T[] data, (int Offset, int Count) at, (int Vbo, nint Ptr) to,
        bool persistent) where T : unmanaged
    {
        if (!NotNull(data) || !Assert(at.Count >= 0) || !Assert(at.Count <= data.Length)) return false;
        var values = data.AsSpan(0, at.Count);
        if (Staged(to.Vbo, at.Offset, MemoryMarshal.AsBytes(values))) return false;
        if (persistent && Mapped(to, at.Offset, at.Count * Unsafe.SizeOf<T>()))
        {
            values.CopyTo(Target<T>(to.Ptr, at));
            return false;
        }

        if (!persistent && Taken(to.Vbo, at.Offset, MemoryMarshal.AsBytes(values))) return false;
        Upload(platform, data, at, to, persistent);
        return true;
    }

    // The engine's updateVAO for the attribute array types it uploads (PoolUpdates hands OpenGL its uploads through here too)
    internal static void Upload(ClientPlatformWindows platform, Array data, (int Offset, int Count) at, (int Vbo, nint Ptr) to,
        bool persistent)
    {
        switch (data)
        {
            case float[] floats: UpdateVao(platform, floats, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
            case int[] ints: UpdateVao(platform, ints, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
            case short[] shorts: UpdateVao(platform, shorts, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
            case byte[] bytes: UpdateVao(platform, bytes, at.Offset, at.Count, to.Vbo, to.Ptr, persistent); break;
        }

        _ = Assert(data is float[] or int[] or short[] or byte[]);
    }

    // An upload OpenGL would make, taken by Through instead (GL_ARRAY_BUFFER is bound to 0 at the upload's end either way)
    private static bool Taken(int buffer, long offset, ReadOnlySpan<byte> data)
    {
        if (Through is not { } through || _uploading is not { } vao || !Assert(offset >= 0)) return false;
        return Assert(buffer >= 0) && through(vao, buffer, offset, data);
    }

    // A persistent attribute the block copy can take. No bind: nothing in OpenGL follows the copy, and the binding is no VAO state.
    private static bool Mapped((int Vbo, nint Ptr) to, int offset, int bytes) =>
        to.Ptr != 0 && Assert(offset >= 0) && Assert(bytes >= 0) && Assert(to.Vbo >= 0);

    // Into the staging Vulkan copies the pool's faces from, as Room hands it out: the persistent mapping the engine writes is the
    // pool's memory in VRAM, and the main thread's writes across the bus (and the faults of each newly touched page) cost about
    // 6 % of the frame while chunks streamed in. Host memory and one GPU copy, ordered as the faces are.
    private static unsafe bool Staged(int buffer, long offset, ReadOnlySpan<byte> bytes)
    {
        if (_uploading is not { } vao || Room is not { } room || Committed is not { } committed || bytes.Length == 0 ||
            !Assert(offset >= 0)) return false;
        var into = room(vao, buffer, offset, bytes.Length);
        if (into == null) return false;
        bytes.CopyTo(new Span<byte>(into, bytes.Length));
        committed();
        return Assert(buffer > 0);
    }

    // The mapping from the attribute's byte offset (updateVAO steps a typed pointer by offset / size), count elements
    private static unsafe Span<T> Target<T>(nint pointer, (int Offset, int Count) at) where T : unmanaged
    {
        _ = Assert(pointer != 0) && Assert(at.Count >= 0);
        return new Span<T>((T*)pointer + at.Offset / sizeof(T), at.Count);
    }
}
