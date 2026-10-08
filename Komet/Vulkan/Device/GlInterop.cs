using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Komet.Vulkan;

// GL_EXT_memory_object(_fd) and GL_EXT_semaphore(_fd), which OpenTK does not bind, taken from the context through
// glfwGetProcAddress. Needs a current context.
internal static unsafe class GlInterop
{
    public const int DeviceUuid = 0x9597, HandleOpaqueFd = 0x9586, DedicatedMemory = 0x9581, Rgba8 = 0x8058;
    public const int Rgba16F = 0x881A, Depth32F = 0x8CAC, R16F = 0x822D;
    public const uint LayoutGeneral = 0x958D, LayoutColorAttachment = 0x958E, LayoutShaderRead = 0x9591;
    public const uint LayoutTransferDst = 0x9593, LayoutDepthAttachment = 0x958F;

    private static readonly string[] Wanted =
        ["GL_EXT_memory_object", "GL_EXT_memory_object_fd", "GL_EXT_semaphore", "GL_EXT_semaphore_fd"];

    private static delegate* unmanaged<int, uint*, void> _createMemoryObjects;
    private static delegate* unmanaged<int, uint*, void> _deleteMemoryObjects;
    private static delegate* unmanaged<uint, uint, int*, void> _memoryObjectParameter;
    private static delegate* unmanaged<uint, ulong, uint, int, void> _importMemoryFd;
    private static delegate* unmanaged<uint, int, uint, int, int, uint, ulong, void> _textureStorageMem2D;
    private static delegate* unmanaged<uint, nint, uint, ulong, void> _bufferStorageMem;
    private static delegate* unmanaged<uint, int, uint, int, int, int, uint, ulong, void> _textureStorageMem3D;
    private static delegate* unmanaged<int, uint*, void> _genSemaphores;
    private static delegate* unmanaged<int, uint*, void> _deleteSemaphores;
    private static delegate* unmanaged<uint, uint, int, void> _importSemaphoreFd;
    private static delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void> _waitSemaphore;
    private static delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void> _signalSemaphore;
    private static delegate* unmanaged<uint, uint, byte*, void> _getUnsignedBytei;

    public static bool Load()
    {
        foreach (var extension in Wanted.Bounded(Wanted.Length))
            if (!Diagnostics.GpuStats.Offered(extension))
                return false;
        _createMemoryObjects = (delegate* unmanaged<int, uint*, void>)Proc("glCreateMemoryObjectsEXT");
        _deleteMemoryObjects = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteMemoryObjectsEXT");
        _memoryObjectParameter = (delegate* unmanaged<uint, uint, int*, void>)Proc("glMemoryObjectParameterivEXT");
        _importMemoryFd = (delegate* unmanaged<uint, ulong, uint, int, void>)Proc("glImportMemoryFdEXT");
        _textureStorageMem2D =
            (delegate* unmanaged<uint, int, uint, int, int, uint, ulong, void>)Proc("glTextureStorageMem2DEXT");
        _bufferStorageMem = (delegate* unmanaged<uint, nint, uint, ulong, void>)Proc("glNamedBufferStorageMemEXT");
        _textureStorageMem3D =
            (delegate* unmanaged<uint, int, uint, int, int, int, uint, ulong, void>)Proc("glTextureStorageMem3DEXT");
        _genSemaphores = (delegate* unmanaged<int, uint*, void>)Proc("glGenSemaphoresEXT");
        _deleteSemaphores = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteSemaphoresEXT");
        _importSemaphoreFd = (delegate* unmanaged<uint, uint, int, void>)Proc("glImportSemaphoreFdEXT");
        _waitSemaphore = (delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void>)Proc("glWaitSemaphoreEXT");
        _signalSemaphore =
            (delegate* unmanaged<uint, uint, uint*, uint, uint*, uint*, void>)Proc("glSignalSemaphoreEXT");
        _getUnsignedBytei = (delegate* unmanaged<uint, uint, byte*, void>)Proc("glGetUnsignedBytei_vEXT");
        return Assert(Wanted.Length == 4) && _createMemoryObjects != null && _deleteMemoryObjects != null &&
               _memoryObjectParameter != null && _importMemoryFd != null && _textureStorageMem2D != null &&
               _genSemaphores != null && _deleteSemaphores != null && _importSemaphoreFd != null &&
               _waitSemaphore != null && _signalSemaphore != null && _getUnsignedBytei != null &&
               _bufferStorageMem != null && _textureStorageMem3D != null;
    }

    // The GPU the context runs on, as Vulkan's VkPhysicalDeviceIDProperties names it
    public static bool Uuid(Span<byte> uuid)
    {
        if (!Assert(uuid.Length == VulkanDevice.UuidBytes) || _getUnsignedBytei == null) return false;
        fixed (byte* bytes = uuid) _getUnsignedBytei(DeviceUuid, 0, bytes);
        return Assert(uuid.Length == 16) && uuid.IndexOfAnyExcept((byte)0) >= 0;
    }

    // GL owns the descriptor from here
    public static (int Texture, uint Memory) Texture(int fd, ulong size, (int Width, int Height, int Levels, int Layers) extent,
        int format)
    {
        var (width, height, levels, layers) = extent;
        if (!Assert(fd >= 0) || !Assert(size > 0 && width > 0 && height > 0 && levels > 0)) return (0, 0);
        var memory = Imported(fd, size, true);
        GL.CreateTextures(layers > 1 ? TextureTarget.Texture2DArray : TextureTarget.Texture2D, 1, out int texture);
        if (layers > 1) _textureStorageMem3D((uint)texture, levels, (uint)format, width, height, layers, memory, 0);
        else _textureStorageMem2D((uint)texture, levels, (uint)format, width, height, memory, 0);
        return GL.GetError() == OpenTK.Graphics.OpenGL.ErrorCode.NoError ? (texture, memory) : (0, memory);
    }

    // An existing texture's storage replaced by Vulkan's exported memory: the name stays (every framebuffer and every holder of
    // it keeps it), the contents are gone; a mutable texture (glTexImage) takes it, an immutable one (glTexStorage) refuses.
    public static uint Adopt(int texture, int fd, ulong size, (int Width, int Height, int Levels, int Layers) extent,
        int format)
    {
        var (width, height, levels, layers) = extent;
        if (!Assert(texture > 0 && fd >= 0) || !Assert(size > 0 && width > 0 && height > 0 && levels > 0)) return 0;
        var memory = Imported(fd, size, true);
        if (layers > 1) _textureStorageMem3D((uint)texture, levels, (uint)format, width, height, layers, memory, 0);
        else _textureStorageMem2D((uint)texture, levels, (uint)format, width, height, memory, 0);
        if (GL.GetError() == OpenTK.Graphics.OpenGL.ErrorCode.NoError) return memory;
        _deleteMemoryObjects(1, &memory);
        return 0;
    }

    // Many GL buffers can take storage in it (not a dedicated allocation); GL owns the descriptor from here
    public static uint Memory(int fd, ulong size)
    {
        if (!Assert(fd >= 0) || !Assert(size > 0)) return 0;
        var memory = Imported(fd, size, false);
        return GL.GetError() == OpenTK.Graphics.OpenGL.ErrorCode.NoError ? memory : 0;
    }

    public static int Buffer(uint memory, ulong offset, ulong size)
    {
        if (!Assert(memory != 0) || !Assert(size is > 0 and <= int.MaxValue)) return 0;
        Drain();
        GL.CreateBuffers(1, out int buffer);
        _bufferStorageMem((uint)buffer, (nint)size, memory, offset);
        if (GL.GetError() == OpenTK.Graphics.OpenGL.ErrorCode.NoError) return buffer;
        GL.DeleteBuffer(buffer);
        return 0;
    }

    // GL_DYNAMIC_STORAGE as the extension makes it (glBufferSubData works); GL owns the descriptor from here
    public static (int Buffer, uint Memory) Buffer(int fd, ulong memorySize, ulong size)
    {
        if (!Assert(fd >= 0) || !Assert(size is > 0 and <= int.MaxValue && memorySize >= size)) return (0, 0);
        var memory = Imported(fd, memorySize, true);
        GL.CreateBuffers(1, out int buffer);
        _bufferStorageMem((uint)buffer, (nint)size, memory, 0);
        return GL.GetError() == OpenTK.Graphics.OpenGL.ErrorCode.NoError ? (buffer, memory) : (0, memory);
    }

    // dedicated: Vulkan made it a dedicated allocation
    private static uint Imported(int fd, ulong size, bool dedicated)
    {
        uint memory;
        var one = 1;
        Drain();
        _createMemoryObjects(1, &memory);
        if (dedicated) _memoryObjectParameter(memory, DedicatedMemory, &one);
        _importMemoryFd(memory, size, HandleOpaqueFd, fd);
        _ = Assert(fd >= 0) && Assert(size > 0);
        return memory;
    }

    // An error someone else left pending would read as the import's own
    private static void Drain()
    {
        var pending = 0;
        for (var i = 0; i < 8 && GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError; i++) pending++;
        _ = Assert(pending < 8);
    }

    public static uint Semaphore(int fd)
    {
        if (!Assert(fd >= 0) || !Assert(_genSemaphores != null)) return 0;
        uint semaphore;
        _genSemaphores(1, &semaphore);
        _importSemaphoreFd(semaphore, HandleOpaqueFd, fd);
        return semaphore;
    }

    public static void Wait(uint semaphore, int texture, uint layout)
    {
        if (Assert(texture > 0) && Assert(semaphore != 0)) Wait(semaphore, [], [(uint)texture], [layout]);
    }

    // GL's commands after this wait for the semaphore; the buffers and textures come from Vulkan, each texture in its layout
    public static void Wait(uint semaphore, ReadOnlySpan<uint> buffers, ReadOnlySpan<uint> textures,
        ReadOnlySpan<uint> layouts)
    {
        if (!Assert(semaphore != 0) || !Assert(textures.Length == layouts.Length)) return;
        fixed (uint* b = buffers, t = textures, l = layouts)
            _waitSemaphore(semaphore, (uint)buffers.Length, b, (uint)textures.Length, t, l);
    }

    // Signals once GL's commands before this are done, handing the buffers and textures (in the layouts given) to Vulkan
    public static void Signal(uint semaphore, ReadOnlySpan<uint> buffers, ReadOnlySpan<uint> textures,
        ReadOnlySpan<uint> layouts)
    {
        if (!Assert(semaphore != 0) || !Assert(textures.Length == layouts.Length)) return;
        fixed (uint* b = buffers, t = textures, l = layouts)
            _signalSemaphore(semaphore, (uint)buffers.Length, b, (uint)textures.Length, t, l);
    }

    public static void Delete(uint memory, uint semaphore)
    {
        if (!Assert(_deleteMemoryObjects != null || (memory == 0 && semaphore == 0)) ||
            !Assert(_deleteSemaphores != null || semaphore == 0)) return;
        if (memory != 0) _deleteMemoryObjects(1, &memory);
        if (semaphore != 0) _deleteSemaphores(1, &semaphore);
    }

    private static IntPtr Proc(string name) =>
        Assert(name.StartsWith("gl", StringComparison.Ordinal)) &&
        Assert(name.EndsWith("EXT", StringComparison.Ordinal))
            ? GLFW.GetProcAddress(name)
            : IntPtr.Zero;
}
