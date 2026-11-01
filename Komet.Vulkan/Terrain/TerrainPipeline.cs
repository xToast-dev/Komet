namespace Komet.Vulkan;

// OpenGL's clip depth (-1..1) kept via VK_EXT_depth_clip_control; front face flipped, as Vulkan's y points down.
internal sealed unsafe partial class TerrainPipeline : IDisposable
{
    private const int MaxResources = 32, MaxAttributes = 16, MaxColors = 8;

    private readonly IntPtr _device;
    private ulong _cache; // the device's pipeline cache, 0 for none
    private uint _pushed; // bytes of push constants both stages read
    private bool _bresenham; // lines as OpenGL rasterizes them
    private int _disposed;
    private static int _live;

    private TerrainPipeline(IntPtr device, Resource[] resources)
    {
        (_device, Resources) = (device, resources);
        Track(1);
    }

    // The count of live pipelines, from their constructor and Dispose
    private static void Track(int by) => _ = Interlocked.Add(ref _live, by);

    // Made, not yet disposed, any thread
    public static int Live => Volatile.Read(ref _live);

    public readonly record struct Resource(int Binding, uint Type, uint Stages);

    public readonly record struct Target(uint[] Colors, uint Depth);

    public ulong Pipeline { get; private set; }
    public ulong Layout { get; private set; }
    public ulong SetLayout { get; private set; }
    public Resource[] Resources { get; }

    public static TerrainPipeline? Create(VulkanDevice device, GlslPort.Ported ported,
        TerrainPools.Attribute[] attributes, (GlTap.Fixed State, Target Target, uint Topology) pass, out string why)
    {
        why = "";
        var colors = pass.Target.Colors.Length;
        if (!NotNull(device) || !NotNull(ported) || !Assert(colors <= MaxColors)) return null;
        if (!device.DepthClipControl)
        {
            why = "the device lacks VK_EXT_depth_clip_control";
            return null;
        }

        var resources = Merged(Spirv.Resources(ported.Vertex), Spirv.Resources(ported.Fragment));
        var pipeline = new TerrainPipeline(device.Handle, resources)
        {
            _pushed = (uint)ported.HotUniforms.Size, _bresenham = device.BresenhamLines, _cache = device.PipelineCache
        };
        // formatless inputs refused before the driver: RADV divides by the size (SIGFPE), e.g. old recipes without
        // matrix columns
        return Owned.Kept(pipeline, (ported, attributes, pass), static (pipeline, s) =>
        {
            var inputs = pipeline.Inputs(s.ported, s.attributes, out var missing);
            return missing.Length > 0 ? missing : pipeline.Layouts() ?? pipeline.Build(s.ported, inputs, s.pass);
        }, out why);
    }

    // Built on a builder from inputs copied here: ports are immutable, device handles change only at start/end (it
    // waits for all builds), and the pipeline cache is not EXTERNALLY_SYNCHRONIZED, so no lock. Dropped: its builder
    // destroys it.
    public static Build<TerrainPipeline> Started(VulkanDevice device, GlslPort.Ported ported,
        TerrainPools.Attribute[] attributes, (GlTap.Fixed State, Target Target, uint Topology) pass)
    {
        TerrainPools.Attribute[] inputs = [.. attributes];
        var copied = (pass.State, new Target([.. pass.Target.Colors], pass.Target.Depth), pass.Topology);
        return new Build<TerrainPipeline>(Builds.Kind.Pipeline,
            () => (Create(device, ported, inputs, copied, out var why), why), static made => made.Dispose());
    }

    private static Resource[] Merged(Spirv.Resource[] vertex, Spirv.Resource[] fragment)
    {
        var merged = new SortedDictionary<int, Resource>();
        var both = vertex.Select(r => (r, Vk.StageVertex)).Concat(fragment.Select(r => (r, Vk.StageFragment)))
            .ToArray();
        foreach (var (r, stage) in both.Bounded(MaxResources * 2))
            merged[r.Binding] = merged.TryGetValue(r.Binding, out var known)
                ? known with { Stages = known.Stages | stage }
                : new Resource(r.Binding, (uint)r.Type, stage);
        _ = Assert(vertex.All(r => r.Set == 0) && fragment.All(r => r.Set == 0));
        return [.. merged.Values];
    }

    private string? Layouts()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Resources.Length <= MaxResources)) return "no device";
        var bindings = stackalloc Vk.LayoutBinding[MaxResources];
        for (var i = 0; i < Math.Min(Resources.Length, MaxResources); i++)
            bindings[i] = new Vk.LayoutBinding
            {
                Binding = (uint)Resources[i].Binding, Type = (int)Resources[i].Type, Count = 1,
                Stages = Resources[i].Stages
            };
        var setInfo = new Vk.SetLayoutInfo
        {
            SType = Vk.SetLayoutCreateInfo, Flags = Vk.PushDescriptorLayout, BindingCount = (uint)Resources.Length,
            Bindings = bindings
        };
        ulong set, layout;
        if (VkApi.CreateSetLayout(_device, &setInfo, null, &set) != Vk.Success) return "no descriptor set layout";
        SetLayout = set;
        var push = new Vk.PushRange { Stages = Vk.StageVertex | Vk.StageFragment, Size = _pushed };
        var layoutInfo = new Vk.PipelineLayoutInfo
        {
            SType = Vk.PipelineLayoutCreateInfo, SetLayoutCount = 1, SetLayouts = &set,
            PushRangeCount = _pushed > 0 ? 1u : 0, PushRanges = &push
        };
        if (VkApi.CreatePipelineLayout(_device, &layoutInfo, null, &layout) != Vk.Success) return "no pipeline layout";
        Layout = layout;
        return null;
    }

    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero)) return;
        if (Interlocked.Exchange(ref _disposed, 1) == 0) Track(-1);
        if (Pipeline != 0) VkApi.DestroyPipeline(_device, Pipeline, null);
        if (Layout != 0) VkApi.DestroyPipelineLayout(_device, Layout, null);
        if (SetLayout != 0) VkApi.DestroySetLayout(_device, SetLayout, null);
        (Pipeline, Layout, SetLayout) = (0, 0, 0);
    }
}

internal sealed unsafe partial class TerrainPipeline
{
    private const uint LineSmooth = 3, PolygonLine = 1, PolygonPoint = 2; // rectangular smooth lines; VkPolygonMode

    public int[] Locations { get; private set; } = [];

    private Vk.BindingDivisor[] Divisors { get; set; } = [];

    private (Vk.VertexBinding[] Bindings, Vk.VertexAttribute[] Attributes) Inputs(GlslPort.Ported ported,
        TerrainPools.Attribute[] attributes, out string missing)
    {
        missing = "";
        if (!NotNull(attributes)) return ([], []);
        var inputs = GlslPort.Columns(ported.Attributes).OrderBy(a => a.Location).ToArray();
        var (bindings, formats) = (new Vk.VertexBinding[inputs.Length], new Vk.VertexAttribute[inputs.Length]);
        for (var i = 0; i < Math.Min(inputs.Length, MaxAttributes); i++)
        {
            var location = inputs[i].Location;
            var pool = attributes.Where(a => a.Location == location).ToArray();
            var format = pool.Length == 1 ? Format(pool[0]) : 0;
            if (format == 0) missing = $"no vertex format for {inputs[i].Name} at location {location}";
            var stride = pool.Length == 1 ? pool[0].Stride : 0;
            bindings[i] = new Vk.VertexBinding
            {
                Binding = (uint)location, Stride = (uint)stride,
                InputRate = pool.Length == 1 && pool[0].Divisor > 0 ? 1u : 0
            };
            if (pool.Length == 1 && pool[0].Divisor > 1) // Vulkan-only, with VK_EXT_vertex_attribute_divisor
                Divisors = [.. Divisors, new Vk.BindingDivisor { Binding = (uint)location, Divisor = (uint)pool[0].Divisor }];
            formats[i] = new Vk.VertexAttribute
            {
                Location = (uint)location, Binding = (uint)location, Format = format
            };
        }

        Locations = [.. inputs.Select(a => a.Location)];
        return (bindings, formats);
    }

    public static uint Format(TerrainPools.Attribute a)
    {
        if (!Assert(a.Location >= 0) || a.Size is < 1 or > 4) return 0;
        var (unsigned, index) = ((uint)a.Type is GlEnum.UnsignedByte or GlEnum.UnsignedShort or GlEnum.UnsignedInt, a.Size - 1);
        var variant = (a.Integer, a.Normalized) switch
        {
            (true, _) => unsigned ? 4u : 5u,
            (_, true) => unsigned ? 0u : 1u,
            _ => unsigned ? 2u : 3u
        };
        ReadOnlySpan<uint> bytes = [9, 16, 23, 37], shorts = [70, 77, 84, 91], floats = [100, 103, 106, 109];
        ReadOnlySpan<uint> halves = [76, 83, 90, 97], ints = [98, 101, 104, 107];
        return (uint)a.Type switch
        {
            GlEnum.Int2101010Rev when a.Size == 4 && a.Normalized && !a.Integer => 65, // A2B10G10R10_SNORM_PACK32
            GlEnum.UnsignedInt2101010Rev when a.Size == 4 && a.Normalized && !a.Integer => 64, // A2B10G10R10_UNORM_PACK32
            GlEnum.Byte or GlEnum.UnsignedByte => bytes[index] + variant,
            GlEnum.Short or GlEnum.UnsignedShort => shorts[index] + variant,
            GlEnum.Float when !a.Integer => floats[index],
            GlEnum.HalfFloat when !a.Integer => halves[index],
            GlEnum.Int or GlEnum.UnsignedInt when a.Integer => ints[index] + (unsigned ? 0u : 1u),
            _ => 0
        };
    }

    private string? Build(GlslPort.Ported ported, (Vk.VertexBinding[] Bindings, Vk.VertexAttribute[] Attributes) input,
        (GlTap.Fixed State, Target Target, uint Topology) pass)
    {
        var (vertex, fragment) = (Module(ported.Vertex), Module(ported.Fragment));
        try
        {
            if (vertex == 0 || fragment == 0) return "a shader module was refused";
            var main = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
            var stages = stackalloc Vk.StageInfo[2];
            stages[0] = new Vk.StageInfo
            {
                SType = Vk.StageCreateInfo, Stage = Vk.StageVertex, Module = vertex, Name = main
            };
            stages[1] = new Vk.StageInfo
            {
                SType = Vk.StageCreateInfo, Stage = Vk.StageFragment, Module = fragment, Name = main
            };
            fixed (Vk.VertexBinding* bindings = input.Bindings)
            fixed (Vk.VertexAttribute* attributes = input.Attributes)
            fixed (Vk.BindingDivisor* divisors = Divisors)
            {
                var divided = new Vk.DivisorState
                {
                    SType = Vk.DivisorStateInfo, Count = (uint)Divisors.Length, Divisors = divisors
                };
                var vertexInput = new Vk.VertexInputState
                {
                    SType = Vk.VertexInputStateInfo, Next = Divisors.Length > 0 ? &divided : null,
                    BindingCount = (uint)input.Bindings.Length, Bindings = bindings,
                    AttributeCount = (uint)input.Attributes.Length, Attributes = attributes
                };
                return Graphics(stages, &vertexInput, pass);
            }
        }
        finally
        {
            if (vertex != 0) VkApi.DestroyShaderModule(_device, vertex, null);
            if (fragment != 0) VkApi.DestroyShaderModule(_device, fragment, null);
        }
    }

    private ulong Module(uint[] words)
    {
        if (!NotNull(words) || !Assert(words.Length > 5)) return 0;
        ulong module;
        fixed (uint* code = words)
        {
            var info = new Vk.ModuleInfo
            {
                SType = Vk.ShaderModuleCreateInfo, CodeSize = (nuint)(4 * words.Length), Code = code
            };
            return VkApi.CreateShaderModule(_device, &info, null, &module) == Vk.Success ? module : 0;
        }
    }

    private string? Graphics(Vk.StageInfo* stages, Vk.VertexInputState* vertexInput,
        (GlTap.Fixed State, Target Target, uint Topology) pass)
    {
        var (state, target, topology) = pass;
        var assembly = new Vk.InputAssemblyState { SType = Vk.InputAssemblyStateInfo, Topology = topology };
        var clip = new Vk.OneFeature { SType = Vk.DepthClipControlCreateInfo, Enabled = 1 };
        var viewport = new Vk.ViewportState
        {
            SType = Vk.ViewportStateInfo, Next = &clip, ViewportCount = 1, ScissorCount = 1
        };
        var raster = Raster(state);
        var lines = topology is Vk.TopologyLines or Vk.TopologyLineStrip || (state.Caps & (uint)GlTap.Caps.WireLines) != 0;
        var smooth = (state.Caps & (uint)GlTap.Caps.LineSmooth) != 0;
        var line = new Vk.LineState { SType = Vk.LineStateInfo, Mode = smooth ? LineSmooth : Vk.LineBresenham };
        if (lines && (_bresenham || smooth)) raster.Next = &line; // OpenGL's lines, not Vulkan's parallelograms
        var multisample = new Vk.MultisampleState { SType = Vk.MultisampleStateInfo, Samples = 1 };
        var depth = Tested(state, target.Depth);
        var blends = stackalloc Vk.BlendAttachment[MaxColors];
        for (var i = 0; i < Math.Min(target.Colors.Length, MaxColors); i++) blends[i] = Blending(state.Blend[i]);
        var logic = (state.Caps & (uint)GlTap.Caps.LogicOp) != 0;
        var blend = new Vk.ColorBlendState
        {
            SType = Vk.ColorBlendStateInfo, AttachmentCount = (uint)target.Colors.Length, Attachments = blends,
            LogicOpEnable = logic ? 1u : 0, LogicOp = logic ? (state.Caps & GlTap.LogicOperationBits) >> 12 : 0
        };
        var stencil = depth.Stencil != 0; // its reference and masks are dynamic: the draw sets them (Stenciling)
        var dynamics = stackalloc uint[]
        {
            Vk.DynamicViewport, Vk.DynamicScissor, stencil ? Vk.DynamicStencilCompare : Vk.DynamicLineWidth,
            Vk.DynamicStencilWrite, Vk.DynamicStencilReference, Vk.DynamicLineWidth
        };
        var dynamic = new Vk.DynamicState
        {
            SType = Vk.DynamicStateInfo, Count = (stencil ? 5u : 2) + (lines ? 1u : 0), States = dynamics
        };
        fixed (uint* colors = target.Colors)
        {
            var rendering = new Vk.PipelineRendering
            {
                SType = Vk.RenderingCreateInfo, ColorCount = (uint)target.Colors.Length, ColorFormats = colors,
                DepthFormat = target.Depth, StencilFormat = Stenciled(target.Depth) ? target.Depth : 0
            };
            var info = new Vk.GraphicsInfo
            {
                SType = Vk.GraphicsPipelineCreateInfo, Next = &rendering, StageCount = 2, Stages = stages,
                VertexInput = vertexInput, InputAssembly = &assembly, Viewport = &viewport, Rasterization = &raster,
                Multisample = &multisample, DepthStencil = &depth, ColorBlend = &blend, Dynamic = &dynamic,
                Layout = Layout
            };
            ulong pipeline;
            if (VkApi.CreateGraphicsPipelines(_device, _cache, 1, &info, null, &pipeline) != Vk.Success)
                return "vkCreateGraphicsPipelines failed";
            Pipeline = pipeline;
            return null;
        }
    }

    // The rendering attaches the depth as stencil too
    public static bool Stenciled(uint format) => format is Vk.FormatD24S8 or Vk.FormatD32S8;

    // Stencil only where the target has one (GlTap.StencilWord ops; reference and masks dynamic)
    private static Vk.DepthStencilState Tested(GlTap.Fixed state, uint format)
    {
        var test = (state.Caps & (uint)GlTap.Caps.DepthTest) != 0;
        var stencil = (state.Caps & (uint)GlTap.Caps.Stencil) != 0 && Stenciled(format);
        return new Vk.DepthStencilState
        {
            SType = Vk.DepthStencilStateInfo, DepthTest = test ? 1u : 0,
            DepthWrite = test && state.DepthWrite ? 1u : 0, DepthCompare = state.DepthCompare, MaxDepthBounds = 1,
            Stencil = stencil ? 1u : 0, Front = stencil ? Face(state.Stencil) : default,
            Back = stencil ? Face(state.Stencil >> 12) : default
        };
    }

    private static Vk.StencilState Face(uint word) =>
        Assert(word < 1u << 24)
            ? new Vk.StencilState
            {
                Compare = word & 7, Fail = (word >> 3) & 7, DepthFail = (word >> 6) & 7, Pass = (word >> 9) & 7,
                CompareMask = 0xFF, WriteMask = 0xFF
            }
            : default;

    private static uint Polygon(uint caps)
    {
        if ((caps & (uint)GlTap.Caps.WireLines) != 0) return PolygonLine;
        return (caps & (uint)GlTap.Caps.WirePoints) != 0 ? PolygonPoint : 0;
    }

    private static Vk.RasterizationState Raster(GlTap.Fixed state)
    {
        var offset = (state.Caps & (uint)GlTap.Caps.PolygonOffsetFill) != 0;
        return new Vk.RasterizationState
        {
            SType = Vk.RasterizationStateInfo, CullMode = state.Culls, LineWidth = 1,
            PolygonMode = Polygon(state.Caps),
            FrontFace = state.CounterClockwise ? Vk.FrontClockwise : Vk.FrontCounterClockwise,
            DepthClamp = (state.Caps & (uint)GlTap.Caps.DepthClamp) != 0 ? 1u : 0, DepthBias = offset ? 1u : 0,
            BiasConstant = offset ? state.OffsetUnits : 0, BiasSlope = offset ? state.OffsetFactor : 0
        };
    }

    private static Vk.BlendAttachment Blending(uint word)
    {
        return new Vk.BlendAttachment
        {
            Enable = (word >> 26) & 1, SourceColor = word & 0xF, TargetColor = (word >> 4) & 0xF,
            SourceAlpha = (word >> 8) & 0xF, TargetAlpha = (word >> 12) & 0xF, ColorOp = (word >> 16) & 7,
            AlphaOp = (word >> 19) & 7, WriteMask = (word >> 22) & 0xF
        };
    }

    public static bool Blendable(uint word)
    {
        if (((word >> 26) & 1) == 0) return true;
        var factors = (word & 0xF, (word >> 4) & 0xF, (word >> 8) & 0xF, (word >> 12) & 0xF);
        var equations = ((word >> 16) & 7, (word >> 19) & 7);
        const uint no = GlTap.Unsupported;
        return factors is not (no, _, _, _) and not (_, no, _, _) and not (_, _, no, _) and not (_, _, _, no) &&
               equations is not (7, _) and not (_, 7);
    }
}
