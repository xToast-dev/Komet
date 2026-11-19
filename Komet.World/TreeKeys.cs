using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.Datastructures;

namespace Komet.World;

// TreeAttribute.FromBytes reads each key as a new string only to intern it (~100 MB in a join's first minute). Decoded
// on the stack and looked up among seen names instead; same format as BinaryReader.ReadString. GetString interns every
// read value (EntityPlayer.PlayerUID: dozens a frame, 3 % of the main thread at a join): the same string object answers
// from a per-thread cache by identity instead, exact as interning never changes its answer.
internal static class TreeKeys
{
    private const int MaxInstructions = 256, MaxKey = 256, MaxKeys = 1 << 16, Slots = 64;
    private static readonly ConcurrentDictionary<string, string> Known = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> ByChars =
        Known.GetAlternateLookup<ReadOnlySpan<char>>();

    private static int _known; // Known's entries: its Count takes every bucket lock
    private static bool _rewritten, _spanRewritten;
    [ThreadStatic] private static (string? From, string To)[]? _interned;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten || _spanRewritten;
    public static bool Reads { get; private set; } // GetString's intern cached

    public static void Install(Harmony harmony)
    {
        var from = AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes), [typeof(BinaryReader)]);
        (_rewritten, _spanRewritten, Reads) =
            (false, false, false); // before any return: the probe reads either one, the last world's included
        if (!NotNull(from)) return;

        _ = harmony.Patch(from, transpiler: new HarmonyMethod(Rewrite));
        var get = AccessTools.Method(typeof(TreeAttribute), nameof(TreeAttribute.GetString),
            [typeof(string), typeof(string)]);
        if (get is not null) _ = harmony.Patch(get, transpiler: new HarmonyMethod(RewriteReads));

        // 1.23 moved the read to a span reader (the BinaryReader overload only forwards); its type is unknown here
        if (AccessTools.TypeByName("Vintagestory.API.Datastructures.BinarySpanReader") is { } span &&
            NotNull(AccessTools.Method(span, "ReadString", [typeof(int)])) &&
            AccessTools.DeclaredMethod(typeof(TreeAttribute), nameof(TreeAttribute.FromBytes),
                [span.MakeByRefType()]) is { } spanFrom)
        {
            _ = harmony.Patch(spanFrom, transpiler: new HarmonyMethod(RewriteSpan));
        }
    }

    // The one string.Intern of GetString, else the engine's IL
    private static List<CodeInstruction> RewriteReads(IEnumerable<CodeInstruction> instructions)
    {
        var (intern, mine) = (AccessTools.Method(typeof(string), nameof(string.Intern), [typeof(string)]),
            AccessTools.Method(typeof(TreeKeys), nameof(Interned)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(intern) ||
            !NotNull(mine))
        {
            return code;
        }

        Reads = Il.SubstituteSingle(code, c => c.Calls(intern), mine);
        return code;
    }

    // 1.23's span FromBytes: the one string.Intern after ReadString(int) is the key, cache it by value as Key does
    private static List<CodeInstruction> RewriteSpan(IEnumerable<CodeInstruction> instructions)
    {
        var (intern, mine) = (AccessTools.Method(typeof(string), nameof(string.Intern), [typeof(string)]),
            AccessTools.Method(typeof(TreeKeys), nameof(KnownKey)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(intern) || !NotNull(mine))
        {
            return code;
        }

        _spanRewritten = Il.SubstituteSingle(code, c => c.Calls(intern), mine);
        return code;
    }

    // string.Intern(text) with Key's seen-names table: the span reader hands over the decoded key itself
    internal static string KnownKey(string text)
    {
        if (!Enabled) return string.Intern(text);

        if (!NotNull(text)) return text;
        if (Known.TryGetValue(text, out var known)) return known;

        var key = string.Intern(text);
        if (Volatile.Read(ref _known) < MaxKeys && Known.TryAdd(key, key)) _ = Interlocked.Increment(ref _known);
        return key;
    }

    // string.Intern(text), the last answers for these very objects kept per thread
    internal static string Interned(string text)
    {
        if (!Enabled) return string.Intern(text);

        if (!NotNull(text)) return text;

        var cache = _interned ??= new (string?, string)[Slots];
        ref var slot = ref cache[RuntimeHelpers.GetHashCode(text) & (Slots - 1)];
        if (ReferenceEquals(slot.From, text)) return slot.To;

        slot = (text, string.Intern(text));
        return slot.To;
    }

    // Exactly one ReadString, the one string.Intern takes, else the engine's IL
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (read, intern, mine) = (AccessTools.Method(typeof(BinaryReader), nameof(BinaryReader.ReadString), []),
            AccessTools.Method(typeof(string), nameof(string.Intern), [typeof(string)]),
            AccessTools.Method(typeof(TreeKeys), nameof(Key)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(read) || !NotNull(intern) ||
            !NotNull(mine))
        {
            return code;
        }

        var at = Il.Single(code, c => c.Calls(read));
        if (at < 0)
        {
            // 1.23's BinaryReader overload only forwards to the span reader, which RewriteSpan handles
            _rewritten = false;
            return code;
        }

        _rewritten = Index(at + 1, code.Count) && code[at + 1].Calls(intern) && Il.Substitute(code, at, mine);
        return code;
    }

    // Replaces reader.ReadString(); string.Intern then finds it interned
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
        if (ByChars.TryGetValue(chars[..count], out var known)) return known;

        var key = string.Intern(new string(chars[..count]));
        if (Volatile.Read(ref _known) < MaxKeys && Known.TryAdd(key, key)) _ = Interlocked.Increment(ref _known);

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
