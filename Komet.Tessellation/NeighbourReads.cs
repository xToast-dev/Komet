using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Tessellation;

// BuildExtendedChunkData unpacks the chunk and its 26 neighbours with Unpack(), which drops their packed bytes and marks
// them changed, though it only reads them: 8 s later SystemCompressChunks packs each anew. Standing in a loaded world
// the server's block updates (snow, growth, block entities: ~50 retessellations a second) kept doing that, and the new
// packed arrays were half of what outlived each gen0 collection (trace 2026-10-28), so every pause copied them. Read
// with Unpack_ReadOnly, an unchanged chunk keeps its bytes and Pack reuses them. Writers call Unpack() themselves
// (SetBlock, ExchangeBlock, the illuminator); a chunk marked dirty (SetChunkDirty finishes buffered light into it,
// MarkChunkDirty follows block changes) is marked changed here as well, so it is packed anew.
// With PackedChunks a packed chunk is not unpacked at all: TesselateChunk leaves the chunk packed and the build reads it
// and its neighbours through ChunkViews (decoded into the tessellation thread's own data, its planes reused), so a
// chunk that is only drawn never holds planes of its own. The build's two Data reads take the view (all or none).
internal static class NeighbourReads
{
    private const int MaxInstructions = 4096, DataSites = 2, MaxGiven = 28;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PotentialBlockOrLightingChanges")]
    private static extern ref bool Changed(WorldChunk chunk);

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }
    public static bool Packed { get; set; } = true; // PackedChunks' switch
    public static bool Viewed { get; private set; } // the build's Data reads and TesselateChunk's unpack rewritten

    // Both rewrites as they are now (Harmony reruns them when another mod patches either method)
    private static bool Viewing => Enabled && Packed && Viewed && Volatile.Read(ref _dataSites) == DataSites;

    // What the build's Data reads gave, per chunk: the centre's second read (curChunk.Data) gets the same data its
    // fast block table was built on, though a writer may have unpacked the chunk in between
    [ThreadStatic] private static List<(WorldChunk Chunk, IChunkBlocks? Data)>? _given;

    internal static MethodBase? Target() =>
        AccessTools.DeclaredMethod(typeof(ChunkTesselator), "BuildExtendedChunkData",
            [typeof(ClientChunk), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool)]);

    internal static MethodBase? Pass() =>
        AccessTools.DeclaredMethod(typeof(ChunkTesselatorManager), nameof(ChunkTesselatorManager.TesselateChunk),
            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool).MakeByRefType()]);

    public static void Install(Harmony harmony)
    {
        (Rewritten, Viewed, _dataSites) = (false, false, 0);
        if (!NotNull(harmony) || Target() is not MethodInfo build ||
            AccessTools.DeclaredField(typeof(WorldChunk), "PotentialBlockOrLightingChanges")?.FieldType != typeof(bool)) return;
        var marking = new HarmonyMethod(AccessTools.Method(typeof(NeighbourReads), nameof(Marking)));
        var setting = new HarmonyMethod(AccessTools.Method(typeof(NeighbourReads), nameof(Setting)));
        var mark = AccessTools.DeclaredMethod(typeof(ClientWorldMap), nameof(ClientWorldMap.MarkChunkDirty),
            [typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(Action), typeof(bool), typeof(bool)]);
        var set = AccessTools.DeclaredMethod(typeof(ClientWorldMap), nameof(ClientWorldMap.SetChunkDirty),
            [typeof(long), typeof(bool), typeof(bool), typeof(bool)]);
        if (mark is null || set is null) return; // no safety net, no read-only reads
        _ = harmony.Patch(mark, marking);
        _ = harmony.Patch(set, setting);
        _ = harmony.Patch(build, new HarmonyMethod(AccessTools.Method(typeof(NeighbourReads), nameof(Building))),
            transpiler: new HarmonyMethod(AccessTools.Method(typeof(NeighbourReads), nameof(Rewrite))));
        // The pass leaves the chunk packed only when the build reads it through views
        if (!Rewritten || Volatile.Read(ref _dataSites) != DataSites || !ChunkViews.Usable || Pass() is not MethodInfo pass) return;
        _ = harmony.Patch(pass, transpiler: new HarmonyMethod(AccessTools.Method(typeof(NeighbourReads), nameof(RewritePass))));
    }

    private static int _dataSites;

    // The one Unpack() becomes Read, both chunk Data reads become Data
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var unpack = AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.Unpack), []);
        var data = AccessTools.PropertyGetter(typeof(WorldChunk), nameof(WorldChunk.Data));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(unpack) || !NotNull(data)) return code;
        Rewritten = Il.SubstituteSingle(code, c => c.operand is MethodInfo m && m == unpack, AccessTools.Method(typeof(NeighbourReads), nameof(Read)));
        _dataSites = 0;
        if (!Rewritten || Il.Count(code, c => c.Calls(data)) != DataSites) return code;
        // in IL order: the neighbour loop's read, then curChunk.Data after it
        var (mine, last) = (AccessTools.Method(typeof(NeighbourReads), nameof(Data)),
            AccessTools.Method(typeof(NeighbourReads), nameof(Last)));
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(data) && Il.Substitute(code, i, _dataSites == 0 ? mine : last)) _dataSites++;
        _ = Assert(_dataSites == DataSites);
        return code;
    }

    // TesselateChunk's Unpack_ReadOnly of the chunk becomes Center
    internal static List<CodeInstruction> RewritePass(IEnumerable<CodeInstruction> instructions)
    {
        var read = AccessTools.DeclaredMethod(typeof(WorldChunk), nameof(WorldChunk.Unpack_ReadOnly), []);
        Viewed = false;
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(read)) return code;
        Viewed = Il.SubstituteSingle(code, c => c.Calls(read), AccessTools.Method(typeof(NeighbourReads), nameof(Center)));
        return code;
    }

    internal static void Read(WorldChunk chunk)
    {
        if (Viewing && ChunkViews.Open(chunk) is not null) return;
        if (Enabled) _ = chunk.Unpack_ReadOnly();
        else chunk.Unpack();
    }

    // The build's chunk.Data: its view while packed, else the chunk's own (unpacked as the engine would if a view fails)
    // (also when the switch went off since Read: a chunk Read left packed is unpacked here)
    internal static IChunkBlocks? Data(WorldChunk chunk)
    {
        var given = _given ??= [];
        foreach (var (owner, data) in given.Bounded(MaxGiven))
            if (ReferenceEquals(owner, chunk)) return data;
        var read = Viewing ? ChunkViews.Open(chunk) : null;
        if (read is null && chunk.Data is null) _ = chunk.Unpack_ReadOnly();
        IChunkBlocks? result = read ?? chunk.Data;
        if (given.Count < MaxGiven) given.Add((chunk, result));
        return result;
    }

    // The build's last Data read (curChunk.Data): as Data, then the list lets go, so a pool thread holds no chunk of a
    // left world until it builds again
    internal static IChunkBlocks? Last(WorldChunk chunk)
    {
        var result = Data(chunk);
        _given?.Clear();
        return result;
    }

    // BuildExtendedChunkData prefix: a new build, nothing given yet
    private static void Building() => _given?.Clear();

    // In place of TesselateChunk's chunk.Unpack_ReadOnly(): a packed chunk stays packed (true: it was packed)
    internal static bool Center(WorldChunk chunk) => (Viewing && ChunkViews.Viewable(chunk)) || chunk.Unpack_ReadOnly();

    // MarkChunkDirty prefix
    private static void Marking(ClientWorldMap __instance, int cx, int cy, int cz)
    {
        if (__instance.IsValidChunkPos(cx, cy, cz)) Touched(__instance.GetChunk(__instance.ChunkIndex3D(cx, cy, cz)) as WorldChunk);
    }

    // SetChunkDirty prefix
    private static void Setting(ClientWorldMap __instance, long index3d) => Touched(__instance.GetChunk(index3d) as WorldChunk);

    // Changed, and touched now: a pack begun before this is not committed within the chunk's time
    private static void Touched(WorldChunk? chunk)
    {
        if (chunk is null) return;
        Changed(chunk) = true;
        chunk.lastReadOrWrite = Environment.TickCount;
    }
}
