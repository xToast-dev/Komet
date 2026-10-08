namespace Komet.Vulkan;

// Submitted without waiting; a frame's command buffer is reused only once its fence says the GPU is done with it
internal sealed unsafe partial class VulkanDevice
{
    public const ulong Forever = ulong.MaxValue;

    // A command pool of its own, for a thread that records alone
    public ulong CommandPool()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(_pool != 0)) return 0;
        var info = new Vk.PoolInfo { SType = Vk.CommandPoolCreateInfo, Flags = Vk.ResetCommandBuffer, Family = _family };
        ulong pool;
        return VkApi.CreateCommandPool(_device, &info, null, &pool) == Vk.Success ? pool : 0;
    }

    public void DestroyPool(ulong pool)
    {
        if (Assert(_device != IntPtr.Zero) && Assert(pool != _pool) && pool != 0)
            VkApi.DestroyCommandPool(_device, pool, null); // its command buffers with it
    }

    public IntPtr Allocate(ulong pool)
    {
        if (!Assert(pool != 0) || !Assert(_device != IntPtr.Zero)) return IntPtr.Zero;
        var allocate = new Vk.CommandBufferInfo { SType = Vk.CommandBufferAllocateInfo, Pool = pool, Count = 1 };
        IntPtr commands;
        return VkApi.AllocateCommandBuffers(_device, &allocate, &commands) == Vk.Success ? commands : IntPtr.Zero;
    }

    public IntPtr Allocate() => Allocate(_pool);

    // A fence, signalled already when the first wait on it should not wait
    public ulong Fence(bool signalled)
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(_fence != 0)) return 0;
        var info = new Vk.FlagsInfo { SType = Vk.FenceCreateInfo, Flags = signalled ? 1u : 0 };
        ulong fence;
        return VkApi.CreateFence(_device, &info, null, &fence) == Vk.Success ? fence : 0;
    }

    public ulong Semaphore()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(VkApi.CreateSemaphore != null)) return 0;
        var info = new Vk.FlagsInfo { SType = Vk.SemaphoreCreateInfo };
        ulong semaphore;
        return VkApi.CreateSemaphore(_device, &info, null, &semaphore) == Vk.Success ? semaphore : 0;
    }

    // A query pool of the type, 0 when the driver refuses it
    public ulong QueryPool(uint type, uint count, uint statistics = 0)
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(count > 0)) return 0;
        var info = new Vk.QueryPoolInfo
        {
            SType = Vk.QueryPoolCreateInfo, QueryType = type, QueryCount = count, PipelineStatistics = statistics
        };
        ulong pool;
        return VkApi.CreateQueryPool(_device, &info, null, &pool) == Vk.Success ? pool : 0;
    }

    public void DestroySemaphore(ulong semaphore)
    {
        if (Assert(_device != IntPtr.Zero) && Assert(VkApi.DestroySemaphore != null) && semaphore != 0)
            VkApi.DestroySemaphore(_device, semaphore, null);
    }

    public void Destroy(ulong fence)
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(fence != _fence) || fence == 0) return;
        VkApi.DestroyFence(_device, fence, null);
    }

    public static bool Begin(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(VkApi.BeginCommandBuffer != null)) return false;
        var begin = new Vk.BeginInfo { SType = Vk.CommandBufferBeginInfo, Flags = Vk.OneTimeSubmit };
        return VkApi.BeginCommandBuffer(commands, &begin) == Vk.Success;
    }

    public static bool End(IntPtr commands) =>
        Assert(commands != IntPtr.Zero) && Assert(VkApi.EndCommandBuffer != null) &&
        VkApi.EndCommandBuffer(commands) == Vk.Success;

    // Ends and submits it after the wait semaphores, signalling the semaphores and the fence; does not wait
    public bool Submit(IntPtr commands, ReadOnlySpan<ulong> wait, ReadOnlySpan<ulong> signal, ulong fence) =>
        Assert(commands != IntPtr.Zero) && Submit([commands], wait, signal, fence);

    // The same for command buffers that run one after the other; ended: how many of the first ones were ended already
    public bool Submit(ReadOnlySpan<IntPtr> buffers, ReadOnlySpan<ulong> wait, ReadOnlySpan<ulong> signal, ulong fence,
        int ended = 0)
    {
        if (!Assert(buffers.Length is > 0 and <= 4) || !Assert(wait.Length <= 4 && signal.Length <= 4)) return false;
        foreach (var commands in buffers[Math.Clamp(ended, 0, buffers.Length)..].Bounded(4))
            if (VkApi.EndCommandBuffer(commands) != Vk.Success)
                return false;
        var stages = stackalloc uint[] { Vk.StageAll, Vk.StageAll, Vk.StageAll, Vk.StageAll };
        fixed (ulong* signals = signal, waits = wait)
        fixed (IntPtr* commands = buffers)
        {
            var submit = new Vk.Submit
            {
                SType = Vk.SubmitInfo, CommandBufferCount = (uint)buffers.Length, CommandBuffers = commands,
                WaitCount = (uint)wait.Length, Waits = waits, WaitStages = stages, SignalCount = (uint)signal.Length,
                Signals = signals
            };
            return Queued(&submit, 1, fence);
        }
    }

    // Before a queue operation of another thread: the frame's recording thread caught up with what it was handed - the
    // submissions it makes among them - so this one comes after them, and an idle device has run them
    public Action? CatchUp { get; set; }

    // vkQueueSubmit, the queue used by one thread at a time
    private bool Queued(Vk.Submit* submit, uint count, ulong fence)
    {
        CatchUp?.Invoke();
        lock (_queueLock)
            return Assert(_queue != IntPtr.Zero) && VkApi.QueueSubmit(_queue, count, submit, fence) == Vk.Success;
    }

    public int Present(Vk.PresentInfo* info)
    {
        if (!Assert(info != null) || !Assert(_queue != IntPtr.Zero)) return -1;
        CatchUp?.Invoke();
        lock (_queueLock) return VkApi.QueuePresent(_queue, info);
    }

    private readonly Lock _queueLock = new();

    // An empty submission signalling the fence once everything submitted before it is done
    public bool Signal(ulong fence)
    {
        if (!Assert(fence != 0) || !Assert(_queue != IntPtr.Zero)) return false;
        return Queued(null, 0, fence);
    }

    public bool WaitIdle()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(_queue != IntPtr.Zero)) return false;
        CatchUp?.Invoke();
        lock (_queueLock) return VkApi.DeviceWaitIdle(_device) == Vk.Success;
    }

    // Waits up to timeout nanoseconds for the fence; true when it is signalled, and then it is reset
    public bool Wait(ulong fence, ulong timeout = Forever)
    {
        if (!Assert(fence != 0) || !Assert(_device != IntPtr.Zero)) return false;
        if (VkApi.WaitForFences(_device, 1, &fence, 1, timeout) != Vk.Success) return false;
        return VkApi.ResetFences(_device, 1, &fence) == Vk.Success;
    }
}
