using System.Text.RegularExpressions;
using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Test.Gpu;

// The shared arena holds what is live: the engine's pools adopted and let go, the staging ring, buffers made and disposed. A block
// left empty goes once its frame is done on the GPU and OpenGL is done with it (its memory object deleted after its last buffer),
// so the exportable allocations every submission names come back to what they were.
[NonParallelizable]
public sealed unsafe partial class SharedArenaGpuTests
{
    private const ulong Mb = 1UL << 20;
    private static readonly int[] Sizes = [4, 4, 2, 2, 6, 2];

    // Pools of the engine's shape and assorted sizes come and go over many frames, as chunk pools do when a world streams in and
    // a renderer starts again; at every frame the arena holds at most two blocks more than the pools need, and once the pools are
    // gone and their frames done it holds nothing (before: every block made stayed until the device went)
    [Test]
    public void ChurnedPoolsLeaveTheArenaAsItWasOnceTheirFramesAreDone()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var (device, exported) = (rig.Device!, VkMemory.Exported);
        var pools = new TerrainPools(device);
        var (live, random) = (new List<(VAO Vao, ulong Bytes)>(), new Random(1234));
        var (peak, frame) = (0UL, 1L);
        for (; frame <= 60; frame++)
        {
            device.Arena.Collect(frame, frame - 3);
            for (var i = 0; i < random.Next(1, 3); i++)
            {
                var vertices = random.Next(50_000, 1_200_000);
                var vao = SsboPool(vertices);
                Assert.That(pools.Adopt(vao, false, out var why), Is.Not.Null, why);
                live.Add((vao, 24UL * (ulong)vertices));
            }

            for (var i = 0; live.Count > 10 && i < 3; i++) Release(pools, live, random.Next(live.Count));
            var needs = live.Aggregate(0UL, (sum, p) => sum + p.Bytes);
            peak = Math.Max(peak, Arena());
            Assert.That(Arena(), Is.LessThanOrEqualTo(needs + 2 * SharedArena.BlockBytes), $"frame {frame}");
        }

        for (var i = live.Count - 1; i >= 0; i--) Release(pools, live, i);
        for (var last = frame + 4; frame < last; frame++) device.Arena.Collect(frame, frame - 3);
        TestContext.Out.WriteLine($"peak {peak / Mb} MB; after: {VkMemory.Report()}");
        Assert.That(Arena(), Is.Zero, "every block went");
        Assert.That(VkMemory.Exported, Is.EqualTo(exported), "no exportable allocation left behind");
        pools.Dispose();
    }

    private static void Release(TerrainPools pools, List<(VAO Vao, ulong Bytes)> live, int at)
    {
        var vao = live[at].Vao;
        live.RemoveAt(at);
        pools.Release(vao);
        vao.Dispose();
    }

    // The renderer going (TerrainPools.Dispose hands the pools back to OpenGL) frees the blocks right away
    [Test]
    public void PoolsHandedBackFreeTheirBlocks()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var exported = VkMemory.Exported;
        VAO[] vaos = [.. Enumerable.Range(0, 12).Select(_ => SsboPool(500_000))];
        for (var round = 0; round < 3; round++)
        {
            var pools = new TerrainPools(rig.Device!);
            Assert.That(pools.Adopt([.. vaos.Select(v => (v, false))], out var why), Is.EqualTo(vaos.Length), why);
            Assert.That(rig.Device!.Arena.Live, Is.EqualTo(pools.Bytes), "the pools' buffers are what the arena carved");
            Assert.That(rig.Device.Arena.Blocks, Is.EqualTo(2), "12 MB pools packed tight: 144 MB in two blocks");
            pools.Dispose();
            Assert.That(rig.Device.Arena.Blocks, Is.Zero, $"round {round}");
            Assert.That(VkMemory.Exported, Is.EqualTo(exported));
        }

        foreach (var vao in vaos) vao.Dispose();
    }

    // Disposed, a buffer's range is free at once, its block not: OpenGL's buffer is deleted at once, the block's memory object
    // (and the memory) only once the frame it emptied in is done; a range taken before then keeps the block
    [Test]
    public void AnEmptyBlockGoesOnlyOnceItsFrameIsDone()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var arena = rig.Device!.Arena;
        arena.Collect(5, 2);
        var first = SharedBuffer.Create(rig.Device, 4 * Mb, out var why);
        Assert.That(first?.Carved, Is.Not.Null, why);
        var (block, gl) = (first!.Carved!.Value.Block, first.Gl);
        first.Dispose();
        Assert.That((GL.IsBuffer(gl), IsMemory(block.GlMemory), arena.Blocks, arena.Live), Is.EqualTo((false, true, 1, 0UL)));

        arena.Collect(6, 4);
        Assert.That((arena.Blocks, IsMemory(block.GlMemory)), Is.EqualTo((1, true)), "frame 5 is still in flight");
        using (var again = SharedBuffer.Create(rig.Device, 2 * Mb, out why))
        {
            Assert.That(again?.Carved?.Block, Is.SameAs(block), why);
            arena.Collect(9, 8);
            Assert.That(arena.Blocks, Is.EqualTo(1), "taken again: kept");
        }

        arena.Collect(10, 8);
        Assert.That(arena.Blocks, Is.EqualTo(1), "emptied in frame 9");
        arena.Collect(11, 9);
        Assert.That((arena.Blocks, arena.Freed, IsMemory(block.GlMemory)), Is.EqualTo((0, 1L, false)));
    }

    // Ranges given back merge with their free neighbours, and a range goes into the tightest hole that holds it
    [Test]
    public void FreedRangesAreReusedTightestFirst()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        SharedBuffer[] made = [.. Sizes.Select(mb => Made(device, mb))];
        var at = made.Select(b => b.Carved!.Value.At).ToArray();
        Assert.That(made.Select(b => b.Carved!.Value.Block).Distinct().Count(), Is.EqualTo(1));
        Assert.That(at, Is.Ordered, "carved one after the other");
        foreach (var i in (int[])[0, 1, 3]) made[i].Dispose(); // holes of 8 MB (merged) and 2 MB
        using var small = Made(device, 2);
        using var large = Made(device, 8);
        Assert.Multiple(() =>
        {
            Assert.That(small.Carved!.Value.At, Is.EqualTo(at[3]), "the 2 MB hole, not the front of the 8 MB one");
            Assert.That(large.Carved!.Value.At, Is.EqualTo(at[0]), "the 8 MB merged from two of 4");
            Assert.That(device.Arena.Blocks, Is.EqualTo(1));
            Assert.That(device.Arena.Ranges, Is.EqualTo(5));
        });
        foreach (var i in (int[])[2, 4, 5]) made[i].Dispose();
    }

    // The staging ring needs device-local memory only: it shares the block of the pools' mapped device-local memory, not a block of
    // its own (an exportable allocation more, mostly empty)
    [Test]
    public void ADeviceLocalBufferSharesAMappedDeviceLocalBlock()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var device = rig.Device!;
        using var mapped = SharedBuffer.Create(device, 8 * Mb, out var why, mapped: true);
        Assert.That(mapped?.Carved, Is.Not.Null, why);
        var block = mapped!.Carved!.Value.Block;
        Assume.That(device.MemoryType(1u << (int)block.Type, Vk.DeviceLocal), Is.EqualTo((int)block.Type),
            "no device-local memory the CPU can map here");
        using var plain = SharedBuffer.Create(device, GlStaging.RingBytes, out why);
        Assert.That(plain?.Carved?.Block, Is.SameAs(block), why);
        Assert.That(device.Arena.Blocks, Is.EqualTo(1));
    }

    // Once the free room of the pools' memory falls under the mark, the next block is allocated on a worker and taken in by a later
    // frame; the range that needs a new block takes it, nothing is allocated then. One nobody took goes with the arena.
    [Test]
    public void TheNextBlockIsMadeAheadAndTakenByTheRangeThatNeedsIt()
    {
        var exported = VkMemory.Exported;
        using (var rig = GpuRig.Open(GpuRig.Vulkan))
        {
            var (device, low) = (rig.Device!, SharedArena.BlockBytes / 2);
            var arena = device.Arena;
            Assert.That(arena.Reserve(low), Is.False, "no block yet: no memory type to make one of");
            SharedBuffer[] made = [Made(device, 30), Made(device, 30), Made(device, 30)];
            Assert.That((arena.Blocks, arena.Made), Is.EqualTo((1, 1L)), "38 MB left");
            Assert.That(Reserved(arena, low), Is.True, "taken in once the worker is done");
            Assert.That((arena.Blocks, arena.Made, arena.AheadMade), Is.EqualTo((2, 2L, 1L)));
            using var next = Made(device, 30);
            using var after = Made(device, 30);
            Assert.That((arena.Blocks, arena.Made), Is.EqualTo((2, 2L)), "the block made ahead took the range");
            Assert.That(after.Carved!.Value.Block, Is.Not.SameAs(made[0].Carved!.Value.Block));
            using var more = Made(device, 30);
            using var most = Made(device, 30);
            Assert.That(arena.Reserve(low), Is.False, "46 MB left: a worker makes a third");
            foreach (var buffer in made) buffer.Dispose();
        }

        Assert.That(VkMemory.Exported, Is.EqualTo(exported), "the third, untaken or still on the worker, went with the arena");
    }

    private static bool Reserved(SharedArena arena, ulong low)
    {
        _ = arena.Reserve(low);
        for (var i = 0; i < 500; i++)
        {
            if (arena.Reserve(low)) return true;
            Thread.Sleep(10);
        }

        return false;
    }

    private static SharedBuffer Made(VulkanDevice device, int mb)
    {
        var made = SharedBuffer.Create(device, (ulong)mb * Mb, out var why);
        Assert.That(made?.Carved, Is.Not.Null, why);
        return made!;
    }

    // A range too large for the ring gets an exportable buffer of its own, not arena memory, and gives it back with its frame
    [Test]
    public void OversizedStagingTakesNothingOfTheArenaAndGoesWithItsFrame()
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        var (arena, exported) = (rig.Device!.Arena, VkMemory.Exported);
        var staging = new GlStaging(rig.Device);
        Assert.That(staging.Take(Mb, 1, out var why), Is.Not.Null, why);
        Assert.That((arena.Blocks, arena.Live), Is.EqualTo((1, GlStaging.RingBytes)), "the ring");
        for (var frame = 2; frame < 40; frame++)
        {
            Assert.That(staging.Take(40 * Mb, frame, out why), Is.Not.Null, why);
            staging.Collect(frame - 1);
            Assert.That((arena.Blocks, VkMemory.Exported), Is.EqualTo((1, exported + 2)), $"frame {frame}");
        }

        staging.Collect(long.MaxValue - 1);
        Assert.That(VkMemory.Exported, Is.EqualTo(exported + 1), "the ring's block alone");
        staging.Dispose();
        Assert.That((arena.Blocks, VkMemory.Exported), Is.EqualTo((0, exported)), "the ring's block went with the staging");
    }

    private static ulong Arena() =>
        ArenaBytes().Match(VkMemory.Report()) is { Success: true } m
            ? ulong.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * Mb
            : 0;

    [GeneratedRegex(@"Shared\.Arena (\d+) MB")]
    private static partial Regex ArenaBytes();

    private static bool IsMemory(uint memory)
    {
        var isMemory = (delegate* unmanaged<uint, byte>)GLFW.GetProcAddress("glIsMemoryObjectEXT");
        Assert.That(isMemory != null);
        return isMemory(memory) != 0;
    }

    // AllocateEmptySSBOMesh for the opaque pass: faces of 16 bytes a vertex, light and one custom int a vertex mapped for good
    private static VAO SsboPool(int vertices)
    {
        const int storage = 0x1C2, map = 0xC2;
        var vao = new VAO { VaoId = GL.GenVertexArray(), vaoSlotNumber = 2, Persistent = true };
        GL.BindVertexArray(vao.VaoId);
        vao.xyzVboId = GL.GenBuffer();
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, vao.xyzVboId);
        GL.BufferStorage(BufferTarget.ShaderStorageBuffer, 16 * vertices, IntPtr.Zero, BufferStorageFlags.DynamicStorageBit);
        vao.rgbaVboId = Mapped(4 * vertices, out vao.rgbaPtr);
        GL.VertexAttribPointer(0, 4, VertexAttribPointerType.UnsignedByte, true, 0, 0);
        vao.customDataIntVboId = Mapped(4 * vertices, out vao.customDataIntPtr);
        GL.VertexAttribIPointer(1, 1, VertexAttribIntegerType.Int, 0, IntPtr.Zero);
        GL.EnableVertexAttribArray(0);
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);
        GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        return vao;

        static int Mapped(int bytes, out nint pointer)
        {
            var buffer = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
            GL.BufferStorage(BufferTarget.ArrayBuffer, bytes, IntPtr.Zero, (BufferStorageFlags)storage);
            pointer = GL.MapBufferRange(BufferTarget.ArrayBuffer, IntPtr.Zero, bytes, (MapBufferAccessMask)map);
            return buffer;
        }
    }
}
