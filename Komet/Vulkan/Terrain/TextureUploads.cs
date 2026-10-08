using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Komet.Vulkan;

// Converted as OpenGL does: BGRA swapped, RGB opaque, a signed short normalized with negatives clamped to zero, floats
// into RGBA16F rounded to the nearest even half.
internal static unsafe class TextureUploads
{
    private const uint Rgba = 0x1908, Bgra = 0x80E1, Rgb = 0x1907;
    private const uint UnsignedByte = 0x1401, Short = 0x1402, UnsignedShort = 0x1403, Float = 0x1406, HalfFloat = 0x140B;
    private const uint Rgba8 = Vk.FormatRgba8, Rgba16F = Vk.FormatRgba16F, Rgba16 = Vk.FormatRgba16, Rgba32F = Vk.FormatRgba32F;
    private const int MaxSide = 16384, MaxValues = MaxSide * 4;

    public static (int Source, int Target) Bytes(uint target, (uint Format, uint Type) data) => (target, data) switch
    {
        (Rgba8, (Rgba or Bgra, UnsignedByte)) => (4, 4),
        (Rgba8, (Rgb, UnsignedByte)) => (3, 4),
        (Rgba16, (Rgba, Short or UnsignedShort)) => (8, 8),
        (Rgba32F, (Rgba, Float)) => (16, 16),
        (Rgba16F, (Rgba, Float)) => (16, 8),
        (Rgba16F, (Rgba, HalfFloat)) => (8, 8),
        _ => (0, 0)
    };

    public static void Convert(uint target, (uint Format, uint Type) data, byte* source, (int Width, int Height, int Stride) rows,
        Span<byte> into)
    {
        var (sourceBytes, targetBytes) = Bytes(target, data);
        if (sourceBytes == 0 || source == null || !Assert(rows.Width is > 0 and <= MaxSide) ||
            !Assert(rows.Height is > 0 and <= MaxSide) || !Assert(into.Length >= rows.Width * rows.Height * targetBytes)) return;
        for (var y = 0; y < Math.Min(rows.Height, MaxSide); y++)
        {
            var row = new ReadOnlySpan<byte>(source + (long)y * rows.Stride, rows.Width * sourceBytes);
            var packed = into.Slice(y * rows.Width * targetBytes, rows.Width * targetBytes);
            Row(target, data, row, packed);
        }
    }

    private static void Row(uint target, (uint Format, uint Type) data, ReadOnlySpan<byte> row, Span<byte> into)
    {
        _ = Assert(row.Length > 0) && Assert(into.Length > 0);
        switch (target, data)
        {
            case (Rgba8, (Bgra, UnsignedByte)):
                Swapped(MemoryMarshal.Cast<byte, uint>(row), MemoryMarshal.Cast<byte, uint>(into));
                break;
            case (Rgba8, (Rgb, UnsignedByte)):
                Opaque(row, into);
                break;
            case (Rgba16, (Rgba, Short)):
                Normalized(MemoryMarshal.Cast<byte, short>(row), MemoryMarshal.Cast<byte, ushort>(into));
                break;
            case (Rgba16F, (Rgba, Float)):
                Halves(MemoryMarshal.Cast<byte, float>(row), MemoryMarshal.Cast<byte, Half>(into));
                break;
            default:
                row.CopyTo(into);
                break;
        }
    }

    internal static void Swapped(ReadOnlySpan<uint> bgra, Span<uint> rgba)
    {
        var n = Math.Min(bgra.Length, rgba.Length);
        var vectors = Vector256.IsHardwareAccelerated ? n / Vector256<uint>.Count : 0;
        if (!Assert(n >= 0) || !Assert(vectors * Vector256<uint>.Count <= n)) return;
        var (keep, low) = (Vector256.Create(0xFF00FF00u), Vector256.Create(0xFFu));
        for (var v = 0; v < Math.Min(vectors, MaxValues); v++)
        {
            var at = v * Vector256<uint>.Count;
            var p = Vector256.Create(bgra[at..]);
            ((p & keep) | ((p >> 16) & low) | ((p & low) << 16)).CopyTo(rgba[at..]);
        }

        for (var i = vectors * Vector256<uint>.Count; i < Math.Min(n, MaxValues); i++)
        {
            var p = bgra[i];
            rgba[i] = (p & 0xFF00FF00u) | ((p >> 16) & 0xFFu) | ((p & 0xFFu) << 16);
        }
    }

    private static void Opaque(ReadOnlySpan<byte> rgb, Span<byte> rgba)
    {
        var n = Math.Min(rgb.Length / 3, rgba.Length / 4);
        _ = Assert(n >= 0) && Assert(rgb.Length % 3 == 0);
        for (var i = 0; i < Math.Min(n, MaxValues); i++)
            (rgba[4 * i], rgba[4 * i + 1], rgba[4 * i + 2], rgba[4 * i + 3]) = (rgb[3 * i], rgb[3 * i + 1], rgb[3 * i + 2], 255);
    }

    // GL_SHORT into GL_RGBA16: clamped to [0, 1], times 65535 rounded to the nearest even. For s in [0, 32767] that is
    // s * 65535 / 32767 = 2s + s / 32767 rounded, 2s + 1 from s = 16384 on: integer lanes, no widening
    internal static void Normalized(ReadOnlySpan<short> source, Span<ushort> into)
    {
        var n = Math.Min(source.Length, into.Length);
        var vectors = Vector256.IsHardwareAccelerated ? n / Vector256<short>.Count : 0;
        if (!Assert(n >= 0) || !Assert(vectors * Vector256<short>.Count <= n)) return;
        for (var v = 0; v < Math.Min(vectors, MaxValues); v++)
        {
            var at = v * Vector256<short>.Count;
            var clamped = Vector256.Max(Vector256.Create(source[at..]), Vector256<short>.Zero).AsUInt16();
            (clamped + clamped + Vector256.ShiftRightLogical(clamped, 14)).CopyTo(into[at..]);
        }

        for (var i = vectors * Vector256<short>.Count; i < Math.Min(n, MaxValues); i++) into[i] = Unorm(source[i]);
    }

    internal static ushort Unorm(short value)
    {
        var clamped = Math.Max((int)value, 0);
        _ = Assert(clamped <= short.MaxValue) && Assert(clamped >> 14 <= 1);
        return (ushort)(2 * clamped + (clamped >> 14));
    }

    private static void Halves(ReadOnlySpan<float> source, Span<Half> into)
    {
        var n = Math.Min(source.Length, into.Length);
        _ = Assert(n >= 0) && Assert(source.Length == into.Length);
        for (var i = 0; i < Math.Min(n, MaxValues); i++) into[i] = (Half)source[i];
    }
}
