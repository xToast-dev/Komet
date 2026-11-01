using System.Runtime.InteropServices;
using unsafe BufferDataFn = delegate* unmanaged<uint, nint, nint, uint, void>;
using unsafe BufferSubDataFn = delegate* unmanaged<uint, nint, nint, nint, void>;
using unsafe NamedBufferDataFn = delegate* unmanaged<uint, nint, nint, uint, void>;
using unsafe NamedBufferSubDataFn = delegate* unmanaged<uint, nint, nint, nint, void>;
using unsafe MapBufferRangeFn = delegate* unmanaged<uint, nint, nint, uint, nint>;
using unsafe MapNamedBufferRangeFn = delegate* unmanaged<uint, nint, nint, uint, nint>;
using unsafe MapBufferFn = delegate* unmanaged<uint, uint, nint>;
using unsafe CopyBufferSubDataFn = delegate* unmanaged<uint, uint, nint, nint, nint, void>;
using unsafe CopyNamedBufferSubDataFn = delegate* unmanaged<uint, uint, nint, nint, nint, void>;
using unsafe TexSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TexImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TextureSubImage2DFn = delegate* unmanaged<uint, int, int, int, int, int, uint, uint, nint, void>;
using unsafe GenerateMipmapFn = delegate* unmanaged<uint, void>;
using unsafe GenerateTextureMipmapFn = delegate* unmanaged<uint, void>;
using unsafe ClearNamedBufferSubDataFn = delegate* unmanaged<uint, uint, nint, nint, uint, uint, nint, void>;
using unsafe TexImage3DFn = delegate* unmanaged<uint, int, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TexSubImage3DFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, nint, void>;
using unsafe TexStorage2DFn = delegate* unmanaged<uint, int, uint, int, int, void>;
using unsafe TexStorage3DFn = delegate* unmanaged<uint, int, uint, int, int, int, void>;
using unsafe GetTextureImageFn = delegate* unmanaged<uint, int, uint, uint, int, nint, void>;
using unsafe GetTextureSubImageFn = delegate* unmanaged<uint, int, int, int, int, int, int, int, uint, uint, int, nint,
    void>;

namespace Komet.Vulkan;

// Buffer and texture uploads, maps and mipmaps: Writes hears each before the driver runs it (Komet's own work, Quietly,
// only as unseen), lean keeps the large ones out of GL's memory, and Touching hears each first. The entries follow
// Drawings' in the tap's list (TouchAt + 31 on).
internal static unsafe partial class GlTap
{
    // Why a buffer Komet specified itself is unseen: its size is not what the listener heard either
    public const string OwnData = "Komet's own data";

    // The upload entries other files call OpenGL through (Tap checks their names)
    private const int BufferDataAt = TouchAt + 31, NamedBufferDataAt = TouchAt + 33, TexImage2DAt = TouchAt + 41;
    private const uint MapWrite = 2, PersistentStorage = 0x40;

    private static Entry[] Uploads() =>
    [
        new("glBufferData", (IntPtr)(BufferDataFn)(&BufferData)),
        new("glBufferSubData", (IntPtr)(BufferSubDataFn)(&BufferSubData)),
        new("glNamedBufferData", (IntPtr)(NamedBufferDataFn)(&NamedBufferData)),
        new("glNamedBufferSubData", (IntPtr)(NamedBufferSubDataFn)(&NamedBufferSubData)),
        new("glMapBufferRange", (IntPtr)(MapBufferRangeFn)(&MapBufferRange)),
        new("glMapNamedBufferRange", (IntPtr)(MapNamedBufferRangeFn)(&MapNamedBufferRange)),
        new("glMapBuffer", (IntPtr)(MapBufferFn)(&MapBuffer)),
        new("glCopyBufferSubData", (IntPtr)(CopyBufferSubDataFn)(&CopyBufferSubData)),
        new("glCopyNamedBufferSubData", (IntPtr)(CopyNamedBufferSubDataFn)(&CopyNamedBufferSubData)),
        new("glTexSubImage2D", (IntPtr)(TexSubImage2DFn)(&TexSubImage2D)),
        new("glTexImage2D", (IntPtr)(TexImage2DFn)(&TexImage2D)),
        new("glTextureSubImage2D", (IntPtr)(TextureSubImage2DFn)(&TextureSubImage2D)),
        new("glGenerateMipmap", (IntPtr)(GenerateMipmapFn)(&GenerateMipmap)),
        new("glGenerateTextureMipmap", (IntPtr)(GenerateTextureMipmapFn)(&GenerateTextureMipmap)),
        new("glClearNamedBufferSubData", (IntPtr)(ClearNamedBufferSubDataFn)(&ClearNamedBufferSubData)),
        new("glTexImage3D", (IntPtr)(TexImage3DFn)(&TexImage3D)),
        new("glTexSubImage3D", (IntPtr)(TexSubImage3DFn)(&TexSubImage3D)),
        new("glTexStorage2D", (IntPtr)(TexStorage2DFn)(&TexStorage2D)),
        new("glTexStorage3D", (IntPtr)(TexStorage3DFn)(&TexStorage3D)),
        new("glBufferStorage", (IntPtr)(BufferDataFn)(&BufferStorage)),
        new("glGetTextureImage", (IntPtr)(GetTextureImageFn)(&GetTextureImage)),
        new("glGetTextureSubImage", (IntPtr)(GetTextureSubImageFn)(&GetTextureSubImage))
    ];

    // A buffer write the listener replays; Komet's own (Quietly) is only heard of as unseen
    private static void DataWritten(uint buffer, nint size, nint data, bool persistent)
    {
        if (!Assert(size >= 0)) return;
        Sized(buffer, size);
        if (_quiet > 0) Unseen(buffer, OwnData);
        else if (Writes is { } writes) Heard(static s => s.writes.BufferData(s.buffer, s.size, s.data, s.persistent), (writes, buffer, size, data, persistent));
    }

    private static void SubDataWritten(uint buffer, nint offset, nint size, nint data)
    {
        if (!Assert(offset >= 0) || !Assert(size >= 0)) return;
        if (_quiet > 0) Unseen(buffer, "Komet's own sub-data");
        else if (Writes is { } writes) Heard(static s => s.writes.BufferSubData(s.buffer, s.offset, s.size, s.data), (writes, buffer, offset, size, data));
    }

    private static void CopyWritten(uint from, uint to, nint fromOffset, nint toOffset, nint size)
    {
        if (!Assert(size >= 0) || !Assert(fromOffset >= 0 && toOffset >= 0)) return;
        if (_quiet > 0) Unseen(to, "Komet's own copy");
        else if (Writes is { } writes)
            Heard(static s => s.writes.BufferCopy(s.from, s.to, s.fromOffset, s.toOffset, s.size), (writes, from, to, fromOffset, toOffset, size));
    }

    // A texture call the record follows, else one more it does not
    private static bool Follows(bool tracked)
    {
        if (!tracked) UntrackedCalls++;
        return tracked;
    }

    [UnmanagedCallersOnly]
    private static void BufferData(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, Bound(a));
        var small = BufferLean(Bound(a), b, 0, d) == Leaning.Small; // first: the mirrors keep all of a lean one
        DataWritten(Bound(a), b, c, false);
        if (Assert(Original[BufferDataAt] != IntPtr.Zero))
            ((BufferDataFn)Original[BufferDataAt])(a, small ? LeanBufferKept : b, small ? 0 : c, d);
    }

    [UnmanagedCallersOnly]
    private static void BufferSubData(uint a, nint b, nint c, nint d)
    {
        Touched(Touch.Upload, Bound(a));
        SubDataWritten(Bound(a), b, c, d);
        if (!BufferHeld(Bound(a)) && Assert(Original[TouchAt + 32] != IntPtr.Zero))
            ((BufferSubDataFn)Original[TouchAt + 32])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void NamedBufferData(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, a);
        var small = BufferLean(a, b, 0, d) == Leaning.Small;
        DataWritten(a, b, c, false);
        if (Assert(Original[NamedBufferDataAt] != IntPtr.Zero))
            ((NamedBufferDataFn)Original[NamedBufferDataAt])(a, small ? LeanBufferKept : b, small ? 0 : c, d);
    }

    [UnmanagedCallersOnly]
    private static void NamedBufferSubData(uint a, nint b, nint c, nint d)
    {
        Touched(Touch.Upload, a);
        SubDataWritten(a, b, c, d);
        if (!BufferHeld(a) && Assert(Original[TouchAt + 34] != IntPtr.Zero))
            ((NamedBufferSubDataFn)Original[TouchAt + 34])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static nint MapBufferRange(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, Bound(a));
        if ((d & MapWrite) != 0) Unseen(Bound(a), "a mapping");
        Unleaned(Bound(a), a);
        var gl = Assert(Original[TouchAt + 35] != IntPtr.Zero)
            ? ((MapBufferRangeFn)Original[TouchAt + 35])(a, b, c, d)
            : 0;
        return Mapped(Bound(a), b, c, d, gl);
    }

    [UnmanagedCallersOnly]
    private static nint MapNamedBufferRange(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, a);
        if ((d & MapWrite) != 0) Unseen(a, "a mapping");
        Unleaned(a, 0);
        var gl = Assert(Original[TouchAt + 36] != IntPtr.Zero)
            ? ((MapNamedBufferRangeFn)Original[TouchAt + 36])(a, b, c, d)
            : 0;
        return Mapped(a, b, c, d, gl);
    }

    [UnmanagedCallersOnly]
    private static nint MapBuffer(uint a, uint b)
    {
        Touched(Touch.Upload, Bound(a));
        if (b != 0x88B8) Unseen(Bound(a), "a mapping"); // anything but GL_READ_ONLY writes
        Unleaned(Bound(a), a);
        return Assert(Original[TouchAt + 37] != IntPtr.Zero)
            ? ((MapBufferFn)Original[TouchAt + 37])(a, b)
            : 0;
    }

    [UnmanagedCallersOnly]
    private static void CopyBufferSubData(uint a, uint b, nint c, nint d, nint e)
    {
        Touched(Touch.Upload, Bound(b));
        CopyWritten(Bound(a), Bound(b), c, d, e);
        if (!BufferHeld(Bound(a)) && !BufferHeld(Bound(b)) && Assert(Original[TouchAt + 38] != IntPtr.Zero))
            ((CopyBufferSubDataFn)Original[TouchAt + 38])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void CopyNamedBufferSubData(uint a, uint b, nint c, nint d, nint e)
    {
        Touched(Touch.Upload, b);
        CopyWritten(a, b, c, d, e);
        if (!BufferHeld(a) && !BufferHeld(b) && Assert(Original[TouchAt + 39] != IntPtr.Zero))
            ((CopyNamedBufferSubDataFn)Original[TouchAt + 39])(a, b, c, d, e);
    }

    [UnmanagedCallersOnly]
    private static void TexSubImage2D(uint a, int b, int c, int d, int e, int f, uint g, uint h, nint i)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var name = FollowedName(a);
        var taken = Follows(Specifiable(a)) && SubImaged(name, a, b, (c, d, Face(a), e, f, 1), (g, h), i);
        if (Skipped(name, a, taken, (b, c, d, e, f))) return;
        if (!TextureHeld(name) && Assert(Original[TouchAt + 40] != IntPtr.Zero))
            ((TexSubImage2DFn)Original[TouchAt + 40])(a, b, c, d, e, f, g, h, i);
    }

    [UnmanagedCallersOnly]
    private static void TexImage2D(uint a, int b, int c, int d, int e, int f, uint g, uint h, nint i)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var lean = Specifiable(a) ? TextureLean(BoundName(a), b, (d, e, 1)) : Leaning.Given;
        if (lean != Leaning.Held && Assert(Original[TexImage2DAt] != IntPtr.Zero))
            ((TexImage2DFn)Original[TexImage2DAt])(a, b, c, lean == Leaning.Small ? 1 : d, lean == Leaning.Small ? 1 : e, f, g, h,
                lean == Leaning.Small ? 0 : i);
        if (Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (s.b, 1), (s.c, s.d, s.e, 1), s.h),
                (BoundName(a), a, b, c, d, e, h));
            Imaged(BoundName(a), a, (b, 1), (c, d, e, 1), (g, h), i);
        }
    }

    [UnmanagedCallersOnly]
    private static void TextureSubImage2D(uint a, int b, int c, int d, int e, int f, uint g, uint h, nint i)
    {
        Touched(Touch.TextureUpload, a);
        if (Follows(Tracked(TargetOf(a)))) _ = SubImaged(a, TargetOf(a), b, (c, d, 0, e, f, 1), (g, h), i);
        if (!TextureHeld(a) && Assert(Original[TouchAt + 42] != IntPtr.Zero))
            ((TextureSubImage2DFn)Original[TouchAt + 42])(a, b, c, d, e, f, g, h, i);
    }

    [UnmanagedCallersOnly]
    private static void GenerateMipmap(uint a)
    {
        Touched(Touch.Mipmap, BoundName(a));
        if (Assert(Original[TouchAt + 43] != IntPtr.Zero))
            ((GenerateMipmapFn)Original[TouchAt + 43])(a);
        if (Follows(Specifiable(a)))
        {
            Heard(MipmapsMade, FollowedName(a));
            Remipmapped(FollowedName(a));
        }
    }

    [UnmanagedCallersOnly]
    private static void GenerateTextureMipmap(uint a)
    {
        Touched(Touch.Mipmap, a);
        if (Assert(Original[TouchAt + 44] != IntPtr.Zero))
            ((GenerateTextureMipmapFn)Original[TouchAt + 44])(a);
        if (!Tapped) return; // a listener untapped meanwhile
        Heard(MipmapsMade, a);
        if (Follows(Tracked(TargetOf(a)))) Remipmapped(a);
    }

    [UnmanagedCallersOnly]
    private static void ClearNamedBufferSubData(uint a, uint b, nint c, nint d, uint e, uint f, nint g)
    {
        Touched(Touch.Upload, a);
        Unseen(a, "a clear");
        if (!BufferHeld(a) && Assert(Original[TouchAt + 45] != IntPtr.Zero))
            ((ClearNamedBufferSubDataFn)Original[TouchAt + 45])(a, b, c, d, e, f, g);
    }

    [UnmanagedCallersOnly]
    private static void TexImage3D(uint a, int b, int c, int d, int e, int f, int g, uint h, uint i, nint j)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var lean = Specifiable(a) ? TextureLean(BoundName(a), b, (d, e, f)) : Leaning.Given;
        var small = lean == Leaning.Small;
        if (lean != Leaning.Held && Assert(Original[TouchAt + 46] != IntPtr.Zero))
            ((TexImage3DFn)Original[TouchAt + 46])(a, b, c, small ? 1 : d, small ? 1 : e, f, g, h, i, small ? 0 : j);
        if (Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (s.b, 1), (s.c, s.d, s.e, s.f), s.i),
                (BoundName(a), a, b, c, d, e, f, i));
            Imaged(BoundName(a), a, (b, 1), (c, d, e, f), (h, i), j);
        }
    }

    [UnmanagedCallersOnly]
    private static void TexSubImage3D(uint a, int b, int c, int d, int e, int f, int g, int h, uint i, uint j, nint k)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        var name = FollowedName(a);
        if (Follows(Specifiable(a))) _ = SubImaged(name, a, b, (c, d, e, f, g, h), (i, j), k);
        if (!TextureHeld(name) && Assert(Original[TouchAt + 47] != IntPtr.Zero))
            ((TexSubImage3DFn)Original[TouchAt + 47])(a, b, c, d, e, f, g, h, i, j, k);
    }

    [UnmanagedCallersOnly]
    private static void TexStorage2D(uint a, int b, uint c, int d, int e)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        LeanGone(BoundName(a), true); // immutable storage is never lean: OpenGL could not be given its size back
        if (Assert(Original[TouchAt + 48] != IntPtr.Zero))
            ((TexStorage2DFn)Original[TouchAt + 48])(a, b, c, d, e);
        if (Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (0, s.b), ((int)s.c, s.d, s.e, 1), 0),
                (BoundName(a), a, b, c, d, e));
            Imaged(BoundName(a), a, (0, Math.Max(b, 1)), ((int)c, d, e, 1), default, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static void TexStorage3D(uint a, int b, uint c, int d, int e, int f)
    {
        Touched(Touch.TextureUpload, BoundName(a));
        LeanGone(BoundName(a), true);
        if (Assert(Original[TouchAt + 49] != IntPtr.Zero))
            ((TexStorage3DFn)Original[TouchAt + 49])(a, b, c, d, e, f);
        if (Follows(Specifiable(a)))
        {
            Heard(static s => Specified(s.Item1, s.a, (0, s.b), ((int)s.c, s.d, s.e, s.f), 0),
                (BoundName(a), a, b, c, d, e, f));
            Imaged(BoundName(a), a, (0, Math.Max(b, 1)), ((int)c, d, e, f), default, 0);
        }
    }

    [UnmanagedCallersOnly]
    private static void BufferStorage(uint a, nint b, nint c, uint d)
    {
        Touched(Touch.Upload, Bound(a));
        var small = BufferLean(Bound(a), b, d, UsageOf(d)) == Leaning.Small;
        DataWritten(Bound(a), b, c, (d & PersistentStorage) != 0);
        if (small && Assert(Original[BufferDataAt] != IntPtr.Zero))
            ((BufferDataFn)Original[BufferDataAt])(a, LeanBufferKept, 0, UsageOf(d)); // mutable: its size can come back
        else if (!small && Assert(Original[TouchAt + 50] != IntPtr.Zero))
            ((BufferDataFn)Original[TouchAt + 50])(a, b, c, d);
    }

    [UnmanagedCallersOnly]
    private static void GetTextureImage(uint a, int b, uint c, uint d, int e, nint f)
    {
        Touched(Touch.Read, a);
        if (Assert(Original[TouchAt + 51] != IntPtr.Zero))
            ((GetTextureImageFn)Original[TouchAt + 51])(a, b, c, d, e, f);
    }

    [UnmanagedCallersOnly]
    private static void GetTextureSubImage(uint a, int b, int c, int d, int e, int f, int g, int h, uint i, uint j, int k,
        nint l)
    {
        Touched(Touch.Read, a);
        if (Assert(Original[TouchAt + 52] != IntPtr.Zero))
            ((GetTextureSubImageFn)Original[TouchAt + 52])(a, b, c, d, e, f, g, h, i, j, k, l);
    }
}
