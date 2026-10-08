namespace Komet.Vulkan;

// The work a frame records later on its own thread, without a closure each (several a pool draw while the GPU culls): a dispatch
// with its descriptors, or a buffer copy. Taken from a pool, back in it once recorded; one a frame drops goes to the collector.
internal sealed unsafe partial class VulkanBackend
{
    private const int MaxIdle = 512;

    private readonly Core.BoundedPool<Recorded> _idle = new(MaxIdle); // the frame's recording thread gives them back

    private Recorded Taken() =>
        _idle.Take() is { Program: null } op && Assert(op.Count <= MaxResources) ? op : new Recorded(this);

    private void Recycled(Recorded op)
    {
        if (Assert(op.Program is null) && Assert(op.Count <= MaxResources)) _idle.Give(op);
    }

    private sealed class Recorded
    {
        public readonly Vk.BufferDescriptor[] Buffers = new Vk.BufferDescriptor[MaxResources];
        public readonly Vk.ImageDescriptor[] Images = new Vk.ImageDescriptor[MaxResources];
        public readonly Action<IntPtr> Run;
        private readonly VulkanBackend _owner;
        public ProgramSlot? Program; // a dispatch; null: the copy
        public int Count;
        public (int X, int Y) Groups;
        public (ulong From, ulong To, Vk.BufferCopy Region) Copy;

        public Recorded(VulkanBackend owner)
        {
            (_owner, Run) = (owner, Execute);
            _ = NotNull(owner) && Assert(Buffers.Length == MaxResources);
        }

        private void Execute(IntPtr commands)
        {
            _ = Assert(commands != IntPtr.Zero) && Assert(Count <= MaxResources);
            if (Program is { } p) Dispatched(commands, p, this);
            else CopyRecorded(commands, Copy);
            Program = null;
            _owner.Recycled(this);
        }
    }

    private static void CopyRecorded(IntPtr commands, (ulong From, ulong To, Vk.BufferCopy Region) copy)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(copy.From != 0 && copy.To != 0)) return;
        var region = copy.Region;
        VkApi.CmdCopyBuffer(commands, copy.From, copy.To, 1, &region);
        Ordered(commands);
    }
}
