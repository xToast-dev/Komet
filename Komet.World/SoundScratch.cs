using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;

namespace Komet.World;

// A join decodes every sound to PCM (LoadSoundsSlow, ~10 % of a join's allocations): OggDecoder writes into a
// MemoryStream that starts empty and doubles, then copies it out with ToArray, two to four times the sound's PCM as
// garbage, most of it on the large object heap. Here it writes into a buffer each decoding thread keeps (up to
// MaxScratch), sized from the file (the last Ogg page's granule position, samples a channel, times channels and two
// bytes), and ToArray copies out only what was written: the PCM the game keeps is the one array left. A file this does
// not read gets the engine's empty stream.
internal static class SoundScratch
{
    private const int MaxInstructions = 4096, MaxChannels = 8, MaxScan = 1 << 17, MaxScratch = 16 << 20;
    private const long MaxPcm = 256L << 20;

    [ThreadStatic] private static int _expected;
    [ThreadStatic] private static byte[]? _scratch;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    internal static MethodBase? Target() =>
        AccessTools.DeclaredMethod(typeof(OggDecoder), nameof(OggDecoder.OggToWav), [typeof(Stream), typeof(IAsset)]);

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        if (!NotNull(harmony) || Target() is not MethodInfo decode) return;
        _ = harmony.Patch(decode, new HarmonyMethod(AccessTools.Method(typeof(SoundScratch), nameof(Decoding))),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(SoundScratch), nameof(Rewrite))));
    }

    // The one new MemoryStream() becomes Sized(); the decoder only writes to it and takes ToArray, both virtual
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var made = AccessTools.Constructor(typeof(MemoryStream), []);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(made)) return code;
        Rewritten = Il.SubstituteSingle(code, c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo m && m == made,
            AccessTools.Method(typeof(SoundScratch), nameof(Sized)));
        return code;
    }

    // OggToWav's prefix: the size this sound will decode to, for Sized on this thread
    private static void Decoding(IAsset asset) => _expected = Enabled && asset?.Data is { } data ? Expected(data) : 0;

    internal static MemoryStream Sized()
    {
        var expected = _expected;
        _expected = 0;
        return expected > 0 ? new Pcm(expected) : new MemoryStream();
    }

    // Written into the thread's scratch (or an array of its own past MaxScratch), copied out once
    private sealed class Pcm(int expected) : MemoryStream(0)
    {
        private byte[] _buffer = Scratch(expected);
        private int _length;

        public override long Length => _length;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_length + buffer.Length > _buffer.Length) _buffer = Grown(_buffer, _length, _length + buffer.Length);
            buffer.CopyTo(_buffer.AsSpan(_length));
            _length += buffer.Length;
        }

        public override void WriteByte(byte value) => Write([value]);

        public override byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

        // Past the file's size (a decoder's last block, a few KB): a larger one, the thread's scratch if it fits
        private static byte[] Grown(byte[] old, int kept, int needed)
        {
            var size = (int)Math.Min(Math.Max(needed, (long)old.Length * 2), int.MaxValue);
            var grown = size <= MaxScratch ? Scratch(size) : new byte[size];
            if (!ReferenceEquals(grown, old)) old.AsSpan(0, kept).CopyTo(grown);
            return grown;
        }

        private static byte[] Scratch(int size)
        {
            if (size > MaxScratch) return new byte[size];
            if (_scratch is not { } scratch || scratch.Length < size) _scratch = scratch = new byte[Math.Max(size, 1 << 16)];
            return scratch;
        }
    }


    // Channels from the Vorbis identification header (first packet of the first page), samples from the last page's
    // granule position; 0 when the file is not one plain Vorbis stream this reads
    internal static int Expected(ReadOnlySpan<byte> ogg)
    {
        if (ogg.Length < 64 || !Page(ogg, 0)) return 0;
        var packet = 27 + ogg[26];
        if (packet + 16 > ogg.Length || ogg[packet] != 1 || !ogg.Slice(packet + 1, 6).SequenceEqual("vorbis"u8)) return 0;
        int channels = ogg[packet + 11];
        if (channels is < 1 or > MaxChannels) return 0;
        for (var i = 0; i < Math.Min(ogg.Length - 27, MaxScan); i++)
        {
            var at = ogg.Length - 27 - i;
            if (!Page(ogg, at)) continue;
            var samples = BitConverter.ToInt64(ogg.Slice(at + 6, 8));
            var bytes = samples * channels * 2;
            return samples > 0 && bytes <= MaxPcm ? (int)bytes : 0;
        }

        return 0;
    }

    private static bool Page(ReadOnlySpan<byte> ogg, int at) => at >= 0 && at + 27 <= ogg.Length && ogg.Slice(at, 4).SequenceEqual("OggS"u8);
}
