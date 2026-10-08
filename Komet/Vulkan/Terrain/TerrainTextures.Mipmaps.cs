namespace Komet.Vulkan;

// glGenerateMipmap in a texture's copy: each level blitted linearly from the one above, whole into whole, as Mesa makes them
// (a linear fetch between each 2x2 block, on the same sampler hardware: the same texels, MipmapsGpuTests). The image comes
// and goes wholly in TRANSFER_DST; levels: the base and those made below it.
internal static unsafe class Mipmaps
{
    private const int MaxLevels = 16;

    public static void Record(IntPtr commands, SharedImage image, int levels)
    {
        if (!Assert(commands != IntPtr.Zero) || !NotNull(image) || !Assert(levels is > 1 and <= MaxLevels)) return;
        var handle = image.Image;
        for (var level = 1; level < Math.Min(levels, MaxLevels); level++)
        {
            Moved(commands, handle, (level - 1, 1), (Vk.LayoutTransferDst, Vk.LayoutTransferSrc));
            var layer = new Vk.Layers { Aspect = Vk.AspectColor, Level = (uint)(level - 1), Count = 1 };
            var blit = new Vk.ImageBlit
            {
                Source = layer, SourceX1 = Side(image.Width, level - 1), SourceY1 = Side(image.Height, level - 1),
                SourceZ1 = 1, Target = layer with { Level = (uint)level }, TargetX1 = Side(image.Width, level),
                TargetY1 = Side(image.Height, level), TargetZ1 = 1
            };
            VkApi.CmdBlitImage(commands, handle, Vk.LayoutTransferSrc, handle, Vk.LayoutTransferDst, 1, &blit, Vk.FilterLinear);
        }

        Moved(commands, handle, (0, levels - 1), (Vk.LayoutTransferSrc, Vk.LayoutTransferDst));
    }

    private static int Side(int size, int level) => Assert(size > 0) ? Math.Max(size >> level, 1) : 1;

    private static void Moved(IntPtr commands, ulong image, (int First, int Count) levels, (int From, int To) layouts)
    {
        if (!Assert(image != 0) || !Assert(levels.Count > 0)) return;
        var barrier = new Vk.ImageBarrier
        {
            SType = Vk.ImageMemoryBarrier, SrcAccess = Vk.AccessTransferWrite | Vk.AccessTransferRead,
            DstAccess = Vk.AccessTransferRead | Vk.AccessTransferWrite, OldLayout = layouts.From, NewLayout = layouts.To,
            SrcFamily = Vk.QueueFamilyIgnored, DstFamily = Vk.QueueFamilyIgnored, Image = image,
            Range = new Vk.ColorRange
            {
                Aspect = Vk.AspectColor, BaseLevel = (uint)levels.First, Levels = (uint)levels.Count, Layers = 1
            }
        };
        VkApi.CmdPipelineBarrier(commands, Vk.StageTransfer, Vk.StageTransfer, 0, 0, null, 0, null, 1, &barrier);
    }
}
