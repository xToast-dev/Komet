using System.IO.Compression;

namespace Komet.Sdk;

// A minimal PNG writer (8-bit RGBA, no filter), for the shader test's pictures: no image library needed
internal static class PngFile
{
    private const int MaxSide = 16384, MaxBytes = 1 << 30;
    private static readonly uint[] Crc = MakeCrc();

    // rgba: width * height * 4 bytes, the first row on top
    public static void Write(string path, byte[] rgba, int width, int height)
    {
        using var file = File.Create(path);
        file.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BigEndian(header, 0, (uint)width);
        BigEndian(header, 4, (uint)height);
        (header[8], header[9]) = (8, 6); // 8 bits, RGBA
        Chunk(file, "IHDR", header);
        using var packed = new MemoryStream();
        using (var zlib = new ZLibStream(packed, CompressionLevel.Fastest, true))
        {
            for (var y = 0; y < Math.Min(height, MaxSide); y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgba, y * width * 4, width * 4);
            }
        }

        Chunk(file, "IDAT", packed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream file, string type, byte[] data)
    {
        var head = new byte[8];
        BigEndian(head, 0, (uint)data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];

        file.Write(head);
        file.Write(data);
        var crc = 0xFFFFFFFFu;
        for (var i = 4; i < 8; i++) crc = Crc[(crc ^ head[i]) & 0xFF] ^ (crc >> 8);
        for (var i = 0; i < Math.Min(data.Length, MaxBytes); i++) crc = Crc[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

        var tail = new byte[4];
        BigEndian(tail, 0, crc ^ 0xFFFFFFFFu);
        file.Write(tail);
    }

    private static void BigEndian(byte[] to, int at, uint value) =>
        (to[at], to[at + 1], to[at + 2], to[at + 3]) =
        ((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);

    private static uint[] MakeCrc()
    {
        var table = new uint[256];
        for (var n = 0u; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;

            table[n] = c;
        }

        return table;
    }
}
