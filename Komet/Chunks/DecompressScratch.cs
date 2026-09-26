using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.Common;

namespace Komet.Chunks;

// Every chunk layer the tesselator, the network thread or the server unpacks goes through Compression.DecompressCombined, which
// decompresses the block planes with ICompression.Decompress. For zstd that fills the thread's reusable buffer and hands back an
// exact-size copy of it (CompressionZSTD.Decompress -> ArrayConvert.ByteToByte). The copy is read twice - the length check and
// ByteToIntArrays into the int[1024] slices - and dropped: 8 MB/s of garbage while chunks stream in. The rewrite hands the method
// the thread's buffer itself and answers the length check with the decompressed size instead of the array's length, which keeps
// the InvalidDataException for short data. Between the two nothing compresses on this thread: ByteToIntArrays only asks the pool
// (ChunkDataPool.NewData) for slices before it copies.
internal static class DecompressScratch
{
    private const string Method = "DecompressCombined";
    private const int MaxInstructions = 1024;

    [ThreadStatic] private static byte[]? _buffer;
    [ThreadStatic] private static int _length;
    private static long _saved;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static long Saved =>
        Interlocked.Read(ref _saved); // bytes the engine would have copied, a total while Counting.Hud

    public static void Install(Harmony harmony)
    {
        var method = Target();
        if (!NotNull(harmony) || !NotNull(method)) return;
        _ = NotNull(harmony.Patch(method, transpiler: new HarmonyMethod(typeof(DecompressScratch), nameof(Rewrite))));
    }

    internal static MethodInfo? Target()
    {
        var method = AccessTools.Method(typeof(Compression), Method); // internal static, one overload
        return NotNull(method) && Assert(method.ReturnType == typeof(int[])) ? method : null;
    }

    // callvirt ICompression.Decompress(byte[], int, int); dup; ldlen; conv.i4 - exactly once, and the array consumed by exactly one
    // ByteToIntArrays with no other call in between but the exception's constructor, and nothing that could keep it: no store, no second
    // dup, no return. Anything else hands back the engine's IL.
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        Rewritten = false;
        var decompress = AccessTools.Method(typeof(ICompression), nameof(ICompression.Decompress),
            [typeof(byte[]), typeof(int), typeof(int)]);
        var consume = AccessTools.Method(typeof(ArrayConvert), nameof(ArrayConvert.ByteToIntArrays));
        var (mine, length) =
            (AccessTools.Method(typeof(DecompressScratch), nameof(Decompress)),
                AccessTools.Method(typeof(DecompressScratch), nameof(Length)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(decompress) ||
            !NotNull(consume) || !NotNull(mine) ||
            !NotNull(length)) return code;
        var at = Il.Single(code, c => c.Calls(decompress));
        if (!Assert(at >= 0) || !IsLengthCheck(code, at)) return code;
        var stop = Stop(code, at + 4, consume);
        if (!Index(stop, code.Count) || !code[stop].Calls(consume))
            return code; // running off the body's end is logged, stopping is not
        // The length first, since on its own it answers every array with its length as ldlen does; conv.i4 after it is a no-op on an int
        Rewritten = Assert(Il.Substitute(code, at + 2, length)) && Assert(Il.Substitute(code, at, mine));
        return code;
    }

    private static bool IsLengthCheck(List<CodeInstruction> code, int at)
    {
        if (!Index(at + 3, code.Count) || !Assert(code[at].opcode == OpCodes.Callvirt)) return false;
        return code[at + 1].opcode == OpCodes.Dup && code[at + 2].opcode == OpCodes.Ldlen &&
               code[at + 3].opcode == OpCodes.Conv_I4;
    }

    // Where the walk from `from` stops: at ByteToIntArrays, at any other call but the throw's constructor, or at anything that could keep
    // the array; code.Count when the body ends first
    private static int Stop(List<CodeInstruction> code, int from, MethodInfo consume)
    {
        if (!NotNull(consume) || !Assert(from > 0)) return code.Count;
        for (var i = from; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(consume) || (code[i].opcode.FlowControl == FlowControl.Call && !Throws(code[i])) ||
                Keeps(code[i]))
                return i;
        return code.Count;
    }

    // The engine's copy could be stored, duplicated or returned and read later; the thread's buffer is overwritten by the next
    // decompression on this thread, so it may only go from the length check straight into ByteToIntArrays
    private static bool Keeps(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.Size > 0)) return true;
        var op = code.opcode;
        return op == OpCodes.Dup || op.FlowControl == FlowControl.Return ||
               op.Name?.StartsWith("st", StringComparison.Ordinal) != false;
    }

    // The only call the length check may make: new InvalidDataException() for the throw
    private static bool Throws(CodeInstruction code)
    {
        if (!NotNull(code) || !Assert(code.opcode.FlowControl == FlowControl.Call)) return false;
        return code.opcode == OpCodes.Newobj && code.operand is ConstructorInfo { DeclaringType: var type } &&
               type == typeof(InvalidDataException);
    }

    // Stands in for compression.Decompress(data, offset, length). Only zstd's Decompress is known to be DecompressAndSize plus a copy,
    // so anything else - and an out-of-range size, which the engine's copy rejects with its own exception - takes the engine's call.
    internal static byte[] Decompress(ICompression compression, byte[] data, int offset, int length)
    {
        if (!Enabled || !NotNull(compression) || compression.GetType() != typeof(CompressionZSTD))
            return compression.Decompress(data, offset, length);
        var size = compression.DecompressAndSize(data, offset, length, out var buffer);
        if (size < 0)
            return []; // what CompressionZSTD.Decompress returns on a zstd error; the length check then throws
        if (!NotNull(buffer) || !Assert(size <= buffer.Length)) return compression.Decompress(data, offset, length);
        (_buffer, _length) = (buffer, size);
        if (Counting.Hud) _ = Interlocked.Add(ref _saved, size);
        return buffer;
    }

    // Stands in for the length check's ldlen: the decompressed size for the buffer handed out above, the array's length otherwise
    internal static int Length(byte[] array)
    {
        if (!ReferenceEquals(array, _buffer) || !NotNull(array)) return array.Length; // null throws here as ldlen would
        _buffer = null; // one answer per decompression, and zstd may swap its buffer on a later call
        return Assert(_length >= 0) ? _length : array.Length;
    }
}
