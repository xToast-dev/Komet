namespace Komet.Vulkan;

// Submitted without waiting; a frame's command buffer is reused only once its fence says the GPU is done with it
internal sealed unsafe partial class VulkanDevice
{
    private const ulong Forever = ulong.MaxValue;

    private readonly Lock _queueLock = new(); // the queue is used by one thread at a time

    // Before another thread's queue operation: the recording thread caught up (incl. its submissions), so this follows
    public Action? CatchUp { get; set; }

    // A command pool of its own, for a thread that records alone
    public ulong CommandPool()
    {
        if (!Assert(Handle != IntPtr.Zero)) return 0;

        var info = new Vk.PoolInfo { SType = Vk.CommandPoolCreateInfo, Flags = Vk.ResetCommandBuffer, Family = Family };
        ulong pool;
        return VkApi.CreateCommandPool(Handle, &info, null, &pool) == Vk.Success ? pool : 0;
    }

    public void DestroyPool(ulong pool)
    {
        if (Assert(Handle != IntPtr.Zero) && Assert(pool != _pool) && pool != 0)
        {
            VkApi.DestroyCommandPool(Handle, pool, null); // its command buffers with it
        }
    }

    public IntPtr Allocate(ulong pool)
    {
        if (!Assert(pool != 0) || !Assert(Handle != IntPtr.Zero)) return IntPtr.Zero;

        var allocate = new Vk.CommandBufferInfo { SType = Vk.CommandBufferAllocateInfo, Pool = pool, Count = 1 };
        IntPtr commands;
        return VkApi.AllocateCommandBuffers(Handle, &allocate, &commands) == Vk.Success ? commands : IntPtr.Zero;
    }

    public IntPtr Allocate() => Allocate(_pool);

    public ulong Fence(bool signalled)
    {
        if (!Assert(Handle != IntPtr.Zero)) return 0;

        var info = new Vk.FlagsInfo { SType = Vk.FenceCreateInfo, Flags = signalled ? 1u : 0 };
        ulong fence;
        return VkApi.CreateFence(Handle, &info, null, &fence) == Vk.Success ? fence : 0;
    }

    public ulong Semaphore()
    {
        if (!Assert(Handle != IntPtr.Zero)) return 0;

        var info = new Vk.FlagsInfo { SType = Vk.SemaphoreCreateInfo };
        ulong semaphore;
        return VkApi.CreateSemaphore(Handle, &info, null, &semaphore) == Vk.Success ? semaphore : 0;
    }

    // 0 when the driver refuses it
    public ulong QueryPool(uint type, uint count, uint statistics = 0)
    {
        if (!Assert(Handle != IntPtr.Zero) || !Assert(count > 0)) return 0;

        var info = new Vk.QueryPoolInfo
        {
            SType = Vk.QueryPoolCreateInfo, QueryType = type, QueryCount = count, PipelineStatistics = statistics
        };
        ulong pool;
        return VkApi.CreateQueryPool(Handle, &info, null, &pool) == Vk.Success ? pool : 0;
    }

    public void DestroySemaphore(ulong semaphore)
    {
        if (Assert(Handle != IntPtr.Zero) && semaphore != 0) VkApi.DestroySemaphore(Handle, semaphore, null);
    }

    public void DestroyFence(ulong fence)
    {
        if (!Assert(Handle != IntPtr.Zero) || !Assert(fence != _fence) || fence == 0) return;

        VkApi.DestroyFence(Handle, fence, null);
    }

    public static bool Begin(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero)) return false;

        var begin = new Vk.BeginInfo { SType = Vk.CommandBufferBeginInfo, Flags = Vk.OneTimeSubmit };
        return VkApi.BeginCommandBuffer(commands, &begin) == Vk.Success;
    }

    public static bool End(IntPtr commands) =>
        Assert(commands != IntPtr.Zero) && VkApi.EndCommandBuffer(commands) == Vk.Success;

    // ended: how many of the first buffers were ended already
    public bool Submit(ReadOnlySpan<IntPtr> buffers, ReadOnlySpan<ulong> signal, ulong fence, int ended = 0)
    {
        if (!Assert(buffers.Length is > 0 and <= 4) || !Assert(signal.Length <= 4)) return false;

        foreach (var commands in buffers[Math.Clamp(ended, 0, buffers.Length)..].Bounded(4))
        {
            if (VkApi.EndCommandBuffer(commands) != Vk.Success) return false;
        }

        fixed (ulong* signals = signal)
        fixed (IntPtr* commands = buffers)
        {
            var submit = new Vk.Submit
            {
                SType = Vk.SubmitInfo, CommandBufferCount = (uint)buffers.Length, CommandBuffers = commands,
                SignalCount = (uint)signal.Length, Signals = signals
            };
            return Queued(&submit, 1, fence);
        }
    }

    // The last vkQueueSubmit's result: a failed submission names it (lost device, out of memory) instead of guessing
    public int LastSubmit { get; private set; }

    private bool Queued(Vk.Submit* submit, uint count, ulong fence)
    {
        CatchUp?.Invoke();
        lock (_queueLock)
        {
            if (!Assert(_queue != IntPtr.Zero)) return false;

            LastSubmit = VkApi.QueueSubmit(_queue, count, submit, fence);
            return LastSubmit == Vk.Success;
        }
    }

    public int Present(Vk.PresentInfo* info)
    {
        if (!Assert(info != null) || !Assert(_queue != IntPtr.Zero)) return -1;

        CatchUp?.Invoke();
        lock (_queueLock)
        {
            return VkApi.QueuePresent(_queue, info);
        }
    }

    // From a thread of its own whose work needs no order with the recording thread's beyond the queue's (FrameBridge: it
    // reads only what an earlier present published), so without CatchUp; ended before submitting
    public bool SubmitAlone(IntPtr commands, ulong signal, ulong fence)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(signal != 0) ||
            VkApi.EndCommandBuffer(commands) != Vk.Success)
        {
            return false;
        }

        var stage = Vk.StageAll;
        var submit = new Vk.Submit
        {
            SType = Vk.SubmitInfo, CommandBufferCount = 1, CommandBuffers = &commands, SignalCount = 1,
            Signals = &signal,
            WaitStages = &stage
        };
        lock (_queueLock)
        {
            return Assert(_queue != IntPtr.Zero) && VkApi.QueueSubmit(_queue, 1, &submit, fence) == Vk.Success;
        }
    }

    public int PresentAlone(Vk.PresentInfo* info)
    {
        if (!Assert(info != null) || !Assert(_queue != IntPtr.Zero)) return -1;

        lock (_queueLock)
        {
            return VkApi.QueuePresent(_queue, info);
        }
    }

    public bool Signal(ulong fence) => Assert(fence != 0) && Queued(null, 0, fence);

    public bool WaitIdle()
    {
        if (!Assert(Handle != IntPtr.Zero)) return false;

        CatchUp?.Invoke();
        lock (_queueLock)
        {
            return VkApi.DeviceWaitIdle(Handle) == Vk.Success;
        }
    }

    // Resets the fence once signalled
    public bool Wait(ulong fence, ulong timeout = Forever)
    {
        if (!Assert(fence != 0) || !Assert(Handle != IntPtr.Zero)) return false;

        if (VkApi.WaitForFences(Handle, 1, &fence, 1, timeout) != Vk.Success) return false;

        return VkApi.ResetFences(Handle, 1, &fence) == Vk.Success;
    }
}
