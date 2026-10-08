using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class TerrainPoolsGpuTests
{
    private const int Faces = 1024, FaceBytes = 16 * 4 * Faces;
    private static readonly uint[] Before = [.. Enumerable.Range(0, 64).Select(i => (uint)i * 7919u)];
    private static readonly uint[] After = [.. Enumerable.Range(0, 64).Select(i => ~(uint)i)];

    private static readonly (int, int, int, bool, int)[] Formats =
    [
        (0, 4, (int)VertexAttribPointerType.UnsignedByte, true, 4),
        (1, 2, (int)VertexAttribPointerType.UnsignedShort, true, 4)
    ];

    [Test]
    public void AnAdoptedPoolKeepsItsContentsAndTheEngineWritesIntoVulkansMemory()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var vao = Topsoil();
        GL.NamedBufferSubData(vao.xyzVboId, 256, Before.Length * 4, Before);
        var (faces, light, shorts) = (vao.xyzVboId, vao.rgbaVboId, vao.customDataShortVboId);
        using var pools = new TerrainPools(rig.Device!);

        var pool = pools.Adopt(vao, false, out var why);
        Assert.That(pool, Is.Not.Null, why);
        Assert.That(pools.Adopt(vao, false, out _), Is.SameAs(pool), "adopted once");
        Assert.That(vao.xyzVboId, Is.EqualTo(pool!.Faces!.Gl));
        int[] old = [faces, light, shorts];
        Assert.That(old.Select(GL.IsBuffer), Is.All.False, "the engine's buffers are gone");
        Assert.That(pool.Attributes.Select(a => (a.Location, a.Size, a.Type, a.Normalized, a.Stride)),
            Is.EqualTo(Formats));
        int[] shared = [vao.rgbaVboId, vao.customDataShortVboId];
        Assert.That(pool.Vertices.Select(v => v.Gl), Is.EquivalentTo(shared));
        var bound = new int[1];
        GL.GetVertexArrayIndexed(vao.VaoId, 0, (VertexArrayIndexedParameter)0x889F, bound);
        Assert.That(bound[0], Is.EqualTo(vao.rgbaVboId), "the VAO draws from the shared buffer");
        Assert.That(Gl(vao.xyzVboId, 256), Is.EqualTo(Before), "copied over");

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, vao.xyzVboId);
        GL.BufferSubData(BufferTarget.ShaderStorageBuffer, 512, After.Length * 4, After);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        Assert.That(rig.Read(pool.Faces, 512, 64), Is.EqualTo(After), "the engine's write reaches Vulkan");
        Assert.That(rig.Read(pool.Faces, 256, 64), Is.EqualTo(Before));

        pools.Release(vao);
        int[] released = [vao.xyzVboId, vao.rgbaVboId, vao.customDataShortVboId];
        Assert.That(released, Is.All.Zero);
        Assert.That(pools.Count, Is.Zero);
        vao.Dispose();
    }

    // A pool as the engine makes it since 1.22 (AllocateEmptyMesh, not static): persistently mapped buffers the engine writes
    // through the VAO's pointers only. Adopted, the pointers lead into the shared memory: what was there before stays, what the
    // engine writes through them after reaches Vulkan. Disposed, the pool is the engine's again: fresh mapped GL buffers holding
    // what the shared ones held, the pointers on them.
    [Test]
    public unsafe void APersistentlyMappedPoolIsWrittenThroughItsPointersBeforeDuringAndAfter()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var vao = Persistent();
        Write(vao.xyzPtr, 256, Before);
        Write(vao.indicesPtr, 0, Before);
        var pools = new TerrainPools(rig.Device!);
        try
        {
            var pool = pools.Adopt(vao, true, out var why);
            Assert.That(pool, Is.Not.Null, why);
            Assert.That(pool!.Faces!.Pointer, Is.Not.Zero, "mapped shared memory");
            Assert.That(vao.xyzPtr, Is.EqualTo(pool.Faces.Pointer), "the engine's pointer moved along");
            Assert.That(vao.indicesPtr, Is.EqualTo(pool.Indices!.Pointer), "the classic indices too");
            Assert.That(Gl(vao.xyzVboId, 256), Is.EqualTo(Before), "copied over");
            Write(vao.xyzPtr, 512, After);
            Assert.That(rig.Read(pool.Faces, 512, 64), Is.EqualTo(After), "the engine's write reaches Vulkan");
            Assert.That(rig.Read(pool.Indices, 0, 64), Is.EqualTo(Before));
        }
        finally
        {
            pools.Dispose();
        }

        Assert.That(GL.IsBuffer(vao.xyzVboId), Is.True, "a GL buffer of the engine's again");
        Assert.That(Gl(vao.xyzVboId, 512), Is.EqualTo(After), "copied back");
        Assert.That(Gl(vao.vboIdIndex, 0), Is.EqualTo(Before));
        Write(vao.xyzPtr, 768, Before);
        GL.Finish();
        Assert.That(Gl(vao.xyzVboId, 768), Is.EqualTo(Before), "the pointer writes the engine's buffer");
        var bound = new int[1];
        GL.GetVertexArrayIndexed(vao.VaoId, 0, (VertexArrayIndexedParameter)0x889F, bound);
        Assert.That(bound[0], Is.EqualTo(vao.xyzVboId), "the VAO draws from it");
        vao.Dispose();
    }

    // A pool adopted as the engine makes it (VulkanRenderer.Birth) holds nothing yet: nothing is copied and nothing waits for the
    // GPU, which is still busy with work given before when the adoption returns. Adopted later, the copy waits for all of it. The
    // engine's writes through the moved pointers reach Vulkan either way.
    [Test]
    public unsafe void AFreshPoolIsAdoptedWithoutWaitingForTheGpu()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var busy = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, busy);
        GL.BufferStorage(BufferTarget.CopyWriteBuffer, BusyBytes, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
        var pools = new TerrainPools(rig.Device!);
        VAO fresh = Persistent(), written = Persistent();
        try
        {
            Write(written.xyzPtr, 256, Before);
            var (fence, pool) = (Busy(busy), pools.Adopt(fresh, true, out var why, fresh: true));
            Assert.That(pool, Is.Not.Null, why);
            Assert.That(Signalled(fence), Is.False, "the GPU is still busy: the fresh adoption did not wait");
            Assert.That(fresh.xyzPtr, Is.EqualTo(pool!.Faces!.Pointer), "the engine's pointer moved along");
            Write(fresh.xyzPtr, 512, After);
            Write(fresh.indicesPtr, 0, Before);
            Assert.That(rig.Read(pool.Faces, 512, 64), Is.EqualTo(After), "the engine's first write reaches Vulkan");
            Assert.That(rig.Read(pool.Indices!, 0, 64), Is.EqualTo(Before));

            fence = Busy(busy);
            Assert.That(pools.Adopt(written, true, out why), Is.Not.Null, why);
            Assert.That(Signalled(fence), Is.True, "a pool with contents is copied, and the copy waited for the GPU");
            Assert.That(Gl(written.xyzVboId, 256), Is.EqualTo(Before), "copied over");
        }
        finally
        {
            pools.Dispose();
            GL.DeleteBuffer(busy);
        }

        fresh.Dispose();
        written.Dispose();
    }

    private const int BusyBytes = 256 << 20, BusyClears = 64;

    // A fence behind some ten milliseconds of GPU work and more
    private static IntPtr Busy(int buffer)
    {
        for (var i = 0; i < BusyClears; i++)
            GL.ClearNamedBufferData(buffer, PixelInternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt,
                IntPtr.Zero);
        var fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
        GL.Flush();
        return fence;
    }

    private static bool Signalled(IntPtr fence)
    {
        var signalled = GL.ClientWaitSync(fence, ClientWaitSyncFlags.None, 0) is WaitSyncStatus.AlreadySignaled
            or WaitSyncStatus.ConditionSatisfied;
        GL.DeleteSync(fence);
        return signalled;
    }

    private static unsafe void Write(nint pointer, int offset, uint[] values)
    {
        Assert.That(pointer, Is.Not.Zero);
        values.AsSpan().CopyTo(new Span<uint>((byte*)pointer + offset, values.Length));
    }

    // AllocateEmptyMesh with persistent storage: xyz at attribute 0, the indices of its own, both mapped for writing
    private static VAO Persistent()
    {
        const int storage = 0x1C2, map = 0xC2;
        var vao = new VAO { VaoId = GL.GenVertexArray(), vaoSlotNumber = 1, Persistent = true };
        GL.BindVertexArray(vao.VaoId);
        vao.xyzVboId = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, vao.xyzVboId);
        GL.BufferStorage(BufferTarget.ArrayBuffer, FaceBytes, IntPtr.Zero, (BufferStorageFlags)storage);
        vao.xyzPtr = GL.MapBufferRange(BufferTarget.ArrayBuffer, IntPtr.Zero, FaceBytes, (MapBufferAccessMask)map);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, 0);
        GL.EnableVertexAttribArray(0);
        vao.vboIdIndex = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, vao.vboIdIndex);
        GL.BufferStorage(BufferTarget.ElementArrayBuffer, 4096, IntPtr.Zero, (BufferStorageFlags)storage);
        vao.indicesPtr = GL.MapBufferRange(BufferTarget.ElementArrayBuffer, IntPtr.Zero, 4096, (MapBufferAccessMask)map);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        return vao;
    }

    private static uint[] Gl(int buffer, int offset)
    {
        var read = new uint[64];
        GL.GetNamedBufferSubData(buffer, offset, read.Length * 4, read);
        return read;
    }

    // AllocateEmptySSBOMesh with the topsoil pass's customs: storage for Faces faces, light and two normalized shorts per vertex
    private static VAO Topsoil()
    {
        var vao = new VAO { VaoId = GL.GenVertexArray(), vaoSlotNumber = 2 };
        GL.BindVertexArray(vao.VaoId);
        vao.xyzVboId = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, vao.xyzVboId);
        GL.BufferStorage(BufferTarget.ShaderStorageBuffer, FaceBytes, IntPtr.Zero,
            BufferStorageFlags.DynamicStorageBit);
        vao.rgbaVboId = Vertices(4 * 4 * Faces);
        GL.VertexAttribPointer(0, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
        vao.customDataShortVboId = Vertices(2 * 2 * 4 * Faces);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.UnsignedShort, true, 4, 0);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        return vao;
    }

    private static int Vertices(int bytes)
    {
        var buffer = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, bytes, IntPtr.Zero, BufferUsageHint.StaticDraw);
        return buffer;
    }
}
