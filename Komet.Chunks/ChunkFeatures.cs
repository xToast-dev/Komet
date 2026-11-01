using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Api.FeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Chunks;

// Chunk arrival: ChunkLookup, ChunkBudget, ChunkLoadBudget, JoinPacer, ChunkPacking, ChunkThreadClosure,
// DecompressScratch and DirectChunks.
// In install order; Core's Features places them among the other modules'.
internal static class ChunkFeatures
{
    internal static readonly Feature[] All =
    [
        new("ChunkLookup")
        {
            Replaces = () =>
            [
                AccessTools.Method(typeof(ClientWorldMap), "GetChunk", [typeof(long)]),
                AccessTools.Method(typeof(ClientWorldMap), "GetChunk", [typeof(int), typeof(int), typeof(int)]),
                AccessTools.Method(typeof(ClientWorldMap), "GetChunkAtBlockPos", [typeof(int), typeof(int), typeof(int)])
            ],
            Install = c => ChunkLookup.Install(c.Harmony),
            Knobs = [Switch("ChunkLookup", "chunks", "arrival", () => ChunkLookup.Enabled,
                on => ChunkLookup.Enabled = on)],
            Stop = _ => ChunkLookup.Clear(),
            Probe = () => ChunkLookup.Installed ? Active : EngineChanged
        },
        new("ChunkBudget")
        {
            Replaces = ChunkBudget.Seams,
            Rivals = PatchKinds.Body,
            Install = c => ChunkBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("UploadCap", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis,
                    ChunkBudget.Uncapped, () => ChunkBudget.CapMillis, v => ChunkBudget.CapMillis = v)
                    { Unit = "ms", Weighed = true, Presets = new(Quality: 5, Balanced: 3, Speed: 2) },
                Switch("UploadYield", "render", "upload", () => ChunkBudget.Yield, on => ChunkBudget.Yield = on)
                    with { Weighed = true }],
            Probe = () => ChunkBudget.Capped ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkBudget.Seams, ChunkBudget.Fingerprint)]
        },
        new("ChunkLoadBudget")
        {
            Replaces = () => [ChunkLoadBudget.Seams()[0]],
            Rivals = PatchKinds.Body,
            Install = c => ChunkLoadBudget.Install(c.Harmony, c.Logger),
            Knobs = [new("ChunkLoadBudget", "chunks", "arrival", ChunkLoadBudget.Engine, ChunkLoadBudget.MaxMillis,
                ChunkLoadBudget.Engine, () => ChunkLoadBudget.Millis, v => ChunkLoadBudget.Millis = v)
                { Unit = "ms", Weighed = true, Presets = new(Quality: 5, Balanced: 3, Speed: 2) }],
            Probe = () => ChunkLoadBudget.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkLoadBudget.Seams, ChunkLoadBudget.Fingerprint)]
        },
        // the integrated server's chunk flood paced to what the client takes in (GC pause share, tessellation backlog)
        new("JoinPacer")
        {
            ServerOnly = true,
            Install = c => JoinPacer.Install(c.Api),
            Stop = _ => JoinPacer.Stop(),
            Knobs = [Switch("JoinPacer", "chunks", "arrival", () => JoinPacer.Enabled, on => JoinPacer.Enabled = on)
                with { Weighed = true }]
        },
        // client chunk data packed in RAM sooner and faster than SystemCompressChunks alone
        new("ChunkPacking")
        {
            Replaces = () => ChunkPacking.Replaced,
            Install = c => ChunkPacking.Install(c.Harmony),
            // little RAM: chunks packed sooner and harder (less memory, a little more CPU when flying)
            Knobs = [new("ChunkPacking", "chunks", "memory", 0, ChunkPacking.Max, 0, () => ChunkPacking.Level,
                v => ChunkPacking.Level = v)
            {
                Weighed = true, Suggest = hw => hw.RamGb switch { > 0 and <= 12 => 3, > 12 and <= 20 => 2, _ => null }
            }],
            Stop = _ => ChunkPacking.Stop(),
            Probe = () => ChunkPacking.Installed ? Active : EngineChanged
        },
        new("ChunkThreadClosure")
        {
            Install = c => ChunkThreadClosure.Install(c.Harmony),
            ServerOnly = true,
            Probe = () => ChunkThreadClosure.Rewritten ? Active : EngineChanged
        },
        new("DecompressScratch")
        {
            Replaces = () => [DecompressScratch.Target()],
            Rivals = PatchKinds.Body,
            Install = c => DecompressScratch.Install(c.Harmony),
            Knobs = [Switch("DecompressScratch", "chunks", "arrival", () => DecompressScratch.Enabled,
                on => DecompressScratch.Enabled = on)],
            Probe = () => DecompressScratch.Rewritten ? Active : EngineChanged
        },
        new("DirectChunks")
        {
            Install = c =>
            {
                DirectChunks.Install(c.Harmony);
                PackedReads.Install(c.Harmony); // single block reads of packed chunks, switched with PackedChunks
            },
            Knobs = [Switch("DirectChunks", "chunks", "arrival", () => DirectChunks.Enabled, on => DirectChunks.Enabled = on)],
            Probe = () => DirectChunks.Installed && PackedReads.Installed ? Active : EngineChanged
        }
    ];
}
