using Komet.World;

namespace Komet.Test.World;

// Every game sound decodes to the same PCM with the stream sized ahead, and with less garbage
[NonParallelizable]
public sealed class SoundScratchTests
{
    [Test]
    public void SoundsDecodeTheSameWithLessGarbage()
    {
        using var harmony = new TestHarmony("komet-test-soundscratch");
        SoundScratch.Install(harmony);
        Assert.That(SoundScratch.Rewritten, Is.True);
        var files = Directory.GetFiles(Path.Combine(GameInstall.Assets, "game", "sounds"), "*.ogg", SearchOption.AllDirectories);
        Assert.That(files, Is.Not.Empty);
        long engine = 0, sized = 0, kept = 0;
        try
        {
            foreach (var file in files)
            {
                var bytes = File.ReadAllBytes(file);
                SoundScratch.Enabled = false;
                var before = GC.GetAllocatedBytesForCurrentThread();
                var expected = Decode(bytes);
                engine += GC.GetAllocatedBytesForCurrentThread() - before;
                SoundScratch.Enabled = true;
                before = GC.GetAllocatedBytesForCurrentThread();
                var pcm = Decode(bytes);
                sized += GC.GetAllocatedBytesForCurrentThread() - before;
                kept += pcm.Length;
                Assert.That(pcm, Is.EqualTo(expected), Path.GetFileName(file));
            }
        }
        finally
        {
            SoundScratch.Enabled = true;
        }

        TestContext.Out.WriteLine($"SOUND {sized >> 20} MB with the sized stream, {engine >> 20} MB as the engine, {kept >> 20} MB PCM kept");
        Assert.That(sized, Is.LessThan(engine));
    }

    private static byte[] Decode(byte[] ogg) =>
        new OggDecoder().OggToWav(new MemoryStream(ogg), new Asset(ogg, new AssetLocation("game", "sounds/test.ogg"), null)).Pcm;
}
