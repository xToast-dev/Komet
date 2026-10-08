namespace Komet.Vulkan;

// Images passing between OpenGL and Vulkan: OpenGL signals a SharedSemaphore naming each texture in a layout, Vulkan acquires
// them from VK_QUEUE_FAMILY_EXTERNAL in that layout; at the end Vulkan releases them to it in the layout OpenGL waits for them in
// (To, when the image moved on the way).
internal static unsafe class Handoff
{
    private const int MaxImages = VulkanFrame.MaxImages;
    private const uint ReadWrite = Vk.AccessMemoryRead | Vk.AccessMemoryWrite;

    // Private: Vulkan's own image (SharedImage.Exported false), which only moves back to its resting layout
    public readonly record struct Image(ulong Handle, int Layout, uint Aspect, int Levels)
    {
        public int To { get; init; } = -1; // the layout it leaves in, when not Layout
        public int Layers { get; init; } = 1;
        public bool Private { get; init; }
    }

    // Records taking the images over from OpenGL, before any use
    public static void Acquire(IntPtr commands, uint family, ReadOnlySpan<Image> images)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(images.Length <= MaxImages)) return;
        var barriers = stackalloc Vk.ImageBarrier[Math.Max(images.Length, 1)]; // as many as there are: it is zeroed
        var count = 0u;
        for (var i = 0; i < Math.Min(images.Length, MaxImages); i++)
            if (!images[i].Private)
                barriers[count++] = Barrier(images[i], (Vk.QueueFamilyExternal, family), (0, ReadWrite));
        if (count > 0) VkApi.CmdPipelineBarrier(commands, Vk.StageTop, Vk.StageAll, 0, 0, null, 0, null, count, barriers);
    }

    // Records handing the images back to OpenGL, after every use; a private one that moved goes back to its resting layout
    // before anything after
    public static void Release(IntPtr commands, uint family, ReadOnlySpan<Image> images)
    {
        if (!Assert(commands != IntPtr.Zero) || !Assert(images.Length <= MaxImages)) return;
        var barriers = stackalloc Vk.ImageBarrier[Math.Max(images.Length, 1)];
        var (count, moved) = (0u, images.Length);
        for (var i = 0; i < Math.Min(images.Length, MaxImages); i++)
        {
            var image = images[i];
            if (!image.Private)
                barriers[count++] = Barrier(image, (family, Vk.QueueFamilyExternal), (Vk.AccessMemoryWrite, 0));
            else if (image.To >= 0 && image.To != image.Layout)
                barriers[--moved] = Barrier(image, (Vk.QueueFamilyIgnored, Vk.QueueFamilyIgnored), (ReadWrite, ReadWrite));
        }

        if (count > 0) VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageBottom, 0, 0, null, 0, null, count, barriers);
        if (moved < images.Length)
            VkApi.CmdPipelineBarrier(commands, Vk.StageAll, Vk.StageAll, 0, 0, null, 0, null, (uint)(images.Length - moved),
                barriers + moved);
    }

    private static Vk.ImageBarrier Barrier(Image image, (uint From, uint To) families,
        (uint Source, uint Target) access)
    {
        if (!Assert(image.Handle != 0) || !Assert(image.Levels > 0)) return default;
        var leaves = image.To >= 0 ? image.To : image.Layout;
        return new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = access.Source, DstAccess = access.Target,
            OldLayout = image.Layout, NewLayout = leaves, SrcFamily = families.From, DstFamily = families.To,
            Image = image.Handle,
            Range = new Vk.ColorRange
            {
                Aspect = image.Aspect, Levels = (uint)image.Levels, Layers = (uint)Math.Max(image.Layers, 1)
            }
        };
    }
}
