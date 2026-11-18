using HarmonyLib;
using Komet.Core;
using Komet.Api;
using Vintagestory.Client.NoObf;
using static Komet.Api.KapiFeatureState;
using static Komet.Core.FeatureKit;

namespace Komet.Chunks;

// Chunk arrival: ChunkLookup, ChunkBudget, ChunkLoadBudget, JoinPacer, ChunkPacking, ChunkThreadClosure,
// DecompressScratch, DirectChunks and PackedTicks.
// Each declares its place (Order, Requires, After); FeatureOrder sorts them among the other modules'.
internal static class ChunkFeatures
{
    internal static readonly Feature[] All =
    [
        new("ChunkLookup")
        {
            Order = 360,
            Replaces = () =>
            [
                AccessTools.Method(typeof(ClientWorldMap), "GetChunk", [typeof(long)]),
                AccessTools.Method(typeof(ClientWorldMap), "GetChunk", [typeof(int), typeof(int), typeof(int)]),
                AccessTools.Method(typeof(ClientWorldMap), "GetChunkAtBlockPos",
                    [typeof(int), typeof(int), typeof(int)])
            ],
            Install = c => ChunkLookup.Install(c.Harmony),
            Knobs =
            [
                Switch("ChunkLookup", "chunks", "arrival", () => ChunkLookup.Enabled,
                    on => ChunkLookup.Enabled = on)
            ],
            Stop = _ => ChunkLookup.Clear(),
            Probe = () => ChunkLookup.Installed ? Active : EngineChanged
        },
        new("ChunkBudget")
        {
            Order = 500,
            Replaces = ChunkBudget.Seams,
            Rivals = KapiPatchKinds.Body,
            Install = c => ChunkBudget.Install(c.Harmony, c.Logger),
            Knobs =
            [
                new Knob("UploadCap", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis,
                        ChunkBudget.Uncapped, () => ChunkBudget.CapMillis, v => ChunkBudget.CapMillis = v)
                    { Unit = "ms", Weighed = true, Presets = new PresetValues(5, 3, 2) },
                Switch("UploadYield", "render", "upload", () => ChunkBudget.Yield, on => ChunkBudget.Yield = on)
                    with
                    {
                        Weighed = true
                    }
            ],
            Probe = () => ChunkBudget.Capped ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkBudget.Seams, ChunkBudget.Fingerprint)]
        },
        new("ChunkLoadBudget")
        {
            Order = 510,
            Replaces = () => [ChunkLoadBudget.Seams()[0]],
            Rivals = KapiPatchKinds.Body,
            Install = c => ChunkLoadBudget.Install(c.Harmony, c.Logger),
            Knobs =
            [
                new Knob("ChunkLoadBudget", "chunks", "arrival", ChunkLoadBudget.Engine, ChunkLoadBudget.MaxMillis,
                        ChunkLoadBudget.Engine, () => ChunkLoadBudget.Millis, v => ChunkLoadBudget.Millis = v)
                    { Unit = "ms", Weighed = true, Presets = new PresetValues(5, 3, 2) }
            ],
            Probe = () => ChunkLoadBudget.Rewritten ? Active : EngineChanged,
            Shapes = [new EngineBodies(ChunkLoadBudget.Seams, ChunkLoadBudget.Fingerprint)]
        },
        // the integrated server's chunk flood paced to what the client takes in (GC pause share, tessellation backlog)
        new("JoinPacer")
        {
            Order = 520,
            ServerOnly = true,
            Install = c => JoinPacer.Install(c.Api),
            Stop = _ => JoinPacer.Stop(),
            Knobs =
            [
                Switch("JoinPacer", "chunks", "arrival", () => JoinPacer.Enabled, on => JoinPacer.Enabled = on)
                    with
                    {
                        Weighed = true
                    }
            ]
        },
        // client chunk data packed in RAM sooner and faster than SystemCompressChunks alone
        new("ChunkPacking")
        {
            Order = 530,
            Replaces = () => ChunkPacking.Replaced,
            Install = c => ChunkPacking.Install(c.Harmony),
            // little RAM: chunks packed sooner and harder (less memory, a little more CPU when flying)
            Knobs =
            [
                new Knob("ChunkPacking", "chunks", "memory", 0, ChunkPacking.Max, 0, () => ChunkPacking.Level,
                    v => ChunkPacking.Level = v)
                {
                    Weighed = true,
                    Suggest = hw => hw.RamGb switch { > 0 and <= 12 => 3, > 12 and <= 20 => 2, _ => null }
                }
            ],
            Stop = _ => ChunkPacking.Stop(),
            Probe = () => ChunkPacking.Installed ? Active : EngineChanged
        },
        new("ChunkThreadClosure")
        {
            Order = 580,
            Install = c => ChunkThreadClosure.Install(c.Harmony),
            ServerOnly = true,
            Probe = () => ChunkThreadClosure.Rewritten ? Active : EngineChanged
        },
        new("DecompressScratch")
        {
            Order = 610,
            Replaces = () => [DecompressScratch.Target()],
            Rivals = KapiPatchKinds.Body,
            Install = c =>
            {
                DecompressScratch.Install(c.Harmony);
                ChunkFactories.Install(c.Harmony);
            },
            Knobs =
            [
                Switch("DecompressScratch", "chunks", "arrival", () => DecompressScratch.Enabled,
                    on => DecompressScratch.Enabled = on)
            ],
            Probe = () => DecompressScratch.Rewritten ? Active : EngineChanged
        },
        new("DirectChunks")
        {
            Order = 620,
            Install = c =>
            {
                DirectChunks.Install(c.Harmony);
                PackedReads.Install(c.Harmony); // single block reads of packed chunks, switched with PackedChunks
            },
            Knobs =
            [
                Switch("DirectChunks", "chunks", "arrival", () => DirectChunks.Enabled, on => DirectChunks.Enabled = on)
            ],
            Probe = () => DirectChunks.Installed && PackedReads.Installed ? Active : EngineChanged
        },
        // random block ticks leave chunks without tickable blocks packed
        new("PackedTicks")
        {
            Order = 630,
            Replaces = PackedTicks.Seams,
            ServerOnly = true,
            Install = c => PackedTicks.Install(c.Harmony),
            Knobs =
            [
                Switch("PackedTicks", "chunks", "memory", () => PackedTicks.Enabled, on => PackedTicks.Enabled = on)
            ],
            Stop = _ => PackedTicks.Stop(),
            Probe = () => PackedTicks.Installed && PackedTicks.Rewritten ? Active : EngineChanged
        }
    ];
}
