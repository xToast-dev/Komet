using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// Every exportable allocation is one more kernel buffer object that every Vulkan submission of the device names and the kernel
// validates - a few hundred made each submission cost a third of a millisecond - so shared buffers take a range of a large block
// instead, imported into OpenGL once as a memory object. A range goes into the tightest free range that holds it (best fit: the
// full blocks fill up, the emptiest ones empty out). A block left empty goes - its OpenGL memory object, then the memory - once
// the frame it emptied in is done on the GPU (Collect) and so is every OpenGL command before its last buffer was deleted (a
// fence); a range taken from it before then keeps it.
internal sealed unsafe class SharedArena(VulkanDevice device) : IDisposable
{
    public const ulong BlockBytes = 128UL << 20;
    private const int MaxBlocks = 128, MaxRanges = 4096;
    private const ulong TrimNs = 2_000_000_000;

    public sealed class Block(ulong memory, uint glMemory, uint type)
    {
        public ulong Memory { get; } = memory;
        public uint GlMemory { get; } = glMemory;
        public uint Type { get; } = type;
        public List<(ulong At, ulong Size)> Free { get; } = [(0, BlockBytes)];
        public int Taken { get; set; }
        public nint Mapped { get; set; }

        // Empty since that frame, behind that OpenGL fence; no fence while ranges are taken
        public (long Frame, IntPtr Fence) Emptied { get; set; }
    }

    private readonly List<Block> _blocks = [];
    private long _frame; // the newest frame begun, as Collect last heard
    private int _empty; // blocks with a fence

    public int Blocks => _blocks.Count;
    public ulong Allocated => (ulong)_blocks.Count * BlockBytes;
    public ulong Live { get; private set; } // carved
    public int Ranges { get; private set; }
    public long Made { get; private set; }
    public long Freed { get; private set; }

    public nint Map(Block block)
    {
        if (!NotNull(block) || !Assert(_blocks.Contains(block))) return 0;
        if (block.Mapped != 0) return block.Mapped;
        void* mapped = null;
        if (VkApi.MapMemory(device.Handle, block.Memory, 0, BlockBytes, 0, &mapped) == Vk.Success) block.Mapped = (nint)mapped;
        return block.Mapped;
    }

    // type: a new block's; also: the types whose blocks serve as well (a bit each), such as device-local memory the CPU can map
    // for a buffer that only needs it device-local
    public (Block Block, ulong At)? Take(ulong size, ulong alignment, uint type, uint also = 0)
    {
        if (!Assert(size is > 0 and <= BlockBytes) || !Assert(alignment is > 0 and <= 1UL << 20) || !Assert(type < 32))
            return null;
        var (block, index) = Fit(size, alignment, also | (1u << (int)type));
        if (block is null && NewBlock(type) is { } made) (block, index, _lastType) = (made, 0, type);
        if (block is null) return null;
        Keep(block);
        return (block, Carve(block, index, size, alignment));
    }

    // The tightest free range of a block of one of the types that holds the size aligned
    private (Block? Block, int Index) Fit(ulong size, ulong alignment, uint types)
    {
        (Block? Block, int Index, ulong Length) best = (null, -1, ulong.MaxValue);
        foreach (var block in _blocks.Bounded(MaxBlocks))
        {
            if ((types & (1u << (int)block.Type)) == 0 || !Assert(block.Free.Count <= MaxRanges)) continue;
            var free = block.Free;
            for (var i = 0; i < Math.Min(free.Count, MaxRanges); i++)
                if (free[i].Size < best.Length && Aligned(free[i].At, alignment) + size <= free[i].At + free[i].Size)
                    best = (block, i, free[i].Size);
        }

        _ = Assert(best.Block is null || best.Length >= size);
        return (best.Block, best.Index);
    }

    private static ulong Aligned(ulong at, ulong alignment) =>
        Assert(alignment > 0) ? (at + alignment - 1) / alignment * alignment : at;

    private ulong Carve(Block block, int index, ulong size, ulong alignment)
    {
        var (at, length) = block.Free[index];
        var start = Aligned(at, alignment);
        _ = Assert(start + size <= at + length) && Assert(block.Free.Count < MaxRanges);
        block.Free.RemoveAt(index);
        if (start + size < at + length) block.Free.Insert(index, (start + size, at + length - start - size));
        if (start > at) block.Free.Insert(index, (at, start - at));
        block.Taken++;
        (Live, Ranges) = (Live + size, Ranges + 1);
        VkMemory.Carved(Live, Ranges, Freed);
        return start;
    }

    private Block? NewBlock(uint type)
    {
        if (!Assert(_blocks.Count < MaxBlocks) || !Assert(device.Handle != IntPtr.Zero)) return null;
        if (_ahead is { } ahead && _aheadType == type)
        {
            _ahead = null;
            (AheadMade, AheadWaited) = (AheadMade + 1, AheadWaited + (ahead.IsCompleted ? 0 : 1));
            _ = Assert(AheadWaited <= AheadMade);
            return Imported(ahead.Result, type); // begun a while ago: the rest of the allocation at most
        }

        return Imported(Allocate(device.Handle, type), type);
    }

    // vkAllocateMemory alone, which a worker may make: the device is not externally synchronized for it
    private static ulong Allocate(IntPtr handle, uint type)
    {
        var export = new Vk.Chained { SType = Vk.ExportMemoryAllocateInfo, HandleTypes = Vk.OpaqueFd };
        var allocate = new Vk.AllocateInfo
        {
            SType = Vk.MemoryAllocateInfo, Next = &export, Size = BlockBytes, TypeIndex = type
        };
        ulong memory = 0;
        return Assert(type < 32) && Assert(handle != IntPtr.Zero) && VkApi.AllocateMemory(handle, &allocate, null, &memory) == Vk.Success
            ? memory
            : 0;
    }

    // The export and OpenGL's import, on the engine's thread
    private Block? Imported(ulong memory, uint type)
    {
        if (memory == 0 || !Assert(_blocks.Count < MaxBlocks)) return null;
        var handle = device.Handle;
        var get = new Vk.GetFdInfo { SType = Vk.MemoryGetFdInfo, Handle = memory, HandleType = Vk.OpaqueFd };
        int fd;
        var glMemory = VkApi.GetMemoryFd(handle, &get, &fd) == Vk.Success && fd >= 0
            ? GlInterop.Memory(fd, BlockBytes)
            : 0;
        if (glMemory == 0)
        {
            VkApi.FreeMemory(handle, memory, null);
            return null;
        }

        var made = new Block(memory, glMemory, type);
        _blocks.Add(made);
        Made++;
        return Assert(_blocks.Count <= MaxBlocks) ? made : null;
    }

    // A block made ahead of need, so the pool that needs it does not wait for vkAllocateMemory of 128 MB in the frame it is made:
    // once the free room of the type the last new block was made for falls under low, a worker allocates the next one, and the
    // engine's thread takes it in (export, OpenGL's import) the next time it asks after the worker is done, or when a range needs it
    // first. An unused one stays until the arena goes. Engine thread.
    public long AheadMade { get; private set; }
    public long AheadWaited { get; private set; } // of those, taken by a range before the worker was done

    private Task<ulong>? _ahead;
    private uint _aheadType, _lastType = NoType;
    private const uint NoType = uint.MaxValue;

    // True when a block was taken in
    public bool Reserve(ulong low)
    {
        if (!Assert(low <= BlockBytes) || _lastType == NoType) return false;
        if (_ahead is { IsCompleted: true } done)
        {
            _ahead = null;
            var taken = Assert(_aheadType < 32) && Imported(done.Result, _aheadType) is not null;
            if (taken) AheadMade++;
            return Assert(AheadMade >= AheadWaited) && taken;
        }

        if (_ahead is not null || _blocks.Count >= MaxBlocks - 1 || Room(_lastType) >= low) return false;
        _aheadType = Assert(_lastType < 32) ? _lastType : NoType;
        var (handle, type) = (device.Handle, _aheadType);
        _ahead = Task.Run(() => Allocate(handle, type));
        return !Assert(_ahead is not null) || !Assert(type < 32);
    }

    // The free bytes of the type's blocks
    private ulong Room(uint type)
    {
        var room = 0UL;
        foreach (var block in _blocks.Bounded(MaxBlocks))
            if (block.Type == type)
                foreach (var (_, size) in block.Free.Bounded(MaxRanges))
                    room += size;
        return Assert(room <= MaxBlocks * BlockBytes) ? room : 0;
    }

    // A worker's block nobody took: freed, waited for if it is still being made
    private void Unreserve()
    {
        if (_ahead is not { } ahead) return;
        _ = Assert(!ahead.IsFaulted); // Allocate returns 0 rather than throw
        _ahead = null;
        if (ahead.Result is var memory and not 0 && Assert(device.Handle != IntPtr.Zero))
            VkApi.FreeMemory(device.Handle, memory, null);
    }

    // Before the last buffer on it is given back, the caller saw the GPU finish every Vulkan command that used it
    public void Give(Block block, ulong at, ulong size)
    {
        if (!NotNull(block) || !Assert(_blocks.Contains(block)) || !Assert(block.Taken > 0 && at + size <= BlockBytes))
            return;
        block.Taken--;
        (Live, Ranges) = (Live - size, Ranges - 1);
        VkMemory.Carved(Live, Ranges, Freed);
        FreeList.Give(block.Free, at, size);
        if (block.Taken == 0) Retire(block);
    }

    // The buffers on it are deleted already: the fence follows every OpenGL command that used them
    private void Retire(Block block)
    {
        _ = Assert(block.Free is [(0, BlockBytes)]) && Assert(block.Emptied.Fence == IntPtr.Zero);
        block.Emptied = (_frame, GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None));
        _empty++;
    }

    private void Keep(Block block)
    {
        if (!NotNull(block) || block.Emptied.Fence == IntPtr.Zero) return;
        GL.DeleteSync(block.Emptied.Fence);
        block.Emptied = default;
        _empty--;
        _ = Assert(_empty >= 0);
    }

    // At a frame's start: frame is the one begun, done the newest the GPU finished. A count begun again is a new renderer's,
    // whose predecessor waited for the GPU as it went: every frame of the old count is done.
    public void Collect(long frame, long done)
    {
        if (!Assert(done < frame)) return;
        if (frame < _frame)
            foreach (var block in _blocks.Bounded(MaxBlocks))
                block.Emptied = block.Emptied with { Frame = long.MinValue };
        _frame = frame;
        if (_empty > 0) Release(done, 0);
    }

    // Every empty block, once OpenGL is done with it (a while at most), for a caller that saw the GPU go idle
    public void Trim()
    {
        if (!Assert(_blocks.Count <= MaxBlocks) || _empty == 0) return;
        _ = device.WaitIdle();
        Release(long.MaxValue, TrimNs);
    }

    private void Release(long done, ulong waitNs)
    {
        var kept = 0;
        for (var i = 0; i < Math.Min(_blocks.Count, MaxBlocks); i++)
        {
            var block = _blocks[i];
            if (block is { Taken: 0, Emptied: var (frame, fence) } && fence != IntPtr.Zero && frame <= done &&
                Signalled(fence, waitNs))
                Free(block);
            else _blocks[kept++] = block;
        }

        _blocks.RemoveRange(kept, _blocks.Count - kept);
        _ = Assert(_empty >= 0) && Assert(_empty <= _blocks.Count);
    }

    private static bool Signalled(IntPtr fence, ulong waitNs) =>
        Assert(fence != IntPtr.Zero) &&
        GL.ClientWaitSync(fence, ClientWaitSyncFlags.SyncFlushCommandsBit, waitNs) is WaitSyncStatus.AlreadySignaled
            or WaitSyncStatus.ConditionSatisfied;

    // OpenGL's import first, then the memory it imported
    private void Free(Block block)
    {
        if (!Assert(block.Taken == 0) || !Assert(block.Free is [(0, BlockBytes)])) return;
        if (block.Emptied.Fence != IntPtr.Zero)
        {
            GL.DeleteSync(block.Emptied.Fence);
            _empty--;
        }

        GlInterop.Delete(block.GlMemory, 0);
        VkApi.FreeMemory(device.Handle, block.Memory, null);
        (block.Emptied, block.Mapped) = (default, 0);
        Freed++;
        VkMemory.Carved(Live, Ranges, Freed);
    }

    public void Dispose()
    {
        Unreserve();
        _ = Assert(_blocks.Count <= MaxBlocks) && device.WaitIdle();
        foreach (var block in _blocks.Bounded(MaxBlocks))
        {
            _ = Assert(block.Taken == 0); // every buffer carved from it went first
            if (block.Emptied.Fence != IntPtr.Zero) GL.DeleteSync(block.Emptied.Fence);
            GlInterop.Delete(block.GlMemory, 0);
            VkApi.FreeMemory(device.Handle, block.Memory, null);
        }

        _blocks.Clear();
        _empty = 0;
    }
}
