using System.Runtime.CompilerServices;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Test.Rigs;

// A ChunkTesselator without a game: only the arrays CalculateVisibleFaces and CalcBlockFaceLight read - the 34^3 halo of solid and
// fluid blocks and light, the draw buffer, blocksFast and tmpPos - and TileSideEnum.MoveIndex as ChunkTesselator.Start sets it.
// Everything else stays null, which the engine's paths under test never touch.
internal sealed class TessRig : IDisposable
{
    public const int Ext = TessSeams.Ext,
        ExtCells = TessSeams.ExtCells,
        Cells = TessSeams.Cells,
        Plane = TessSeams.Plane;

    public static readonly int[] Moves = TessSeams.Moves;

    private readonly int[] _moves;

    public TessRig(Block[] palette)
    {
        _moves = [.. TileSideEnum.MoveIndex];
        Moves.CopyTo(TileSideEnum.MoveIndex, 0);
        Tesselator = (ChunkTesselator)RuntimeHelpers.GetUninitializedObject(typeof(ChunkTesselator));
        Solid = TessSeams.BlocksExt(Tesselator) = new Block[ExtCells];
        Fluid = TessSeams.FluidsExt(Tesselator) = new Block[ExtCells];
        Rgb = TessSeams.RgbsExt(Tesselator) = new int[ExtCells];
        TessSeams.Draw(Tesselator) = new byte[Cells];
        TessSeams.BlocksFast(Tesselator) = palette;
        Pos = TessSeams.TmpPos(Tesselator) = new BlockPos(0);
        Air = palette[0];
        Vars = new TCTCache(Tesselator);
    }

    public ChunkTesselator Tesselator { get; }
    public TCTCache Vars { get; }
    public Block[] Solid { get; }
    public Block[] Fluid { get; }
    public int[] Rgb { get; }
    public BlockPos Pos { get; }
    public Block Air { get; }

    public byte[] DrawBuffer
    {
        get => TessSeams.Draw(Tesselator)!;
        set => TessSeams.Draw(Tesselator) = value;
    }

    public void Dispose()
    {
        _moves.CopyTo(TileSideEnum.MoveIndex, 0);
    }

    // The engine's internal methods, called through their entry: patched when a test patched them
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CalcBlockFaceLight")]
    public static extern long CalcBlockFaceLight(TCTCache vars, int tileSide, int extNeibIndex3D);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "neighbourLightRGBS")]
    public static extern ref int[] Neighbours(TCTCache vars);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jsonLightRGB")]
    public static extern ref int[] JsonLight(JsonTesselator tesselator);
}

// Deterministic answers for the recording blocks: the same call gets the same answer in both runs
internal static class TessMix
{
    public static int Hash(int x, int y, int z)
    {
        var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791);
        h ^= h >> 13;
        h *= 0x5BD1E995u;
        return (int)((h ^ (h >> 15)) & 0x7FFFFFFF);
    }

    public static bool Bit(params int[] values)
    {
        var h = 0x9E3779B9u;
        foreach (var v in values) h = ((h ^ (uint)v) * 0x85EBCA6Bu) ^ (h >> 13);
        return ((h ^ (h >> 16)) & 1) != 0;
    }
}
