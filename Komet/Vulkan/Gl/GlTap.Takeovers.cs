using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

// A blit goes to Blitting first, which may take it over (OpenGL skips it then). Each framebuffer object's read buffer is
// followed as glReadBuffer sets it, asked from OpenGL once for one set before tapping (while bound for reading). A 2D
// texture's mipmaps made may be made in its copy too (Writes.TextureMipmapped), else the texture changed.
internal static unsafe partial class GlTap
{
    public const int ReadNone = -1, ReadUnknown = -2;
    private const int MaxReads = 256;

    private static readonly Dictionary<uint, int> Reads = [];

    // Read: the read framebuffer and the attachment its read buffer names (or ReadNone, ReadUnknown); From and To as OpenGL
    // takes them, X0 Y0 X1 Y1 either way round
    public readonly record struct BlitCall(int Read, int ReadAttachment, int Draw, (int X0, int Y0, int X1, int Y1) From,
        (int X0, int Y0, int X1, int Y1) To, uint Mask, uint Filter);

    // Asked before every blit between framebuffer objects reaches the driver: true when Vulkan blitted
    public static System.Func<BlitCall, bool>? Blitting { get; set; }

    private static bool Blitted(uint read, uint draw, ReadOnlySpan<int> corners, uint mask, uint filter) =>
        _quiet == 0 && Blitting is { } blitting && read > 0 && draw > 0 && Assert(corners.Length == 8) &&
        Assert(_table is not null) && blitting(new BlitCall((int)read, ReadBufferOf(read), (int)draw,
            (corners[0], corners[1], corners[2], corners[3]), (corners[4], corners[5], corners[6], corners[7]), mask, filter));

    public static int ReadBufferOf(uint framebuffer)
    {
        if (framebuffer == 0 || !Assert(Reads.Count <= MaxReads)) return ReadUnknown;
        if (Reads.TryGetValue(framebuffer, out var known)) return known;
        if (framebuffer != _readFramebuffer || Reads.Count >= MaxReads) return ReadUnknown;
        return Reads[framebuffer] = ReadOf((uint)GL.GetInteger(GetPName.ReadBuffer));
    }

    private static int ReadOf(uint mode) => mode switch
    {
        >= GlColor0 and < GlColor0 + 16 => (int)(mode - GlColor0),
        0 => ReadNone,
        _ => Assert(mode != uint.MaxValue) ? ReadUnknown : ReadNone
    };

    private static void Mipmapped(uint texture, bool flat)
    {
        if (Writes is not { } writes || !Assert(texture != uint.MaxValue)) return;
        if (!flat || texture == 0 || !writes.TextureMipmapped(texture)) writes.TextureChanged(texture);
    }

    // A texture parameter set: texture 0's too, whose sampling then is no longer the default
    private static void Tuned(uint texture, uint name, float value)
    {
        if (texture == 0 && Assert(name > 0)) DefaultTextureTouched = true;
        else Writes?.TextureTuned(texture, name, value);
    }

    // Komet's own framebuffers (GlTexture.Copier's, quiet) are no blit's source: their read buffers are not followed
    private static void ReadSet(uint framebuffer, uint mode)
    {
        if (framebuffer == 0 || _quiet > 0) return;
        System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(Reads, framebuffer, out _) = ReadOf(mode);
        if (Reads.Count > MaxReads) Reads.Clear();
    }
}
