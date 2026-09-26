using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Test.Chunks;

// The mirror behind the patched ClientWorldMap.GetChunk follows the engine's dictionary through loads and unloads
public sealed class ChunkLookupTests
{
    // More chunks in one unload packet than the old bound of 8192: the server sends a tick's unloads in one packet, however many
    [Test]
    public void AnUnloadPacketLeavesNoChunkBehind()
    {
        const int n = 10000;
        using var rig = new ChunkRig();
        var chunks = (Dictionary<long, ClientChunk>)ChunkRig.Get(rig.Map, "chunks");
        var chunk = ClientChunk.CreateNew(rig.Pool);
        var (loaded, unloading) = (AccessTools.Method(typeof(ChunkLookup), "Loaded"),
            AccessTools.Method(typeof(ChunkLookup), "Unloading"));
        int[] xs = new int[n], ys = new int[n], zs = new int[n];
        var harmony = new Harmony("komet-test-chunklookup");
        try
        {
            ChunkLookup.Install(harmony);
            for (var i = 0; i < n; i++)
            {
                (xs[i], zs[i]) = (i % 100, i / 100);
                chunks[ChunkRig.Key(xs[i], 0, zs[i])] = chunk; // what loadChunkMT inserts, then its postfix stores
                _ = loaded.Invoke(null, [rig.Map, new Packet_ServerChunk { X = xs[i], Z = zs[i] }, chunk]);
            }

            Assert.That(rig.Map.GetChunk(ChunkRig.Key(99, 0, 99)), Is.SameAs(chunk));
            var packet = new Packet_UnloadServerChunk();
            (packet.X, packet.XCount, packet.Y, packet.YCount, packet.Z, packet.ZCount) = (xs, n, ys, n, zs, n);
            _ = unloading.Invoke(null,
                [rig.Game, new Packet_Server { Id = 11, UnloadChunk = packet }]); // the prefix, then the engine
            chunks.Clear();
            var left = Enumerable.Range(0, n).Count(i => rig.Map.GetChunk(ChunkRig.Key(xs[i], 0, zs[i])) is not null);
            Assert.That(left, Is.Zero, "an unloaded chunk was still answered from the mirror");
        }
        finally
        {
            harmony.UnpatchAll(harmony.Id);
            ChunkLookup.Clear();
        }
    }
}
