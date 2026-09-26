using System.Diagnostics.CodeAnalysis;

namespace Komet.Core;

// Page and Group place the knob in the settings dialog (null: bench only), its label is the lang key "settings-" + lower-case Key.
// Engine is the value that leaves the game's own behaviour. Json is the komet-hud.json key when it differs from Key (kept from before
// a rename, so saved settings survive).
internal sealed record Knob(
    string Key,
    string? Page,
    string? Group,
    int Min,
    int Max,
    int Engine,
    Func<int> Get,
    Action<int> Set,
    string? Json = null)
{
    public string Persisted => Json ?? Key;
    public bool IsSwitch => Min == 0 && Max == 1;
    public string Unit { get; init; } = "";

    [SuppressMessage("Globalization", "CA1308", Justification = "lang keys are lower case")]
    public string Label => Key.ToLowerInvariant();
}

// Every switch of a feature static, in dialog order: the settings dialog, komet-hud.json and the benchmark arms all read this table.
// Arms write the statics directly, never HudSettings, which persists what it sets.
internal static class Knobs
{
    public const int MaxKnobs = 64;

    private static readonly Knob[] Table =
    [
        Switch("FrustumSweep", "render", "culling", () => FrustumSweep.Enabled, on => FrustumSweep.Enabled = on),
        Switch("SunOcclusion", "render", "culling", () => SunOcclusion.Enabled, on => SunOcclusion.Enabled = on),
        Switch("ShaderUseCache", "render", "drawing", () => ShaderUseCache.Enabled, on => ShaderUseCache.Enabled = on),
        Switch("IndirectDraw", "render", "drawing", () => IndirectDraw.Enabled, on => IndirectDraw.Enabled = on),
        Switch("WindowSizeCache", "render", "drawing", () => WindowSizeCache.Enabled,
            on => WindowSizeCache.Enabled = on),
        Switch("MeshPool", "render", "upload", () => MeshPool.Enabled, on => MeshPool.Enabled = on, "PoolFragments"),
        Switch("MeshRecycle", "render", "upload", () => MeshRecycle.Enabled, on => MeshRecycle.Enabled = on),
        new("UploadCap", "render", "upload", ChunkBudget.Uncapped, ChunkBudget.MaxCapMillis, ChunkBudget.Uncapped,
            () => ChunkBudget.CapMillis, v => ChunkBudget.CapMillis = v) { Unit = "ms" },
        Switch("ChunkLookup", "chunks", "arrival", () => ChunkLookup.Enabled, on => ChunkLookup.Enabled = on),
        Switch("DecompressScratch", "chunks", "arrival", () => DecompressScratch.Enabled,
            on => DecompressScratch.Enabled = on),
        Switch("TessSchedule", "chunks", "tessellation", () => TessSchedule.Enabled, on => TessSchedule.Enabled = on,
            "NearFirst"),
        Switch("ExtendedRows", "chunks", "tessellation", () => ExtendedRows.Enabled, on => ExtendedRows.Enabled = on),
        Switch("VisibleFaces", "chunks", "tessellation", () => VisibleFaces.Enabled, on => VisibleFaces.Enabled = on),
        Switch("FaceLight", "chunks", "tessellation", () => FaceLight.Enabled, on => FaceLight.Enabled = on),
        Switch("OccludedChunks", "chunks", "tessellation", () => OccludedChunks.Enabled,
            on => OccludedChunks.Enabled = on),
        new("WorkerThreads", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => WorkerPool.Wanted,
            v => WorkerPool.Wanted = v),
        new("TessJobs", "chunks", "threads", 0, WorkerPool.MaxThreads, 0, () => TessWorkers.Jobs,
            v => TessWorkers.Jobs = v),
        Switch("LightScratch", "chunks", "light", () => LightScratch.Enabled, on => LightScratch.Enabled = on),
        Switch("ParticleLight", "chunks", "light", () => ParticleLight.Enabled, on => ParticleLight.Enabled = on),
        Switch("AnimationFrames", "misc", "entities", () => AnimationFrames.Enabled,
            on => AnimationFrames.Enabled = on),
        Switch("InitOnce", "misc", "entities", () => InitOnce.Enabled, on => InitOnce.Enabled = on),
        Switch("ShapeInitMemo", "misc", "entities", () => ShapeInitMemo.Enabled, on => ShapeInitMemo.Enabled = on),
        new("EntityTessBudget", "misc", "entities", EntityTessBudget.Engine, EntityTessBudget.MaxMillis,
            EntityTessBudget.Engine,
            () => EntityTessBudget.Millis, v => EntityTessBudget.Millis = v) { Unit = "ms" },
        Switch("ClimateCache", "misc", "garbage", () => ClimateCache.Enabled, on => ClimateCache.Enabled = on),
        Switch("ColumnNoiseScratch", "misc", "garbage", () => ColumnNoiseScratch.Enabled,
            on => ColumnNoiseScratch.Enabled = on),
        Switch("CloudTileScratch", null, null, () => CloudTileScratch.Enabled, on => CloudTileScratch.Enabled = on),
        Switch("PreJit", "misc", "garbage", () => PreJit.Enabled, on => PreJit.Enabled = on)
    ];

    public static int Count => Table.Length;

    public static ReadOnlySpan<Knob> All => Table;

    public static string Name(int knob)
    {
        return Index(knob, Table.Length) && Assert(Table.Length <= MaxKnobs) ? Table[knob].Key : "";
    }

    public static int Find(string key)
    {
        if (!NotNull(key) || !Assert(Table.Length <= MaxKnobs)) return -1;
        for (var i = 0; i < Math.Min(Table.Length, MaxKnobs); i++)
            if (string.Equals(Table[i].Key, key, StringComparison.Ordinal))
                return i;
        return -1;
    }

    public static bool InRange(int knob, int value)
    {
        return Index(knob, Table.Length) && Assert(Table[knob].Min <= Table[knob].Max) && value >= Table[knob].Min &&
               value <= Table[knob].Max;
    }

    public static int[] Snapshot()
    {
        var values = new int[Table.Length];
        if (!Assert(values.Length <= MaxKnobs)) return values;
        for (var i = 0; i < Math.Min(Table.Length, MaxKnobs); i++) values[i] = Table[i].Get();
        return values;
    }

    public static int[] EngineValues()
    {
        var values = new int[Table.Length];
        if (!Assert(values.Length <= MaxKnobs)) return values;
        for (var i = 0; i < Math.Min(Table.Length, MaxKnobs); i++) values[i] = Table[i].Engine;
        return values;
    }

    // Only what differs is written: ShaderUseCache.Enabled = true empties its cache even when it already was true, and a lap that
    // starts with a cold cache in one arm only would measure the toggle instead of the feature.
    public static int Apply(ReadOnlySpan<int> values)
    {
        if (!Assert(values.Length == Table.Length) || !Assert(values.Length <= MaxKnobs)) return 0;
        var changed = 0;
        for (var i = 0; i < Math.Min(values.Length, MaxKnobs); i++)
        {
            if (!InRange(i, values[i]) || Table[i].Get() == values[i]) continue;
            Table[i].Set(values[i]);
            changed++;
        }

        return changed;
    }

    private static Knob Switch(string key, string? page, string? group, Func<bool> get, Action<bool> set,
        string? json = null)
    {
        return Assert(key.Length > 0) && NotNull(get) && NotNull(set)
            ? new Knob(key, page, group, 0, 1, 0, () => get() ? 1 : 0, value => set(value != 0), json)
            : throw new ArgumentException("knob without a name", nameof(key));
    }
}
