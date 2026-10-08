using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

internal readonly record struct SharedFormat(uint Vulkan, int Gl, uint Aspect)
{
    public static readonly SharedFormat Rgba8 = new(Vk.FormatRgba8, GlInterop.Rgba8, Vk.AspectColor);
    public static readonly SharedFormat Rgba16F = new(Vk.FormatRgba16F, GlInterop.Rgba16F, Vk.AspectColor);
    public static readonly SharedFormat Depth32F = new(Vk.FormatD32F, GlInterop.Depth32F, Vk.AspectDepth);
    public static readonly SharedFormat R16F = new(Vk.FormatR16F, GlInterop.R16F, Vk.AspectColor);
    public static readonly SharedFormat Rgba32F = new(Vk.FormatRgba32F, 0x8814, Vk.AspectColor);
    public static readonly SharedFormat R8 = new(Vk.FormatR8, 0x8229, Vk.AspectColor);
    public static readonly SharedFormat Rg16F = new(Vk.FormatRg16F, 0x822F, Vk.AspectColor);
    public static readonly SharedFormat R32F = new(Vk.FormatR32F, 0x822E, Vk.AspectColor);
    public static readonly SharedFormat Rgba16 = new(Vk.FormatRgba16, 0x805B, Vk.AspectColor);

    public static SharedFormat? Of(int gl)
    {
        _ = Assert(gl >= 0) && Assert(Rgba8.Gl == 0x8058);
        return gl switch
        {
            0x8058 or 0x1908 => Rgba8, // RGBA8, and the unsized RGBA drivers make RGBA8
            0x881A => Rgba16F,
            0x822D => R16F,
            0x8814 => Rgba32F,
            0x8229 => R8,
            0x822F => Rg16F,
            0x822E => R32F,
            0x805B => Rgba16,
            0x8CAC or 0x81A7 or 0x1902 or 0x81A6 => Depth32F, // DEPTH_COMPONENT32F, 32, unsized, 24
            _ => null
        };
    }
}

// Vulkan hands it to OpenGL with a queue family release to VK_QUEUE_FAMILY_EXTERNAL and a SharedSemaphore, OpenGL back with
// glSignalSemaphoreEXT. A private one (not Exported) is Vulkan's alone: no GL texture, no handoff, memory no submission names.
internal sealed unsafe class SharedImage : IDisposable
{
    private readonly IntPtr _device;
    private ulong _image, _memory, _view;
    private uint _glMemory;

    private readonly ulong[] _layerViews = new ulong[MaxLayers];

    private const int MaxLayers = 16;

    private SharedImage(IntPtr device, (int Width, int Height, int Levels, int Layers) size, SharedFormat format)
    {
        _ = Assert(device != IntPtr.Zero) && Assert(size.Levels is > 0 and <= 16);
        (_device, Width, Height, Levels, Format) = (device, size.Width, size.Height, size.Levels, format);
        Layers = Math.Clamp(size.Layers, 1, MaxLayers);
    }

    public ulong Image => _image;
    public int Texture { get; private set; }

    public ulong View
    {
        get
        {
            if (_view == 0 && Assert(_image != 0)) _view = MadeView(0, Whole);
            return _view;
        }
    }

    // Sampled with alpha 1, as OpenGL samples the RGB texture this image stands in for (its GL texture swizzles the same way)
    public bool OpaqueAlpha { get; set; }

    public ulong Sampled
    {
        get
        {
            if (!OpaqueAlpha) return View;
            if (_sampled == 0 && Assert(_image != 0)) _sampled = MadeView(SwizzleOne, Whole);
            return _sampled;
        }
    }

    private const uint SwizzleOne = 2;
    private ulong _sampled;

    private Vk.ColorRange Whole => new() { Aspect = Format.Aspect, Levels = (uint)Levels, Layers = (uint)Layers };

    // A view of level 0 of one layer, as a framebuffer attaches it (FramebufferTextureLayer); the whole view for a plain image
    public ulong LayerView(int layer)
    {
        if (Layers == 1 || !Index(layer, Layers)) return View;
        if (_layerViews[layer] == 0 && Assert(_image != 0))
            _layerViews[layer] = MadeView(0,
                new Vk.ColorRange { Aspect = Format.Aspect, Levels = 1, BaseLayer = (uint)layer, Layers = 1 });
        return _layerViews[layer];
    }

    private ulong MadeView(uint alpha, Vk.ColorRange range)
    {
        if (!Assert(_image != 0) || !Assert(range.Layers > 0)) return 0;
        var type = range.Layers > 1 ? Vk.ViewType2DArray : Vk.ViewType2D;
        var info = new Vk.ViewInfo
        {
            SType = Vk.ImageViewCreateInfo, Image = _image, ViewType = Cube && range.Layers == 6 ? Vk.ViewTypeCube : type,
            Format = Format.Vulkan, A = alpha, Range = range
        };
        ulong view;
        return VkApi.CreateImageView(_device, &info, null, &view) == Vk.Success ? view : 0;
    }

    public Handoff.Image In(int layout) =>
        Assert(layout >= 0)
            ? new Handoff.Image(_image, layout, Format.Aspect, Levels) { Layers = Layers, Private = !Exported }
            : default;

    public bool Exported { get; private init; } = true;

    // Six layers sampled as a cube (a GL cube map's copy); a layer view stays 2D
    public bool Cube { get; private init; }

    // The last frame (VulkanFrame.Number) whose draws sampled it: the texture copy unused longest makes room first
    public long Used { get; set; } = -1;
    public int Layers { get; }
    public int Width { get; }
    public int Height { get; }
    public int Levels { get; }
    public SharedFormat Format { get; }

    public static SharedImage? Create(VulkanDevice device, int width, int height, uint usage, out string why) =>
        Create(device, width, height, 1, SharedFormat.Rgba8, usage, out why);

    public static SharedImage? Create(VulkanDevice device, int width, int height, int levels, SharedFormat format,
        uint usage, out string why) => Create(device, (width, height, levels, 1), format, usage, out why);

    public static SharedImage? Create(VulkanDevice device, (int Width, int Height, int Levels, int Layers) size,
        SharedFormat format, uint usage, out string why)
    {
        why = "";
        var (width, height, levels, layers) = size;
        if (!NotNull(device) || !Assert(width > 0 && height > 0) || !Assert(usage != 0 && levels is > 0 and <= 16) ||
            !Assert(layers is > 0 and <= MaxLayers))
            return null;
        var shared = new SharedImage(device.Handle, (width, height, levels, layers), format);
        why = shared.Allocate(device, usage) ?? shared.Import() ?? "";
        if (why.Length == 0) return shared;
        shared.Dispose();
        return null;
    }

    public static SharedImage? Private(VulkanDevice device, (int Width, int Height, int Levels, int Layers) size,
        SharedFormat format, uint usage, out string why, bool cube = false)
    {
        why = "";
        if (!NotNull(device) || !Assert(size.Width > 0 && size.Height > 0) || !Assert(size.Levels is > 0 and <= 16) ||
            !Assert(size.Layers is > 0 and <= MaxLayers) || !Assert(!cube || size.Layers == 6)) return null;
        var image = new SharedImage(device.Handle, size, format) { Exported = false, Cube = cube };
        why = image.Allocate(device, usage) ?? "";
        if (why.Length == 0) return image;
        image.Dispose();
        return null;
    }

    // An existing GL texture moved onto a new image: its name stays, its storage is the image's memory now (contents gone).
    // Null with the reason when OpenGL refuses (an immutable texture).
    public static SharedImage? Adopt(VulkanDevice device, int texture, (int Width, int Height, int Levels, int Layers) size,
        SharedFormat format, uint usage, out string why)
    {
        why = "";
        if (!NotNull(device) || !Assert(texture > 0) || !Assert(size.Width > 0 && size.Height > 0)) return null;
        var shared = new SharedImage(device.Handle, size, format);
        why = shared.Allocate(device, usage) ?? shared.Import(texture) ?? "";
        if (why.Length == 0) return shared;
        shared.Texture = 0; // the name stays the engine's
        shared.Dispose();
        return null;
    }

    private string? Allocate(VulkanDevice device, uint usage)
    {
        if (!Assert(_image == 0) || !Assert(usage != 0)) return "allocated already";
        var external = new Vk.Chained { SType = Vk.ExternalMemoryImageCreateInfo, HandleTypes = Vk.OpaqueFd };
        var info = new Vk.ImageInfo
        {
            SType = Vk.ImageCreateInfo, Next = Exported ? &external : null, Flags = Cube ? Vk.CubeCompatible : 0,
            ImageType = 1, Format = Format.Vulkan,
            Width = (uint)Width,
            Height = (uint)Height, Depth = 1, MipLevels = (uint)Levels, ArrayLayers = (uint)Layers, Samples = 1,
            Usage = usage,
            InitialLayout = Vk.LayoutUndefined
        };
        ulong image, memory;
        if (VkApi.CreateImage(_device, &info, null, &image) != Vk.Success) return "vkCreateImage failed";
        _image = image;
        Vk.MemoryRequirements needs;
        VkApi.GetImageMemoryRequirements(_device, image, &needs);
        var type = device.MemoryType(needs.TypeBits, Vk.DeviceLocal);
        if (type < 0) return "no device-local memory for the image";
        var dedicated = new Vk.DedicatedInfo { SType = Vk.MemoryDedicatedAllocateInfo, Image = image };
        var export = new Vk.Chained
        {
            SType = Vk.ExportMemoryAllocateInfo, Next = &dedicated, HandleTypes = Vk.OpaqueFd
        };
        // A private image's memory is neither exportable nor dedicated: RADV makes only such memory local to the device's
        // address space, which no submission has to name
        var allocate = new Vk.AllocateInfo
        {
            SType = Vk.MemoryAllocateInfo, Next = Exported ? &export : null, Size = needs.Size, TypeIndex = (uint)type
        };
        if (VkApi.AllocateMemory(_device, &allocate, null, &memory) != Vk.Success) return "vkAllocateMemory failed";
        _memory = memory;
        Size = needs.Size;
        return VkApi.BindImageMemory(_device, image, memory, 0) == Vk.Success ? null : "vkBindImageMemory failed";
    }

    private ulong Size { get; set; }

    private string? Import(int texture = 0)
    {
        if (!Assert(_memory != 0) || !Assert(Size > 0 && texture >= 0)) return "no memory";
        var get = new Vk.GetFdInfo { SType = Vk.MemoryGetFdInfo, Handle = _memory, HandleType = Vk.OpaqueFd };
        int fd;
        if (VkApi.GetMemoryFd(_device, &get, &fd) != Vk.Success || fd < 0) return "vkGetMemoryFdKHR failed";
        if (texture == 0)
        {
            (Texture, _glMemory) = GlInterop.Texture(fd, Size, (Width, Height, Levels, Layers), Format.Gl);
            return Texture != 0 ? null : "OpenGL did not take the image's memory";
        }

        _glMemory = GlInterop.Adopt(texture, fd, Size, (Width, Height, Levels, Layers), Format.Gl);
        Texture = texture;
        return _glMemory != 0 ? null : $"OpenGL did not move texture {texture} onto the image's memory";
    }

    // Vulkan lets go and OpenGL keeps the texture, whose memory stays while the texture does: the owner of the texture (the
    // engine's framebuffer) deletes it when it wants to, and no GL name changes under it
    public void Abandon()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Texture >= 0)) return;
        Texture = 0;
        Dispose();
    }

    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Texture >= 0)) return;
        if (Texture != 0) GL.DeleteTexture(Texture);
        GlInterop.Delete(_glMemory, 0);
        if (_view != 0) VkApi.DestroyImageView(_device, _view, null);
        if (_sampled != 0) VkApi.DestroyImageView(_device, _sampled, null);
        _sampled = 0;
        foreach (var view in _layerViews.Bounded(MaxLayers))
            if (view != 0)
                VkApi.DestroyImageView(_device, view, null);
        Array.Clear(_layerViews);
        if (_image != 0) VkApi.DestroyImage(_device, _image, null);
        if (_memory != 0) VkApi.FreeMemory(_device, _memory, null);
        (Texture, _glMemory, _image, _memory, _view) = (0, 0, 0, 0, 0);
    }
}

internal sealed unsafe class SharedSemaphore : IDisposable
{
    private readonly IntPtr _device;
    private ulong _semaphore;

    private SharedSemaphore(IntPtr device)
    {
        _ = Assert(device != IntPtr.Zero);
        _device = device;
    }

    public ulong Vulkan => _semaphore;
    public uint Gl { get; private set; }

    public static SharedSemaphore? Create(VulkanDevice device)
    {
        if (!NotNull(device) || !Assert(device.Handle != IntPtr.Zero)) return null;
        var shared = new SharedSemaphore(device.Handle);
        if (shared.Make()) return shared;
        shared.Dispose();
        return null;
    }

    private bool Make()
    {
        if (!Assert(_semaphore == 0) || !Assert(_device != IntPtr.Zero)) return false;
        var export = new Vk.Chained { SType = Vk.ExportSemaphoreCreateInfo, HandleTypes = Vk.OpaqueFd };
        var info = new Vk.FlagsInfo { SType = Vk.SemaphoreCreateInfo, Next = &export };
        ulong semaphore;
        int fd;
        if (VkApi.CreateSemaphore(_device, &info, null, &semaphore) != Vk.Success) return false;
        _semaphore = semaphore;
        var get = new Vk.GetFdInfo { SType = Vk.SemaphoreGetFdInfo, Handle = semaphore, HandleType = Vk.OpaqueFd };
        if (VkApi.GetSemaphoreFd(_device, &get, &fd) == Vk.Success && fd >= 0) Gl = GlInterop.Semaphore(fd);
        return Gl != 0;
    }

    public void Dispose()
    {
        if (!Assert(_device != IntPtr.Zero) || !Assert(Gl == 0 || _semaphore != 0)) return;
        GlInterop.Delete(0, Gl);
        if (_semaphore != 0) VkApi.DestroySemaphore(_device, _semaphore, null);
        (Gl, _semaphore) = (0, 0);
    }
}
