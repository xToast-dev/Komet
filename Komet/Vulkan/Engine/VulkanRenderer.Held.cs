namespace Komet.Vulkan;

// Komet's own largest holdings beside the Vulkan memory report: whether the managed heap grows with Komet or with the game. Bytes
// are estimates (entry sizes), kept uploads lie in the frames' host memory, the chunk lookups are the engine's own chunks.
internal static partial class VulkanRenderer
{
    private const int FaceGroupBytes = 280; // a FaceSorting.Sorted with its four arrays and its entry

    internal static string Held()
    {
        var (kept, keptBytes) = _renderer?.KeptUploads ?? (0, 0);
        var (mirrors, mirrorBytes) = _scene?.Mirrors is { } m ? (m.Count, m.ManagedBytes) : (0, 0);
        var tap = GlTap.Held;
        _ = Assert(kept >= 0 && mirrors >= 0) && Assert(tap.Bytes >= 0);
        return $"Komet holds {kept} kept uploads ({keptBytes >> 10} KB host memory), {mirrors} buffer mirrors " +
               $"({mirrorBytes >> 10} KB managed), the tap's {tap.Programs} programs, {tap.Arrays} vertex arrays, " +
               $"{tap.Textures} texture targets, {tap.Framebuffers} framebuffers ({tap.Bytes >> 10} KB), " +
               $"{FaceSorting.Located} face groups ({(long)FaceSorting.Located * FaceGroupBytes >> 10} KB), " +
               $"{ChunkLookup.Count} chunk lookups, {AnimationFrames.Count} animation sets";
    }
}
