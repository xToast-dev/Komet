using System.Collections.Concurrent;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Datastructures;

namespace Komet.World;

// Every block entity that arrives with a chunk, on the server and the client, is read by TreeAttribute.FromBytes, which reads each
// attribute's key as a new string (BinaryReader.ReadString) only to intern it at once: a hundred megabytes of garbage in the first
// minute of a join, all of it copies of a few hundred names. The rewrite reads the key's bytes onto the stack and looks the name up
// among those seen before; a new one becomes a string and is interned as the engine interns it. BinaryReader.ReadString reads the
// same 7-bit length and UTF-8 bytes (the engine's readers all use the default encoding).
internal static class TreeKeys
{
    private const int MaxInstructions = 256, MaxKey = 256, MaxKeys = 1 << 16;
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> ByChars =
        Known.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        var from = AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes), [typeof(BinaryReader)]);
        Rewritten = false;
        if (!NotNull(harmony) || !NotNull(from)) return;
        _ = NotNull(harmony.Patch(from, transpiler: new HarmonyMethod(Rewrite)));
    }

    // Exactly one ReadString, the one string.Intern takes, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (read, intern, mine) = (AccessTools.Method(typeof(BinaryReader), nameof(BinaryReader.ReadString), []),
            AccessTools.Method(typeof(string), nameof(string.Intern), [typeof(string)]), AccessTools.Method(typeof(TreeKeys), nameof(Key)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(read) || !NotNull(intern) ||
            !NotNull(mine)) return code;
        var at = Il.Single(code, c => c.Calls(read));
        Rewritten = Assert(at >= 0) && Index(at + 1, code.Count) && code[at + 1].Calls(intern) && Il.Substitute(code, at, mine);
        return code;
    }

    // Stands in for reader.ReadString(); string.Intern then finds it interned
    internal static string Key(BinaryReader reader)
    {
        if (!Enabled) return reader.ReadString();
        var length = reader.Read7BitEncodedInt();
        if (length is < 0 or > MaxKey) return Long(reader, length);
        Span<byte> bytes = stackalloc byte[length];
        var got = 0;
        for (var i = 0; i < MaxKey && got < length; i++) // each read brings at least one byte
        {
            var read = reader.Read(bytes[got..]);
            if (read <= 0) throw new EndOfStreamException();
            got += read;
        }

        Span<char> chars = stackalloc char[length]; // UTF-8 never decodes to more chars than bytes
        var count = Encoding.UTF8.GetChars(bytes, chars);
        _ = Assert(count <= length);
        if (ByChars.TryGetValue(chars[..count], out var known)) return known;
        var key = string.Intern(new string(chars[..count]));
        if (Known.Count < MaxKeys) _ = Known.TryAdd(key, key);
        return key;
    }

    private static string Long(BinaryReader reader, int length)
    {
        if (!Assert(length >= 0)) throw new FormatException("negative string length");
        return Encoding.UTF8.GetString(reader.ReadBytes(length) is { } bytes && bytes.Length == length
            ? bytes
            : throw new EndOfStreamException());
    }
}
