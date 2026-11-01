namespace Komet.Vulkan;

// Hand-declared per the C headers on 64-bit: dispatchable handles are pointers, the others 64-bit numbers.
internal static partial class Vk
{
    public const uint ApiVersion13 = (1u << 22) | (3u << 12), QueueFamilyIgnored = ~0u;
    public const int Success = 0, OutOfDeviceMemory = -2;

    // VkStructureType
    public const int ApplicationInfo = 0, InstanceCreateInfo = 1, DeviceQueueCreateInfo = 2, DeviceCreateInfo = 3;
    public const int SubmitInfo = 4, MemoryAllocateInfo = 5, FenceCreateInfo = 8, SemaphoreCreateInfo = 9;
    public const int ImageCreateInfo = 14, CommandPoolCreateInfo = 39, CommandBufferAllocateInfo = 40;
    public const int CommandBufferBeginInfo = 42, ImageMemoryBarrier = 45;
    public const int PhysicalDeviceProperties2 = 1000059001, PhysicalDeviceIdProperties = 1000071004;

    public const uint FormatRgba8 = 37, TransferSrc = 0x1, TransferDst = 0x2, Sampled = 0x4;
    public const uint Storage = 0x8, ColorAttachment = 0x10, DepthAttachment = 0x20, AspectDepth = 0x2;
    public const uint FormatRgba16F = 97, FormatD32F = 126, FormatR16F = 76;
    public const uint FormatRgba32F = 109, FormatR8 = 9, FormatRg16F = 83, FormatRgba16 = 91;
    public const uint FormatRg8 = 16, FormatSrgba8 = 43, FormatR16 = 70, FormatA2B10G10R10 = 64, FormatB10G11R11F = 122;
    public const int LayoutUndefined = 0, LayoutGeneral = 1, LayoutTransferDst = 7;
    public const uint StageTop = 0x1, StageTransfer = 0x1000, StageBottom = 0x2000, StageAll = 0x10000;
    public const uint AccessTransferWrite = 0x1000, AccessMemoryRead = 0x8000, AccessMemoryWrite = 0x10000;
    public const uint QueueGraphics = 0x1, QueueCompute = 0x2, DeviceLocal = 0x1, AspectColor = 0x1;
    public const uint OneTimeSubmit = 0x1, ResetCommandBuffer = 0x2;

    // VkPhysicalDeviceProperties2: vendorID at 16+8, deviceID 16+12, deviceName 16+20, pipelineCacheUUID 16+276
    public const int PropertiesBytes = 1024, DeviceNameOffset = 36;
    public const int VendorOffset = 24, ModelOffset = 28, TypeOffset = 32, CacheUuidOffset = 292;

    // VkPhysicalDeviceLimits at 312: timestampComputeAndGraphics at 420, timestampPeriod (float ns/tick) at 424
    public const int TimestampsOffset = 312 + 420, TimestampPeriodOffset = 312 + 424;

    public unsafe struct AppInfo
    {
        public int SType; public void* Next;
        public byte* ApplicationName;
        public uint ApplicationVersion;
        public byte* EngineName;
        public uint EngineVersion, Api;
    }

    public unsafe struct InstanceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public AppInfo* Application;
        public uint LayerCount;
        public byte** Layers;
        public uint ExtensionCount;
        public byte** Extensions;
    }

    public unsafe struct IdProperties
    {
        public int SType; public void* Next;
        public fixed byte DeviceUuid[16];
        public fixed byte DriverUuid[16];
        public fixed byte DeviceLuid[8];
        public uint DeviceNodeMask, DeviceLuidValid;
    }

    public struct QueueFamily { public uint Flags, Count, TimestampBits, GranularityWidth, GranularityHeight, GranularityDepth; }

    public unsafe struct MemoryProperties
    {
        public uint TypeCount;
        public fixed uint Types[64]; // 32 of { propertyFlags, heapIndex }
        public uint HeapCount;
        public fixed ulong Heaps[32]; // 16 of { size, flags }
    }

    public unsafe struct QueueInfo
    {
        public int SType; public void* Next;
        public uint Flags, Family, Count;
        public float* Priorities;
    }

    public unsafe struct DeviceInfo
    {
        public int SType; public void* Next;
        public uint Flags, QueueInfoCount;
        public QueueInfo* QueueInfos;
        public uint LayerCount;
        public byte** Layers;
        public uint ExtensionCount;
        public byte** Extensions;
        public void* Features;
    }

    public unsafe struct PoolInfo
    {
        public int SType; public void* Next;
        public uint Flags, Family;
    }

    public unsafe struct CommandBufferInfo
    {
        public int SType; public void* Next;
        public ulong Pool;
        public int Level;
        public uint Count;
    }

    public unsafe struct BeginInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public void* Inheritance;
    }

    public unsafe struct ImageInfo
    {
        public int SType; public void* Next;
        public uint Flags, ImageType, Format, Width, Height, Depth, MipLevels, ArrayLayers, Samples, Tiling, Usage;
        public uint SharingMode, FamilyCount;
        public uint* Families;
        public int InitialLayout;
    }

    public struct MemoryRequirements
    {
        public ulong Size, Alignment;
        public uint TypeBits;
    }

    public unsafe struct AllocateInfo
    {
        public int SType; public void* Next;
        public ulong Size;
        public uint TypeIndex;
    }

    public unsafe struct FlagsInfo
    {
        public int SType; public void* Next;
        public uint Flags; // VkSemaphoreCreateInfo, VkFenceCreateInfo
    }

    public unsafe struct ImageBarrier
    {
        public int SType; public void* Next;
        public uint SrcAccess, DstAccess;
        public int OldLayout, NewLayout;
        public uint SrcFamily, DstFamily;
        public ulong Image;
        public ColorRange Range;

        // Between two layouts, all memory access on both sides, no queue hand-over
        public static ImageBarrier Full(ulong image, (int From, int To) layouts, uint aspect, int levels = 1, int layers = 1) => new()
        {
            SType = ImageMemoryBarrier, SrcAccess = AccessMemoryRead | AccessMemoryWrite,
            DstAccess = AccessMemoryRead | AccessMemoryWrite, OldLayout = layouts.From, NewLayout = layouts.To,
            SrcFamily = QueueFamilyIgnored, DstFamily = QueueFamilyIgnored, Image = image,
            Range = new ColorRange { Aspect = aspect, Levels = (uint)levels, Layers = (uint)layers }
        };
    }

    public struct ColorRange { public uint Aspect, BaseLevel, Levels, BaseLayer, Layers; }

    public unsafe struct Submit
    {
        public int SType; public void* Next;
        public uint WaitCount;
        public ulong* Waits;
        public uint* WaitStages;
        public uint CommandBufferCount;
        public IntPtr* CommandBuffers;
        public uint SignalCount;
        public ulong* Signals;
    }
}

// Device features, graphics pipeline state and dynamic rendering
internal static partial class Vk
{
    public const int PhysicalDeviceFeatures2 = 1000059000, Vulkan12Features = 51, Vulkan13Features = 53;
    public const int DepthClipControlFeatures = 1000355000;
    public const int DepthClipControlCreateInfo = 1000355001, GraphicsPipelineCreateInfo = 28;
    public const int VertexInputStateInfo = 19, InputAssemblyStateInfo = 20, ViewportStateInfo = 22;
    public const int RasterizationStateInfo = 23, MultisampleStateInfo = 24, DepthStencilStateInfo = 25;
    public const int ColorBlendStateInfo = 26, DynamicStateInfo = 27, RenderingCreateInfo = 1000044002;
    public const int RenderingInfoType = 1000044000, RenderingAttachmentInfo = 1000044001;
    public const uint StageVertex = 0x1, StageFragment = 0x10, BindGraphics = 0;
    public const uint TopologyTriangles = 3;
    public const uint FrontClockwise = 1, FrontCounterClockwise = 0;
    public const uint DynamicViewport = 0, DynamicScissor = 1, IndexUint32 = 1;
    public const uint LoadOpLoad = 0, StoreOpStore = 0;
    public const int LayoutColorAttachment = 2, LayoutDepthAttachment = 3, LayoutShaderRead = 5;
    public const uint CombinedSampler = 1;
    public const int FeatureCount = 55, IndependentBlend = 3, MultiDrawIndirect = 9, SamplerAnisotropy = 19;
    public const int FeatureDepthClamp = 11, LayoutDepthReadOnly = 4, FillModeNonSolid = 13, WideLines = 15, FeatureLogicOp = 8;
    public const int DrawIndirectFirstInstance = 10, PipelineCacheCreateInfo = 17;
    public const int DynamicRendering = 12, Vulkan13Count = 15, ExtensionNameBytes = 256;
    public const int DrawIndirectCount = 1, Vulkan12Count = 47;

    public unsafe struct Features2
    {
        public int SType; public void* Next;
        public fixed uint Features[FeatureCount];
    }

    public unsafe struct Features12
    {
        public int SType; public void* Next;
        public fixed uint Features[Vulkan12Count];
    }

    public unsafe struct Features13
    {
        public int SType; public void* Next;
        public fixed uint Features[Vulkan13Count];
    }

    public unsafe struct OneFeature
    {
        public int SType; public void* Next;
        // VkPhysicalDeviceDepthClipControlFeaturesEXT, VkPipelineViewportDepthClipControlCreateInfoEXT
        public uint Enabled;
    }

    public unsafe struct ExtensionProperties
    {
        public fixed byte Name[ExtensionNameBytes];
        public uint SpecVersion;
    }

    public struct VertexBinding { public uint Binding, Stride, InputRate; }

    public struct VertexAttribute { public uint Location, Binding, Format, Offset; }

    public unsafe struct VertexInputState
    {
        public int SType; public void* Next;
        public uint Flags, BindingCount;
        public VertexBinding* Bindings;
        public uint AttributeCount;
        public VertexAttribute* Attributes;
    }

    public unsafe struct InputAssemblyState
    {
        public int SType; public void* Next;
        public uint Flags, Topology, PrimitiveRestart;
    }

    public unsafe struct ViewportState
    {
        public int SType; public void* Next;
        public uint Flags, ViewportCount;
        public void* Viewports;
        public uint ScissorCount;
        public void* Scissors;
    }

    public unsafe struct RasterizationState
    {
        public int SType; public void* Next;
        public uint Flags, DepthClamp, Discard, PolygonMode, CullMode, FrontFace, DepthBias;
        public float BiasConstant, BiasClamp, BiasSlope, LineWidth;
    }

    public unsafe struct MultisampleState
    {
        public int SType; public void* Next;
        public uint Flags, Samples, SampleShading;
        public float MinSampleShading;
        public void* SampleMask;
        public uint AlphaToCoverage, AlphaToOne;
    }

    public struct StencilState { public uint Fail, Pass, DepthFail, Compare, CompareMask, WriteMask, Reference; }

    public unsafe struct DepthStencilState
    {
        public int SType; public void* Next;
        public uint Flags, DepthTest, DepthWrite, DepthCompare, DepthBounds, Stencil;
        public StencilState Front, Back;
        public float MinDepthBounds, MaxDepthBounds;
    }

    public struct BlendAttachment { public uint Enable, SourceColor, TargetColor, ColorOp, SourceAlpha, TargetAlpha, AlphaOp, WriteMask; }

    public unsafe struct ColorBlendState
    {
        public int SType; public void* Next;
        public uint Flags, LogicOpEnable, LogicOp, AttachmentCount;
        public BlendAttachment* Attachments;
        public fixed float Constants[4];
    }

    public unsafe struct DynamicState
    {
        public int SType; public void* Next;
        public uint Flags, Count;
        public uint* States;
    }

    public unsafe struct PipelineRendering
    {
        public int SType; public void* Next;
        public uint ViewMask, ColorCount;
        public uint* ColorFormats;
        public uint DepthFormat, StencilFormat;
    }

    public unsafe struct GraphicsInfo
    {
        public int SType; public void* Next;
        public uint Flags, StageCount;
        public StageInfo* Stages;
        public VertexInputState* VertexInput;
        public InputAssemblyState* InputAssembly;
        public void* Tessellation;
        public ViewportState* Viewport;
        public RasterizationState* Rasterization;
        public MultisampleState* Multisample;
        public DepthStencilState* DepthStencil;
        public ColorBlendState* ColorBlend;
        public DynamicState* Dynamic;
        public ulong Layout, RenderPass;
        public uint Subpass;
        public ulong BasePipeline;
        public int BaseIndex;
    }

    public struct Viewport { public float X, Y, Width, Height, MinDepth, MaxDepth; }

    public struct Rect
    {
        public int X, Y;
        public uint Width, Height;
    }

    public unsafe struct Attachment
    {
        public int SType; public void* Next;
        public ulong View;
        public int Layout;
        public uint ResolveMode;
        public ulong ResolveView;
        public int ResolveLayout;
        public uint Load, Store;
        public fixed float Clear[4];
    }

    public unsafe struct RenderingInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public Rect Area;
        public uint Layers, ViewMask, ColorCount;
        public Attachment* Colors;
        public Attachment* Depth;
        public Attachment* Stencil;
    }

    public struct DrawIndexed
    {
        public uint IndexCount, InstanceCount, FirstIndex;
        public int VertexOffset;
        public uint FirstInstance;
    }

    public unsafe struct PipelineCacheInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public nuint InitialDataSize;
        public void* InitialData;
    }
}

// Surfaces (Xlib, Wayland, Win32) and the swapchain that shows Vulkan-only's frames
internal static partial class Vk
{
    public const int XlibSurfaceCreateInfo = 1000004000, SwapchainCreateInfo = 1000001000, PresentInfoType = 1000001001;
    public const int WaylandSurfaceCreateInfo = 1000006000;
    public const string WaylandSurfaceExtension = "VK_KHR_wayland_surface";
    public const int Win32SurfaceCreateInfo = 1000009000;
    public const string Win32SurfaceExtension = "VK_KHR_win32_surface";
    public const int LayoutPresentSrc = 1000001002, Suboptimal = 1000001003, OutOfDate = -1000001004;
    public const uint PresentImmediate = 0, PresentMailbox = 1, PresentFifo = 2, PresentFifoRelaxed = 3;
    public const uint FormatBgra8 = 44, ColorSpaceSrgbNonlinear = 0, CompositeOpaque = 1;
    public const string SurfaceExtension = "VK_KHR_surface", XlibSurfaceExtension = "VK_KHR_xlib_surface";
    public const string SwapchainExtension = "VK_KHR_swapchain";

    // VkXlibSurfaceCreateInfoKHR: the X11 display and window (an XID, unsigned long)
    public unsafe struct XlibSurfaceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public IntPtr Display;
        public nuint Window;
    }

    // VkWaylandSurfaceCreateInfoKHR: the Wayland display and surface (wl_display*, wl_surface*)
    public unsafe struct WaylandSurfaceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public IntPtr Display;
        public IntPtr Surface;
    }

    // VkWin32SurfaceCreateInfoKHR: the module (HINSTANCE) and window (HWND)
    public unsafe struct Win32SurfaceInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public IntPtr Instance;
        public IntPtr Window;
    }

    public struct SurfaceCapabilities
    {
        public uint MinImageCount, MaxImageCount;
        public uint CurrentWidth, CurrentHeight, MinWidth, MinHeight, MaxWidth, MaxHeight;
        public uint MaxArrayLayers, SupportedTransforms, CurrentTransform, SupportedCompositeAlpha, SupportedUsage;
    }

    public struct SurfaceFormat { public uint Format, ColorSpace; }

    public unsafe struct SwapchainInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public ulong Surface;
        public uint MinImageCount, Format, ColorSpace, Width, Height, ArrayLayers, Usage, Sharing, FamilyCount;
        public uint* Families;
        public uint Transform, CompositeAlpha, PresentMode, Clipped;
        public ulong OldSwapchain;
    }

    public unsafe struct PresentInfo
    {
        public int SType; public void* Next;
        public uint WaitCount;
        public ulong* Waits;
        public uint SwapchainCount;
        public ulong* Swapchains;
        public uint* Indices;
        public int* Results;
    }
}

// Draws, lines, blits, copies, clears and queries; then Vulkan-only's for mods' raw GL: depth/stencil, points, 8-bit
// indices, divisors, indirect draws, image clears
internal static partial class Vk
{
    public const int LayoutTransferSrc = 6, ViewType2DArray = 5, ViewTypeCube = 3;
    public const uint CubeCompatible = 0x10;
    public const uint FilterLinear = 1, FilterNearest = 0;
    public const uint TopologyLines = 1, TopologyLineStrip = 2, TopologyTriangleStrip = 4;
    public const uint TopologyTriangleFan = 5, IndexUint16 = 0, DynamicLineWidth = 2;

    public const int LineFeatures = 1000259000, LineStateInfo = 1000259001, QueryPoolCreateInfo = 11;
    public const uint QueryOcclusion = 0, QueryTimestamp = 2, QueryPrecise = 1, Result64 = 1, ResultWait = 2;
    public const uint ResultAvailability = 4, LineBresenham = 2;
    public const uint StageHost = 0x4000, AccessHostRead = 0x2000;
    public const int OcclusionQueryPrecise = 23, PipelineStatisticsQuery = 24, ShaderFloat64 = 39;

    // Pipeline statistics, written in order: VS invocations, clipping invocations, clipping primitives, FS invocations
    public const uint QueryPipelineStatistics = 1, CountedStatistics = 0x4 | 0x20 | 0x40 | 0x80;

    // Vulkan-only, for mods' raw GL
    public const uint AspectStencil = 0x4, AspectDepthStencil = AspectDepth | AspectStencil;
    public const uint FormatD24S8 = 129, FormatD32S8 = 130, TopologyPoints = 0;
    public const uint IndexUint8 = 1000265000; // VK_INDEX_TYPE_UINT8_EXT
    public const uint DynamicStencilCompare = 6, DynamicStencilWrite = 7, DynamicStencilReference = 8;
    public const uint StencilFront = 0x1, StencilBack = 0x2;

    // VkFormatFeatureFlags of optimal tiling
    public const uint FeatureSampled = 0x1, FeatureDepthStencilAttachment = 0x200, FeatureBlitSource = 0x400,
        FeatureBlitTarget = 0x800;

    public const int IndexTypeUint8Features = 1000265000; // VkPhysicalDeviceIndexTypeUint8FeaturesEXT
    public const int DivisorFeatures = 1000190002, DivisorStateInfo = 1000190001; // VK_EXT_vertex_attribute_divisor
    public const string IndexUint8Extension = "VK_EXT_index_type_uint8", DivisorExtension = "VK_EXT_vertex_attribute_divisor";

    public struct PushRange { public uint Stages, Offset, Size; }

    public unsafe struct QueryPoolInfo
    {
        public int SType; public void* Next;
        public uint Flags, QueryType, QueryCount, PipelineStatistics;
    }

    // VkPhysicalDeviceLineRasterizationFeaturesKHR
    public unsafe struct LineFeaturesInfo
    {
        public int SType; public void* Next;
        public uint Rectangular, Bresenham, Smooth, StippledRectangular, StippledBresenham, StippledSmooth;
    }

    // VkPipelineRasterizationLineStateCreateInfoKHR
    public unsafe struct LineState
    {
        public int SType; public void* Next;
        public uint Mode, Stippled, Factor;
        public ushort Pattern;
    }

    public struct Layers { public uint Aspect, Level, BaseLayer, Count; }

    public struct ImageBlit
    {
        public Layers Source;
        public int SourceX0, SourceY0, SourceZ0, SourceX1, SourceY1, SourceZ1;
        public Layers Target;
        public int TargetX0, TargetY0, TargetZ0, TargetX1, TargetY1, TargetZ1;
    }

    // VkImageCopy: same-sized regions between two images of compatible formats, texel for texel
    public struct ImageCopy
    {
        public Layers Source;
        public int SourceX, SourceY, SourceZ;
        public Layers Target;
        public int TargetX, TargetY, TargetZ;
        public uint Width, Height, Depth;
    }

    // VkClearAttachment: the value is a VkClearValue - four floats for a color, depth and stencil for a depth
    public struct ClearAttachment
    {
        public uint Aspect, Attachment;
        public float Red, Green, Blue, Alpha;
    }

    public struct ClearRect
    {
        public Rect Rect;
        public uint BaseLayer, Layers;
    }

    public struct FormatProperties { public uint Linear, Optimal, Buffer; }

    // VkPhysicalDeviceVertexAttributeDivisorFeaturesEXT
    public unsafe struct TwoFeatures
    {
        public int SType; public void* Next;
        public uint First, Second;
    }

    public struct BindingDivisor { public uint Binding, Divisor; }

    // VkPipelineVertexInputDivisorStateCreateInfoEXT
    public unsafe struct DivisorState
    {
        public int SType; public void* Next;
        public uint Count;
        public BindingDivisor* Divisors;
    }

    // VkClearDepthStencilValue
    public struct DepthStencilValue
    {
        public float Depth;
        public uint Stencil;
    }
}

// Buffers, image views, samplers, shader modules, descriptors and layouts, compute pipelines, barriers and copies
internal static partial class Vk
{
    public const int BufferCreateInfo = 12, ImageViewCreateInfo = 15, ShaderModuleCreateInfo = 16, StageCreateInfo = 18;
    public const int ComputePipelineCreateInfo = 29, PipelineLayoutCreateInfo = 30, SamplerCreateInfo = 31;
    public const int SetLayoutCreateInfo = 32, WriteDescriptorSet = 35, MemoryBarrier = 46;
    public const uint HostVisible = 0x2, HostCoherent = 0x4, HostCached = 0x8, UsageUniform = 0x10, UsageStorage = 0x20;
    public const uint UsageIndex = 0x40, UsageVertex = 0x80, UsageIndirect = 0x100;
    public const uint StageCompute = 0x20, PushDescriptorLayout = 0x1;
    public const uint PipelineCompute = 0x800, PipelineIndirect = 0x2, AccessIndirect = 0x1, AccessShaderRead = 0x20;
    public const uint AccessShaderWrite = 0x40, AccessTransferRead = 0x800;
    public const int BindCompute = 1, ViewType2D = 1, FormatR32F = 100;

    public struct BufferCopy { public ulong SourceOffset, TargetOffset, Size; }

    public unsafe struct BufferInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public ulong Size;
        public uint Usage, SharingMode, FamilyCount;
        public uint* Families;
    }

    public unsafe struct ViewInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public ulong Image;
        public int ViewType;
        public uint Format, R, G, B, A;
        public ColorRange Range;
    }

    public unsafe struct SamplerInfo
    {
        public int SType; public void* Next;
        public uint Flags, MagFilter, MinFilter, MipmapMode, AddressU, AddressV, AddressW;
        public float LodBias;
        public uint Anisotropy;
        public float MaxAnisotropy;
        public uint Compare, CompareOp;
        public float MinLod, MaxLod;
        public uint Border, Unnormalized;
    }

    public unsafe struct ModuleInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public nuint CodeSize;
        public uint* Code;
    }

    public unsafe struct LayoutBinding
    {
        public uint Binding;
        public int Type;
        public uint Count, Stages;
        public ulong* Samplers;
    }

    public unsafe struct SetLayoutInfo
    {
        public int SType; public void* Next;
        public uint Flags, BindingCount;
        public LayoutBinding* Bindings;
    }

    public unsafe struct PipelineLayoutInfo
    {
        public int SType; public void* Next;
        public uint Flags, SetLayoutCount;
        public ulong* SetLayouts;
        public uint PushRangeCount;
        public void* PushRanges;
    }

    public unsafe struct StageInfo
    {
        public int SType; public void* Next;
        public uint Flags, Stage;
        public ulong Module;
        public byte* Name;
        public void* Specialization;
    }

    public unsafe struct ComputeInfo
    {
        public int SType; public void* Next;
        public uint Flags;
        public StageInfo Stage;
        public ulong Layout, Base;
        public int BaseIndex;
    }

    public unsafe struct WriteInfo
    {
        public int SType; public void* Next;
        public ulong Set;
        public uint Binding, ArrayElement, Count;
        public int Type;
        public ImageDescriptor* Images;
        public BufferDescriptor* Buffers;
        public void* TexelViews;
    }

    public struct BufferDescriptor { public ulong Buffer, Offset, Range; }

    public struct ImageDescriptor
    {
        public ulong Sampler, View;
        public int Layout;
    }

    public unsafe struct GlobalBarrier
    {
        public int SType; public void* Next;
        public uint SrcAccess, DstAccess;
    }

    public struct BufferImageCopy
    {
        public ulong Offset;
        public uint RowLength, ImageHeight, Aspect, Level, BaseLayer, Layers;
        public int X, Y, Z;
        public uint Width, Height, Depth;
    }
}
