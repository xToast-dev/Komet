using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.World;

// The world generator rotates every structure it places while it is packed (BlockSchematic.TransformWhilePacked), and each block
// entity of a structure is encoded back to text through Ascii85.Encode: a StringBuilder grown from its first guess, and a new char[5]
// for every four bytes (EncodeValue). In the world-join traces that was the largest allocation of the integrated server (~150 MB in
// 50 s of chars and strings), and every MB the server allocates in the client's process triggers the client's collections too.
// Here the length is worked out first and the string written once, in place (string.Create): the same characters, no other garbage.
internal static class Ascii85Text
{
    private const int Group = 4, Digits = 5, Base = 85, First = 33, MaxGroups = int.MaxValue / Digits - 1;

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    public static void Install(Harmony harmony)
    {
        Patched = false;
        var target = AccessTools.DeclaredMethod(typeof(Ascii85), nameof(Ascii85.Encode), [typeof(byte[])]);
        if (!NotNull(harmony) || !NotNull(target) || !Assert(target.ReturnType == typeof(string))) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Encode)));
    }

    // A null array is left to the engine, which throws
    private static bool Encode(byte[]? bytes, ref string __result)
    {
        if (!Enabled || bytes is null || bytes.Length / Group >= MaxGroups) return true;
        __result = Of(bytes);
        return !Assert(__result.Length <= (bytes.Length / Group + 1) * Digits);
    }

    // Ascii85.Encode: each full group of four bytes big-endian as five base-85 digits from '!', or 'z' where all four are zero; a
    // tail of n bytes padded with zeros and cut to n + 1 digits
    internal static string Of(byte[] bytes)
    {
        var (full, tail) = (bytes.Length / Group, bytes.Length % Group);
        var length = tail == 0 ? 0 : tail + 1;
        for (var g = 0; g < Math.Min(full, MaxGroups); g++) length += Value(bytes, g * Group, Group) == 0 ? 1 : Digits;
        if (length == 0) return string.Empty;
        _ = Assert(length <= (full + 1) * Digits);
        return string.Create(length, bytes, static (chars, source) =>
        {
            var (groups, rest) = (source.Length / Group, source.Length % Group);
            var at = 0;
            for (var g = 0; g < Math.Min(groups, MaxGroups); g++)
            {
                var value = Value(source, g * Group, Group);
                if (value == 0) chars[at++] = 'z';
                else at += Write(chars[at..], value, Digits);
            }

            if (rest > 0) at += Write(chars[at..], Value(source, groups * Group, rest), rest + 1);
            _ = Assert(at == chars.Length);
        });
    }

    // count bytes from start, big-endian, the missing ones zero
    private static uint Value(byte[] bytes, int start, int count)
    {
        uint value = 0;
        _ = Assert(count is > 0 and <= Group) && Assert(start >= 0);
        for (var i = 0; i < Group; i++)
            if (i < count)
                value |= (uint)bytes[start + i] << (24 - i * 8);
        return value;
    }

    // The first `keep` of the value's five digits, most significant first
    private static int Write(Span<char> chars, uint value, int keep)
    {
        Span<char> digits = stackalloc char[Digits];
        for (var k = 0; k < Digits; k++)
        {
            digits[Digits - 1 - k] = (char)(value % Base + First);
            value /= Base;
        }

        digits[..keep].CopyTo(chars);
        return Assert(keep is > 0 and <= Digits) ? keep : 0;
    }
}
