using Komet.Core;
using Komet.Rendering;

namespace Komet.Vulkan;

// Each dispatch takes every resource from the per-kind binding tables (OpenGL's namespaces), or the placeholder. A frame
// records dispatches and buffer copies later on its thread without closures (Recorded): pooled, dropped ones go to the GC.
internal sealed unsafe partial class VulkanBackend
{
    private const int MaxUnits = 64, MaxResources = 16, MaxIdle = 512;
    private readonly BoundedPool<Recorded> _idle = new(MaxIdle); // the frame's recording thread gives them back
    private readonly (int Image, int Level)[] _imageUnits = new (int, int)[MaxUnits];

    private readonly ProgramSlot?[] _programs = new ProgramSlot?[MaxHandles];
    private readonly int[] _storage = new int[MaxUnits], _uniforms = new int[MaxUnits], _textures = new int[MaxUnits];
    private ulong _sampler;

    public int Program(string shader, ILogger logger)
    {
        if (!Assert(shader.Length > 0) || GpuShaders.Source(shader) is not { } source) return 0;

        var words = Shaderc.Compile(source, Shaderc.Compute, shader, out var error);
        if (words is null)
        {
            logger.Warning("Komet: {0} does not compile for Vulkan: {1}", shader, error);
            return 0;
        }

        var at = Free(_programs);
        if (at == 0 || Pipeline(words, Spirv.Resources(words)) is not { } program) return 0;

        _programs[at] = program;
        return at;
    }

    public void Storage(int binding, int buffer) => Bind(_storage, binding, buffer);

    public void Uniforms(int binding, int buffer) => Bind(_uniforms, binding, buffer);

    public void Texture(int unit, int texture) => Bind(_textures, unit, texture);

    public void Image(int unit, int texture, int level, bool write)
    {
        if (!Index(unit, MaxUnits) || !Assert(level >= 0) || !Index(texture, MaxHandles)) return;

        _imageUnits[unit] = (texture, level);
    }

    // Descriptors from the binding tables as they stand now; recorded now, or on a frame posted to its thread
    public void Dispatch(int program, int x, int y = 1)
    {
        if (!Index(program, MaxHandles) || _programs[program] is not { } p || !Assert(x >= 0 && y >= 0) ||
            x * y == 0)
        {
            return;
        }

        if (_placeholderImage == 0 && _frame is not null) _placeholderImage = Pyramid(1, 1, out _);

        if (!Assert(_placeholderImage != 0)) return;

        VulkanRenderer.Mark(VulkanRenderer.Drawn.Compute);
        var op = Taken();
        (op.Count, op.Groups) = (Math.Min(p.Resources.Length, MaxResources), (x, y));
        for (var i = 0; i < Math.Min(op.Count, MaxResources); i++)
        {
            var r = p.Resources[i];
            op.Buffers[i] = default;
            op.Images[i] = default;
            if (r.Type is Spirv.StorageBuffer or Spirv.UniformBuffer)
            {
                op.Buffers[i] = Described(r);
            }
            else if (Viewed(r) is { } image)
            {
                op.Images[i] = image;
            }
            else
            {
                Recycled(op); // an engine image the frame could not make readable: nothing is recorded
                return;
            }
        }

        (op.Program, op.After) = (p, _transferred);
        _transferred = false;
        Record(op.Run);
    }

    // Every write before, by shaders or copies, is seen by every read or write after, including indirect commands
    public void Barrier(GpuBarrier wait)
    {
        if (!Assert(wait != GpuBarrier.None)) return;

        _transferred = false;
        Record(Ordered);
    }

    public void DeleteProgram(ref int program)
    {
        if (!Index(program, MaxHandles) || program == 0 || !Assert(_programs[program] is not null || _disposed)) return;

        Idle();
        if (_programs[program] is { } p) DestroyProgram(p);

        _programs[program] = null;
        program = 0;
    }

    private ProgramSlot? Pipeline(uint[] words, Spirv.Resource[] resources)
    {
        if (!Assert(resources.Length <= MaxResources) || !Assert(words.Length > 5)) return null;

        var bindings = stackalloc Vk.LayoutBinding[MaxResources];
        for (var i = 0; i < Math.Min(resources.Length, MaxResources); i++)
        {
            bindings[i] = new Vk.LayoutBinding
            {
                Binding = (uint)resources[i].Binding, Type = resources[i].Type, Count = 1, Stages = Vk.StageCompute
            };
        }

        ulong module, set = 0, layout = 0, pipeline = 0;
        fixed (uint* code = words)
        {
            var info = new Vk.ModuleInfo
            {
                SType = Vk.ShaderModuleCreateInfo, CodeSize = (nuint)(4 * words.Length), Code = code
            };
            if (VkApi.CreateShaderModule(_handle, &info, null, &module) != Vk.Success) return null;
        }

        var setInfo = new Vk.SetLayoutInfo
        {
            SType = Vk.SetLayoutCreateInfo, Flags = Vk.PushDescriptorLayout, BindingCount = (uint)resources.Length,
            Bindings = bindings
        };
        var layoutInfo = new Vk.PipelineLayoutInfo
        {
            SType = Vk.PipelineLayoutCreateInfo, SetLayoutCount = 1, SetLayouts = &set
        };
        var made = VkApi.CreateSetLayout(_handle, &setInfo, null, &set) == Vk.Success &&
                   VkApi.CreatePipelineLayout(_handle, &layoutInfo, null, &layout) == Vk.Success;
        var main = stackalloc byte[] { (byte)'m', (byte)'a', (byte)'i', (byte)'n', 0 };
        var compute = new Vk.ComputeInfo
        {
            SType = Vk.ComputePipelineCreateInfo, Layout = layout,
            Stage = new Vk.StageInfo
            {
                SType = Vk.StageCreateInfo, Stage = Vk.StageCompute, Module = module, Name = main
            }
        };
        made = made &&
               VkApi.CreateComputePipelines(_handle, _device.PipelineCache, 1, &compute, null, &pipeline) == Vk.Success;
        VkApi.DestroyShaderModule(_handle, module, null);
        var program = new ProgramSlot(pipeline, layout, set, resources);
        if (made) return program;

        DestroyProgram(program);
        return null;
    }

    // As GL unbinds a deleted texture: Free hands the number to the next image, which an old binding would then sample
    private void Unbound(int image)
    {
        for (var i = 0; i < MaxUnits; i++)
        {
            if (_textures[i] == image) _textures[i] = 0;

            if (_imageUnits[i].Image == image) _imageUnits[i] = (0, 0);
        }
    }

    private static void Bind(int[] table, int at, int handle)
    {
        if (!Index(at, MaxUnits) || !Index(handle, MaxHandles)) return;

        table[at] = handle;
    }

    private static void Dispatched(IntPtr commands, ProgramSlot p, Recorded described)
    {
        var (count, groups) = (Math.Min(described.Count, MaxResources), described.Groups);
        var writes = stackalloc Vk.WriteInfo[MaxResources];
        fixed (Vk.BufferDescriptor* buffers = described.Buffers)
        {
            fixed (Vk.ImageDescriptor* images = described.Images)
            {
                for (var i = 0; i < Math.Min(count, MaxResources); i++)
                {
                    writes[i] = new Vk.WriteInfo
                    {
                        SType = Vk.WriteDescriptorSet, Binding = (uint)p.Resources[i].Binding, Count = 1,
                        Type = p.Resources[i].Type, Buffers = buffers + i, Images = images + i
                    };
                }

                VkApi.CmdBindPipeline(commands, Vk.BindCompute, p.Pipeline);
                VkApi.CmdPushDescriptors(commands, Vk.BindCompute, p.Layout, 0, (uint)count, writes);
            }
        }

        VkApi.CmdDispatch(commands, (uint)groups.X, (uint)groups.Y, 1);
    }

    private static void Dispatched(IntPtr commands, Recorded described)
    {
        if (described.After)
        {
            Ordered(commands); // the copies and fills since the last barrier
        }

        if (described.Program is { } p) Dispatched(commands, p, described);
    }

    private Vk.BufferDescriptor Described(Spirv.Resource r)
    {
        var table = r.Type == Spirv.StorageBuffer ? _storage : _uniforms;
        var handle = Index(r.Binding, MaxUnits) && table[r.Binding] != 0 ? table[r.Binding] : _placeholderBuffer;
        var view = _buffers[handle] is { } b && b.View.Buffer != 0 ? b.View : _buffers[_placeholderBuffer]!.View;
        return Assert(view.Buffer != 0)
            ? new Vk.BufferDescriptor { Buffer = view.Buffer, Offset = view.Offset, Range = view.Range }
            : default;
    }

    // An engine image (Engine) is sampled in the layout the frame moved it into; null when the frame could not
    private Vk.ImageDescriptor? Viewed(Spirv.Resource r)
    {
        var (storage, unit) = (r.Type == Spirv.StorageImage, Math.Clamp(r.Binding, 0, MaxUnits - 1));
        var (handle, level) = storage ? _imageUnits[unit] : (_textures[unit], 0);
        if (!Index(handle, MaxHandles) || _images[handle] is null) handle = _placeholderImage;

        var image = _images[handle]!;
        var (view, sampler) = storage
            ? (image.Levels[Math.Clamp(level, 0, image.Levels.Length - 1)], 0UL)
            : (image.All, _sampler);
        var layout = Vk.LayoutGeneral;
        if (image.Shared is not { } shared) // the frame moves it to be sampled, unless its open segment does not hold it
        {
            return Assert(view != 0)
                ? new Vk.ImageDescriptor { Sampler = sampler, View = view, Layout = layout }
                : default;
        }

        layout = shared.Format.Aspect == Vk.AspectDepth ? Vk.LayoutDepthReadOnly : Vk.LayoutShaderRead;
        if (_frame?.Sample(shared, layout) == true)
        {
            return Assert(view != 0)
                ? new Vk.ImageDescriptor { Sampler = sampler, View = view, Layout = layout }
                : default;
        }

        Refused++;
        return null;

    }

    internal static void Ordered(IntPtr commands)
    {
        if (!Assert(commands != IntPtr.Zero)) return;

        var barrier = new Vk.GlobalBarrier
        {
            SType = Vk.MemoryBarrier, SrcAccess = Vk.AccessShaderWrite | Vk.AccessTransferWrite,
            DstAccess = Vk.AccessShaderRead | Vk.AccessShaderWrite | Vk.AccessIndirect | Vk.AccessTransferRead |
                        Vk.AccessTransferWrite
        };
        VkApi.CmdPipelineBarrier(commands, Vk.PipelineCompute | Vk.StageTransfer,
            Vk.PipelineCompute | Vk.StageTransfer | Vk.PipelineIndirect, 0, 1, &barrier, 0, null, 0, null);
    }

    private void DestroyProgram(ProgramSlot program)
    {
        if (program.Pipeline != 0) VkApi.DestroyPipeline(_handle, program.Pipeline, null);

        if (program.Layout != 0) VkApi.DestroyPipelineLayout(_handle, program.Layout, null);

        if (program.Set != 0) VkApi.DestroySetLayout(_handle, program.Set, null);
    }

    private Recorded Taken() => _idle.Take() is { Program: null } op ? op : new Recorded(this);

    private void Recycled(Recorded op) => _idle.Give(op);

    private sealed class Recorded
    {
        public readonly Vk.BufferDescriptor[] Buffers = new Vk.BufferDescriptor[MaxResources];
        public readonly Vk.ImageDescriptor[] Images = new Vk.ImageDescriptor[MaxResources];
        public readonly Action<IntPtr> Run;
        private readonly VulkanBackend _owner;
        public bool After, Fill; // a dispatch after copies or fills not yet ordered; the transfer fills
        public (ulong From, ulong To, Vk.BufferCopy Region) Copy;
        public int Count;
        public (int X, int Y) Groups;
        public ProgramSlot? Program; // a dispatch; null: the copy or fill

        public Recorded(VulkanBackend owner) => (_owner, Run) = (owner, Execute);

        private void Execute(IntPtr commands)
        {
            if (Program is not null)
            {
                Dispatched(commands, this);
            }
            else if (Fill)
            {
                VkApi.CmdFillBuffer(commands, Copy.To, Copy.Region.TargetOffset, Copy.Region.Size, 0);
            }
            else
            {
                var region = Copy.Region;
                VkApi.CmdCopyBuffer(commands, Copy.From, Copy.To, 1, &region);
            }

            (Program, After) = (null, false);
            _owner.Recycled(this);
        }
    }

    private sealed record ProgramSlot(ulong Pipeline, ulong Layout, ulong Set, Spirv.Resource[] Resources);
}
