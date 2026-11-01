namespace Komet.Test.Synth;

[Category(Synth.Category)]
public sealed class ChunksSynth
{
    private delegate int[] Combined(byte[] data, ref int[][]? blocks, ref int count, Func<int[]>? make);

    private static readonly Combined Unpack =
        AccessTools.Method(typeof(Compression), "DecompressCombined").CreateDelegate<Combined>();

    // A chunk layer of 16 palette entries: four planes, the common case while terrain streams in
    [Test]
    public void DecompressScratch()
    {
        var r = new Random(1);
        var palette = new int[16];
        for (var i = 0; i < palette.Length; i++) palette[i] = r.Next();
        var planes = new int[4 * 1024];
        for (var i = 0; i < planes.Length; i++) planes[i] = r.Next();
        var layer = Compression.CompressAndCombine(planes, palette, 16);
        var slices = Enumerable.Range(0, 15).Select(_ => new int[1024]).ToArray();
        var count = 0;
        _ = Synth.Run("DecompressScratch", "layer16", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var into = slices;
                _ = Unpack(layer, ref into, ref count, null);
            }
        }, 200, () => slices[3][77]);
    }
}
