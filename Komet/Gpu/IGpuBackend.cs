namespace Komet.Gpu;

// Handles are the backend's own numbers; 0 is none.
internal interface IGpuBackend
{
    bool Supported();

    // Doubles in compute shaders (rows.comp's exact frustum test); OpenGL 4.6 has them
    bool Doubles() => true;

    // A program from one of GpuShaders' files; 0, with the log written, when it does not build
    int Program(string shader, ILogger logger);

    int Buffer();

    // A fresh store with the data, at least minBytes long; the old one may still be read by the GPU and is not waited for
    void Upload<T>(int buffer, ReadOnlySpan<T> data, int minBytes = 0) where T : unmanaged;

    // Into the buffer's store at the byte offset, which keeps the rest (the store has to be large enough)
    void UploadAt<T>(int buffer, int offsetBytes, ReadOnlySpan<T> data) where T : unmanaged;

    void Zero(int buffer, int firstUint, int uints);

    // Host memory of bytes that goes into the buffer at the byte offset like UploadAt, for the caller (and any thread it hands the
    // pointer to) to fill before the next command it records; null when the backend has none (OpenGL takes the data at the call)
    unsafe byte* Staged(int buffer, int offsetBytes, int bytes) => null;

    // Waits for the GPU's writes to the buffer: only for buffers the GPU last wrote frames ago, and on OpenGL only for Readable ones
    void Read(int buffer, int firstUint, Span<uint> into);

    // Storage of at least bytes that Read maps as it is. radeonsi reads a buffer in VRAM or write-combined memory (every other
    // usage) through a copy it appends to the frame and waits for, so such a Read waited for the GPU's whole frame so far.
    void Readable(int buffer, int bytes);

    // uints from one buffer into the same place of another, on the GPU, after a Copy barrier
    void Copy(int from, int to, int firstUint, int uints);

    void Storage(int binding, int buffer);
    void Uniforms(int binding, int buffer);
    void Texture(int unit, int texture);
    void Image(int unit, int texture, int level, bool write);

    // An R32F image with every level down to 1x1
    int Pyramid(int width, int height, out int levels);

    // One of the engine's textures (its depth buffer) as this backend names it for Texture; 0 when it cannot read it
    int Engine(int texture);

    // Groups of the program's local size; whatever the engine had in use is in use again afterwards
    void Dispatch(int program, int x, int y = 1);

    void Barrier(GpuBarrier wait);

    void DeleteProgram(ref int program);
    void DeleteBuffer(ref int buffer);
    void DeleteTexture(ref int texture);
}

internal static class GpuBackends
{
    public static IGpuBackend Current { get; set; } = GlBackend.Instance;

    // Vulkan's, recording into its frame, while it draws the opaque terrain (culled or not); null while OpenGL does. What runs
    // beside the frame's draws (the occlusion measurement) takes it before Current, so OpenGL dispatches nothing then.
    public static IGpuBackend? Frame { get; set; }

    public static IGpuBackend Measuring => Frame ?? Current;
}

// What a barrier orders: in Vulkan a pipeline barrier with these destination accesses, in OpenGL glMemoryBarrier bits
[Flags]
internal enum GpuBarrier
{
    None = 0,
    Storage = 1, // compute writes to buffers, then compute reads them
    Images = 2, // image stores, then image loads
    Textures = 4, // image stores, then texture fetches
    Indirect = 8, // compute writes, then indirect draw commands and counts read them
    Attachments = 16, // draws into the depth buffer, then texture fetches of it
    Copy = 32 // compute writes to buffers, then a buffer copy reads them
}
