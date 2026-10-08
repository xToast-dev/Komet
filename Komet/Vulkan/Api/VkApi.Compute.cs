namespace Komet.Vulkan;

internal static unsafe partial class VkApi
{
    public static delegate* unmanaged<IntPtr, Vk.BufferInfo*, void*, ulong*, int> CreateBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyBuffer { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void> GetBufferRequirements { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, int> BindBufferMemory { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void**, int> MapMemory { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.ViewInfo*, void*, ulong*, int> CreateImageView { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyImageView { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.SamplerInfo*, void*, ulong*, int> CreateSampler { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySampler { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.ModuleInfo*, void*, ulong*, int> CreateShaderModule { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyShaderModule { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.SetLayoutInfo*, void*, ulong*, int> CreateSetLayout { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroySetLayout { get; private set; }
    public static delegate* unmanaged<IntPtr, Vk.PipelineLayoutInfo*, void*, ulong*, int> CreatePipelineLayout { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyPipelineLayout { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, uint, Vk.ComputeInfo*, void*, ulong*, int> CreateComputePipelines { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, void*, void> DestroyPipeline { get; private set; }
    public static delegate* unmanaged<IntPtr, int, ulong, void> CmdBindPipeline { get; private set; }
    public static delegate* unmanaged<IntPtr, int, ulong, uint, uint, Vk.WriteInfo*, void> CmdPushDescriptors { get; private set; }

    public static delegate* unmanaged<IntPtr, uint, uint, uint, void> CmdDispatch { get; private set; }
    public static delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void> CmdFillBuffer { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, ulong, uint, Vk.BufferCopy*, void> CmdCopyBuffer { get; private set; }

    public static delegate* unmanaged<IntPtr, ulong, ulong, int, uint, Vk.BufferImageCopy*, void> CmdCopyBufferToImage { get; private set; }

    private static bool LoadCompute(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
        CreateBuffer =
            (delegate* unmanaged<IntPtr, Vk.BufferInfo*, void*, ulong*, int>)Device(device, "vkCreateBuffer");
        DestroyBuffer = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyBuffer");
        GetBufferRequirements = (delegate* unmanaged<IntPtr, ulong, Vk.MemoryRequirements*, void>)Device(device,
            "vkGetBufferMemoryRequirements");
        BindBufferMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, int>)Device(device, "vkBindBufferMemory");
        MapMemory = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void**, int>)Device(device, "vkMapMemory");
        CreateImageView =
            (delegate* unmanaged<IntPtr, Vk.ViewInfo*, void*, ulong*, int>)Device(device, "vkCreateImageView");
        DestroyImageView = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyImageView");
        CreateSampler =
            (delegate* unmanaged<IntPtr, Vk.SamplerInfo*, void*, ulong*, int>)Device(device, "vkCreateSampler");
        DestroySampler = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroySampler");
        CreateShaderModule =
            (delegate* unmanaged<IntPtr, Vk.ModuleInfo*, void*, ulong*, int>)Device(device, "vkCreateShaderModule");
        DestroyShaderModule = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyShaderModule");
        return LoadPipelines(device) && CreateBuffer != null && DestroyBuffer != null &&
               GetBufferRequirements != null &&
               BindBufferMemory != null && MapMemory != null && CreateImageView != null && DestroyImageView != null &&
               CreateSampler != null && DestroySampler != null && CreateShaderModule != null &&
               DestroyShaderModule != null;
    }

    private static bool LoadPipelines(IntPtr device)
    {
        if (!Assert(device != IntPtr.Zero)) return false;
        CreateSetLayout =
            (delegate* unmanaged<IntPtr, Vk.SetLayoutInfo*, void*, ulong*, int>)Device(device,
                "vkCreateDescriptorSetLayout");
        DestroySetLayout =
            (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyDescriptorSetLayout");
        CreatePipelineLayout = (delegate* unmanaged<IntPtr, Vk.PipelineLayoutInfo*, void*, ulong*, int>)Device(device,
            "vkCreatePipelineLayout");
        DestroyPipelineLayout =
            (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyPipelineLayout");
        CreateComputePipelines = (delegate* unmanaged<IntPtr, ulong, uint, Vk.ComputeInfo*, void*, ulong*, int>)Device(
            device, "vkCreateComputePipelines");
        DestroyPipeline = (delegate* unmanaged<IntPtr, ulong, void*, void>)Device(device, "vkDestroyPipeline");
        CmdBindPipeline = (delegate* unmanaged<IntPtr, int, ulong, void>)Device(device, "vkCmdBindPipeline");
        CmdPushDescriptors = (delegate* unmanaged<IntPtr, int, ulong, uint, uint, Vk.WriteInfo*, void>)Device(device,
            "vkCmdPushDescriptorSetKHR");
        CmdDispatch = (delegate* unmanaged<IntPtr, uint, uint, uint, void>)Device(device, "vkCmdDispatch");
        CmdFillBuffer = (delegate* unmanaged<IntPtr, ulong, ulong, ulong, uint, void>)Device(device, "vkCmdFillBuffer");
        CmdCopyBuffer = (delegate* unmanaged<IntPtr, ulong, ulong, uint, Vk.BufferCopy*, void>)Device(device,
            "vkCmdCopyBuffer");
        CmdCopyBufferToImage = (delegate* unmanaged<IntPtr, ulong, ulong, int, uint, Vk.BufferImageCopy*, void>)Device(
            device, "vkCmdCopyBufferToImage");
        return Assert(CreateSetLayout != null) && DestroySetLayout != null && CreatePipelineLayout != null &&
               DestroyPipelineLayout != null && CreateComputePipelines != null && DestroyPipeline != null &&
               CmdBindPipeline != null && CmdPushDescriptors != null && CmdDispatch != null && CmdFillBuffer != null &&
               CmdCopyBufferToImage != null && CmdCopyBuffer != null;
    }
}
