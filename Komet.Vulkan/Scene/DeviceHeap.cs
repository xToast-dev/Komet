namespace Komet.Vulkan;

// Returned ranges wait for their last frame (Retire). Plain, non-exportable allocations: free to submit.
internal sealed unsafe class DeviceHeap(VulkanDevice device) : IDisposable
{
    public const ulong BlockBytes = 64UL << 20;
    private const int MaxBlocks = 256, MaxRanges = 1 << 14, MaxRetired = 1 << 16;
    private const uint Usage = Vk.UsageVertex | Vk.UsageIndex | Vk.UsageUniform | Vk.UsageStorage | Vk.UsageIndirect |
                               Vk.TransferSrc | Vk.TransferDst;

    private static readonly (uint Want, uint Avoid)[] DeviceOnly = [(Vk.DeviceLocal, 0)];

    private readonly List<Block> _blocks = [];
    private Retired<Range>? _retiredList;
    private Retired<Range> Retiring => _retiredList ??= new Retired<Range>(MaxRetired, Give);

    public sealed class Block(ulong buffer, ulong memory, ulong size)
    {
        public ulong Buffer { get; } = buffer;
        public ulong Memory { get; } = memory;
        public ulong Size { get; } = size;
        public List<(ulong At, ulong Size)> Free { get; } = [(0, size)];
        public int Taken { get; set; }
    }

    public readonly record struct Range(Block Block, ulong Offset, ulong Size)
    {
        public ulong Buffer => Block.Buffer;
    }

    public ulong Bytes => (ulong)_blocks.Sum(b => (decimal)b.Size);

    public Range? Take(ulong size)
    {
        if (!Assert(size > 0)) return null;
        size = (size + 255) / 256 * 256;
        foreach (var block in _blocks.Bounded(MaxBlocks))
            if (Carve(block, size) is { } at)
                return new Range(block, at, size);
        var made = Made(Math.Max(size, BlockBytes));
        if (made is null) return null;
        _blocks.Add(made);
        return Carve(made, size) is { } first ? new Range(made, first, size) : null;
    }

    public void Retire(Range range, long frame)
    {
        if (!NotNull(range.Block) || !Assert(frame >= 0)) return;
        if (!Retiring.Add(range, frame)) Give(range); // should not happen: nothing collects
    }

    public void Collect(long done)
    {
        if (!Assert(done >= -VulkanFrame.Slots) || Retiring.Count == 0) return;
        Retiring.Collect(done);
        Release();
    }

    // Empty blocks (Taken 0) go back to the driver but one (so the next mirror need not allocate): a burst of large
    // mirrors leaves no lasting high-water mark.
    private void Release()
    {
        var spare = false;
        for (var n = 0; n < MaxBlocks; n++)
        {
            var i = _blocks.Count - 1 - n;
            if (i < 0) break;
            var block = _blocks[i];
            if (block.Taken != 0) continue;
            if (!spare)
            {
                spare = true;
                continue;
            }

            _ = Assert(block.Free.Count == 1);
            VkApi.DestroyBuffer(device.Handle, block.Buffer, null);
            VkApi.FreeMemory(device.Handle, block.Memory, null);
            _blocks.RemoveAt(i);
        }
    }

    private static ulong? Carve(Block block, ulong size)
    {
        for (var i = 0; i < Math.Min(block.Free.Count, MaxRanges); i++)
        {
            var (at, length) = block.Free[i];
            if (length < size) continue;
            if (length == size) block.Free.RemoveAt(i);
            else block.Free[i] = (at + size, length - size);
            block.Taken++;
            return at;
        }

        return null;
    }

    private static void Give(Range range)
    {
        var block = range.Block;
        if (!Assert(block.Taken > 0) || !Assert(range.Offset + range.Size <= block.Size)) return;
        block.Taken--;
        Freed(block.Free, range.Offset, range.Size);
    }

    // Back into the block's free ranges, sorted by offset, merged with its neighbours
    private static void Freed(List<(ulong At, ulong Size)> free, ulong at, ulong size)
    {
        if (!Assert(free.Count < MaxRanges)) return;
        var i = free.Count;
        for (var f = 0; f < Math.Min(free.Count, MaxRanges); f++)
            if (free[f].At > at)
            {
                i = f;
                break;
            }

        free.Insert(i, (at, size));
        if (i + 1 < free.Count && free[i].At + free[i].Size == free[i + 1].At)
        {
            free[i] = (free[i].At, free[i].Size + free[i + 1].Size);
            free.RemoveAt(i + 1);
        }

        if (i > 0 && free[i - 1].At + free[i - 1].Size == free[i].At)
        {
            free[i - 1] = (free[i - 1].At, free[i - 1].Size + free[i].Size);
            free.RemoveAt(i);
        }
    }

    private Block? Made(ulong size)
    {
        if (!Assert(_blocks.Count < MaxBlocks)) return null;
        return device.MakeBuffer(size, Usage, DeviceOnly, false, out var made) is null ? new Block(made.Buffer, made.Memory, size) : null;
    }

    public void Dispose()
    {
        _ = device.WaitIdle();
        foreach (var block in _blocks.Bounded(MaxBlocks))
        {
            VkApi.DestroyBuffer(device.Handle, block.Buffer, null);
            VkApi.FreeMemory(device.Handle, block.Memory, null);
        }

        _blocks.Clear();
        Retiring.Clear(freed: false); // the blocks went whole
    }
}
