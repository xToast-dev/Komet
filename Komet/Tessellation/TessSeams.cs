using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;

namespace Komet.Tessellation;

// What the tesselation fast paths (ExtendedRows, VisibleFaces, FaceLight, OccludedChunks) share: the tesselator's 34^3 halo, its
// neighbour moves, the engine fields they read, and one schedule for their foreign-patch checks - at install, then at the start of
// the first tesselation pass and every RecheckMs after (another mod's patch added at runtime is honoured within that time)
internal static class TessSeams
{
    public const int Size = 32, Ext = 34, Plane = Ext * Ext, ExtCells = Ext * Ext * Ext, Cells = Size * Size * Size;
    public const int Faces = 6, RecheckMs = 2000;

    // TileSideEnum's sides, and the halo index of the chunk's first cell
    public const int North = 0, East = 1, South = 2, West = 3, Up = 4, Down = 5, Origin = Plane + Ext + 1;

    // TileSideEnum.MoveIndex as ChunkTesselator.Start sets it: north, east, south, west, up, down
    public static readonly int[] Moves = [-Ext, 1, Ext, -1, Plane, -Plane];

    private static long _nextCheck;

    // The mod's logger, for the features' stand-down notes (EngineShape.Report)
    internal static ILogger? Logger { get; private set; }

    public static void Install(Harmony harmony, ILogger? logger)
    {
        (Logger, _nextCheck) = (logger, 0);
        var pass = AccessTools.DeclaredMethod(typeof(ChunkTesselator), nameof(ChunkTesselator.NowProcessChunk));
        if (!NotNull(harmony) || !NotNull(pass)) return; // the features keep what they found at install
        _ = NotNull(harmony.Patch(pass, new HarmonyMethod(Pass) { priority = Priority.First }));
    }

    // Before any prefix that may skip the pass (OccludedChunks), on every tessellation thread; the one that moves the time on rechecks,
    // so no two rechecks run at once. The flags a recheck writes are single bools the passes read.
    private static void Pass()
    {
        var now = Environment.TickCount64;
        var due = Volatile.Read(ref _nextCheck);
        if (now < due || Interlocked.CompareExchange(ref _nextCheck, now + RecheckMs, due) != due) return;
        _ = Assert(now + RecheckMs > now);
        Recheck();
    }

    public static void Recheck()
    {
        ExtendedRows.Recheck();
        VisibleFaces.Recheck();
        FaceLight.Recheck();
        OccludedChunks.Recheck();
    }

    public static bool Standard(int[]? moves) =>
        NotNull(moves) && moves.Length == Faces && Assert(Moves.Length == Faces) && moves.AsSpan().SequenceEqual(Moves);

    // The fields the accessors below name, with their types: a renamed one would throw MissingFieldException on the tesselation thread
    public static bool Accessible(string feature, ILogger? logger)
    {
        var (tesselator, layer) = (typeof(ChunkTesselator), typeof(ChunkDataLayer));
        (Type Owner, string Name, Type Type)[] fields =
        [
            (tesselator, "currentChunkBlocksExt", typeof(Block[])),
            (tesselator, "currentChunkFluidBlocksExt", typeof(Block[])),
            (tesselator, "currentChunkRgbsExt", typeof(int[])), (tesselator, "currentChunkDraw32", typeof(byte[])),
            (tesselator, "blocksFast", typeof(Block[])), (tesselator, "tmpPos", typeof(BlockPos)),
            (tesselator, "game", typeof(ClientMain)),
            (layer, "dataBits", typeof(int[][])), (layer, "bitsize", typeof(int))
        ];
        foreach (var (owner, name, type) in fields.Bounded(Size))
        {
            if (AccessTools.DeclaredField(owner, name)?.FieldType == type) continue;
            logger?.Warning(
                "Komet {0}: the engine field {1}.{2} is missing or of another type, the engine's version runs", feature,
                owner.Name, name);
            return false;
        }

        return NotNull(feature);
    }

    // The method or null, for a list of bodies to fingerprint or of seams to watch (EngineShape counts a missing one as a mismatch)
    public static MethodInfo? Method(Type? type, string name, params Type[] parameters) =>
        type is null || !NotNull(name) ? null : AccessTools.DeclaredMethod(type, name, parameters);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkBlocksExt")]
    internal static extern ref Block[]? BlocksExt(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkFluidBlocksExt")]
    internal static extern ref Block[]? FluidsExt(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkRgbsExt")]
    internal static extern ref int[]? RgbsExt(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentChunkDraw32")]
    internal static extern ref byte[]? Draw(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blocksFast")]
    internal static extern ref Block[]? BlocksFast(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "tmpPos")]
    internal static extern ref BlockPos? TmpPos(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    internal static extern ref ClientMain? Game(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "started")]
    internal static extern ref bool Started(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dataBits")]
    internal static extern ref int[]?[]? DataBits(ChunkDataLayer layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "bitsize")]
    internal static extern ref int Bitsize(ChunkDataLayer layer);
}
