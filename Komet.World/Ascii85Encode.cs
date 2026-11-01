using System.Buffers.Binary;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.World;

// Worldgen re-encodes every placed structure's block entity data as Ascii85 text (BlockSchematic.TransformWhilePacked):
// the engine's Encode grows a StringBuilder and makes a new char[5] for every four bytes, the bulk of the char[]
// garbage of a server generating new land (4 MB/s in a user's protocol). The same text here in one string of its
// exact length: a group of four zero bytes is 'z', any other five characters, a last group of n bytes n + 1. Switched
// with WorldGenScratch.
internal static class Ascii85Encode
{
    private const int MaxGroups = 1 << 28; // a byte[] holds at most 2^31 bytes

    public static bool Patched { get; private set; }

    public static void Install(Harmony harmony)
    {
        Patched = false;
        var encode = AccessTools.DeclaredMethod(typeof(Ascii85), nameof(Ascii85.Encode), [typeof(byte[])]);
        if (!NotNull(harmony) || encode is null) return;
        Patched = harmony.Patch(encode, new HarmonyMethod(typeof(Ascii85Encode), nameof(Encoding))) is not null;
    }

    // Prefix: the engine's null check stays the engine's (it throws)
    private static bool Encoding(byte[]? bytes, ref string __result)
    {
        if (!WorldGenScratch.Enabled || bytes is null) return true;
        __result = Encode(bytes);
        return false;
    }

    internal static string Encode(byte[] bytes)
    {
        var (whole, rest) = (bytes.Length / 4, bytes.Length % 4);
        var length = rest > 0 ? rest + 1 : 0;
        for (var g = 0; g < Math.Min(whole, MaxGroups); g++) length += Whole(bytes, g) == 0 ? 1 : 5;
        return string.Create(length, bytes, static (span, data) =>
        {
            var (groups, left, at) = (data.Length / 4, data.Length % 4, 0);
            for (var g = 0; g < Math.Min(groups, MaxGroups); g++)
            {
                var value = Whole(data, g);
                if (value == 0) span[at++] = 'z';
                else at += Write(span[at..], value, 5);
            }

            if (left > 0) _ = Write(span[at..], Group(data, groups * 4, left), left + 1);
        });
    }

    // The g-th whole group: one big-endian load, not four shifted bytes (twice per group, per placed structure)
    private static uint Whole(byte[] bytes, int g) => BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(g * 4, 4));

    // Up to four bytes, big-endian, the missing low ones zero
    private static uint Group(byte[] bytes, int at, int count)
    {
        var value = 0u;
        for (var i = 0; i < Math.Min(count, 4); i++) value |= (uint)bytes[at + i] << (24 - i * 8);
        return value;
    }

    // The value's five base-85 digits, the first `count` of them
    private static int Write(Span<char> into, uint value, int count)
    {
        Span<char> digits = stackalloc char[5];
        for (var i = 0; i < 5; i++)
        {
            digits[4 - i] = (char)(value % 85 + 33);
            value /= 85;
        }

        digits[..count].CopyTo(into);
        return count;
    }
}
