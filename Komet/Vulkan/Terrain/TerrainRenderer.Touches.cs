namespace Komet.Vulkan;

// An OpenGL call about to run, as GlTap's Touch group tells it while the scene is on: what Vulkan drew into the copies of what
// it reads goes back first, the open segment closes where the call must follow Vulkan's work, and the copies of what it draws
// into are stale after it.
internal sealed partial class TerrainRenderer
{
    private const int MaxReads = 256;

    private readonly List<uint> _reads = [];

    public void Touched(GlTap.Touch kind, uint id)
    {
        if (!Assert(kind <= GlTap.Touch.Mipmap)) return;
        if (kind == GlTap.Touch.Draw) WrittenRead();
        else if (kind == GlTap.Touch.Blit || (kind == GlTap.Touch.Read && id == 0)) ReadBack(GlTap.ReadFramebuffer);
        else if (kind is not (GlTap.Touch.Upload or GlTap.Touch.Clear or GlTap.Touch.ClearNamed) && id > 0)
            WriteBack((int)id);
        Frame.Beside(kind, id);
        var fbo = kind switch
        {
            GlTap.Touch.Draw or GlTap.Touch.Clear => GlTap.DrawFramebuffer,
            GlTap.Touch.ClearNamed => (int)id,
            GlTap.Touch.Blit => id > 0 ? (int)id : GlTap.DrawFramebuffer,
            _ => 0
        };
        if (fbo > 0) Rendered(fbo);
        Frame.GlTouched(kind, id); // last: Beside may have closed a segment OpenGL must now wait for
    }

    private void WrittenRead()
    {
        if (!Copies.AnyWritten) return;
        GlTap.Sampled(_reads);
        foreach (var texture in _reads.Bounded(MaxReads)) WriteBack((int)texture); // quiet: nothing taps into _reads meanwhile
        _ = Assert(_reads.Count <= MaxReads);
    }

    // OpenGL reads the framebuffer (a blit, glReadPixels): what Vulkan drew into its textures' copies goes back first
    public void ReadBack(int fbo)
    {
        if (fbo <= 0 || Ours(fbo) || !Copies.AnyWritten) return;
        var (colors, depth) = Attachments(fbo);
        foreach (var texture in colors.Bounded(GlFramebuffer.Colors))
            if (texture > 0)
                WriteBack(texture);
        if (depth > 0) WriteBack(depth);
    }
}
