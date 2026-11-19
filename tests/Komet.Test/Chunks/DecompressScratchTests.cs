using Komet.Chunks;

namespace Komet.Test.Chunks;

// Golden test against the engine: layers compressed by Compression.CompressAndCombine, as ChunkDataLayer does, and unpacked by the
// engine's own DecompressCombined once as shipped and once rewritten. Palettes, slices and palette counts must match, and data that the
// engine rejects must still be rejected - also when the thread's zstd buffer is far longer than what was decompressed into it.
public sealed class DecompressScratchTests
{
    private static readonly Combined Unpack =
        AccessTools.Method(typeof(Compression), "DecompressCombined").CreateDelegate<Combined>();

    [TearDown]
    public void Reset() => DecompressScratch.Enabled = true;

    // A palette of `count` entries in an array rounded up to a power of two, and one random 1024-int plane per palette bit
    private static byte[] Layer(int seed, int count)
    {
        var r = new Random(seed);
        var length = 1;
        for (var i = 0; i < 16 && length < count; i++)
        {
            length <<= 1;
        }

        var bits = length <= 1 ? 1 : (int)Math.Log2(length);
        var palette = new int[length];
        for (var i = 0; i < count; i++)
        {
            palette[i] = r.Next();
        }

        var planes = new int[bits * 1024];
        for (var i = 0; i < planes.Length; i++)
        {
            planes[i] = r.Next();
        }

        return Compression.CompressAndCombine(planes, palette, count);
    }

    private static Unpacked Run(byte[] data)
    {
        int[][]? blocks = null;
        var count = 0;
        var palette = Unpack(data, ref blocks, ref count, () => new int[1024]);
        return new Unpacked(palette, count, blocks);
    }

    private static string? Difference(Unpacked engine, Unpacked mine)
    {
        var bits = (int)Math.Log2(engine.Palette!.Length);
        if (!mine.Palette!.SequenceEqual(engine.Palette)) return "palette";

        if (mine.Count != engine.Count) return "palette count";

        if (mine.Slices!.Length < bits ||
            !Enumerable.Range(0, bits).All(b => mine.Slices[b] is { } plane && plane.SequenceEqual(engine.Slices![b] ?? [])))
        {
            return "slices";
        }

        return mine.Slices.Skip(bits).All(slice => slice is null) ? null : "slices past the planes";
    }

    private static TestHarmony Patched()
    {
        var harmony = new TestHarmony("komet-test-decompressscratch");
        DecompressScratch.Install(harmony);
        Assert.That(DecompressScratch.Rewritten, Is.True, "DecompressCombined no longer has the expected shape");
        return harmony;
    }

    [Test]
    public void UnpacksExactlyWhatTheEngineUnpacks()
    {
        // raw palettes up to 18 entries, zstd-compressed ones above, one to six planes
        int[] counts = [2, 3, 4, 7, 8, 16, 17, 18, 19, 32, 33, 64];
        var layers = counts.SelectMany((count, i) => new[] { Layer(i, count), Layer(100 + i, count) }).ToList();
        layers.Add(Layer(1000, 64)); // leaves a large zstd buffer behind for the small layers after it
        layers.AddRange(counts.Select((count, i) => Layer(200 + i, count)));
        var engine = layers.Select(Run).ToList();
        using var harmony = Patched();
        var mine = layers.Select(Run).ToList();
        Assert.That(engine.Zip(mine, Difference), Is.All.Null);
    }

    // Three palette entries round up to four, two planes, but CompressAndCombine only packed floor(log2(3)) = one: the engine throws
    // InvalidDataException because 4096 decompressed bytes are fewer than two planes. The rewrite has to measure the decompressed
    // size, not the zstd buffer, which a previous 64-entry layer left at 24 KiB.
    [Test]
    public void ShortAndCorruptDataIsStillRejected()
    {
        var planes = new int[1024];
        var shortLayer = Compression.CompressAndCombine(planes, [1, 2, 3], 3);
        var full = Layer(7, 8);
        var truncated = full[..^8];
        var engineShort = Assert.Catch(() => Run(shortLayer));
        var engineTruncated = Assert.Catch(() => Run(truncated));
        using var harmony = Patched();
        _ = Run(Layer(8, 64));
        Assert.Multiple(() =>
        {
            Assert.That(engineShort, Is.TypeOf<InvalidDataException>());
            Assert.That(Assert.Catch(() => Run(shortLayer)), Is.TypeOf(engineShort!.GetType()));
            Assert.That(Assert.Catch(() => Run(truncated)), Is.TypeOf(engineTruncated!.GetType()));
            Assert.That(Run(full).Palette, Is.Not.Null, "the buffer is handed back for the next layer");
        });
    }

    private sealed record Unpacked(int[]? Palette, int Count, int[]?[]? Slices);

    private delegate int[]? Combined(byte[] data, ref int[][]? blocks, ref int refCount, Func<int[]>? newArray);
}
