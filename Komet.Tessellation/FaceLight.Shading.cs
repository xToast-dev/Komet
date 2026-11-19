using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// FaceLight: gathering a block's neighbours and shading its faces with SIMD.
internal static partial class FaceLight
{
    // The 20 cells the faces sample, each read once; a block that is not plain marks its cell
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Gather(Tables tables, Block[] solid, Block[] fluid, int[] rgb, int e, ref Block27 desc,
        ref Block27 light)
    {
        var (cells, kinds) = (tables.Geometry.CellList, tables.Kinds);
        Block? known = null;
        for (var i = 0; i < Math.Min(cells.Length, Around); i++)
        {
            var cell = cells[i];
            var p = e + Geometry.OffsetOf(cell);
            var block = solid[p];
            var custom = 0;
            if (!ReferenceEquals(block, known)) (known, custom) = Plain(kinds, block) ? (block, 0) : (known, Custom);

            (desc[cell], light[cell]) = (Sample(block, fluid[p]) | custom, rgb[p]);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Sample(Block solid, Block fluid) =>
        solid.EmitSideAo |
        (solid.BlockMaterial == EnumBlockMaterial.Leaves ? Leaves : 0) |
        (fluid.LightAbsorption > 0 ? Absorbs : 0);

    // Front cell: its fluid when that absorbs light, else its solid block, as the engine picks it
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Front(Entry[] kinds, Block solid, Block fluid)
    {
        var absorbs = fluid.LightAbsorption > 0;
        var block = absorbs ? fluid : solid;
        var id = block.BlockId;
        if ((uint)id >= (uint)kinds.Length || !ReferenceEquals(kinds[id].Owner, block)) return Custom;

        var fluids = kinds[id].FluidsLayer && block.LightAbsorption > 0;
        return block.EmitSideAo | (absorbs ? Absorbs : 0) | (fluids ? FluidsAbsorb : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Plain(Entry[] kinds, Block block)
    {
        var id = block.BlockId;
        return (uint)id < (uint)kinds.Length && ReferenceEquals(kinds[id].Owner, block);
    }

    // CornerAoRGB multipliers: [0] full occlusion min(occ, 1 - halfoccInverted * clamp(LightAbsorption, 0, 32)), [1..3]
    // occ / 2, 3, 4 for 2-4 lights, [4] 1f / 4 for front ambient 1. False unless all in [0, MaxFactor].
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Multipliers(TCTCache vars, Block self, out Floats5 factors)
    {
        var quotients = Divisions(vars.occ);
        factors = default;
        var full = Math.Min(vars.occ, 1f - vars.halfoccInverted * GameMath.Clamp(self.LightAbsorption, 0, 32));
        (factors[0], factors[1], factors[2], factors[3], factors[4]) =
            (full, quotients.Half, quotients.Third, quotients.Fourth, Quarter);
        return quotients.Bounded && full is >= 0 and <= MaxFactor && Assert(Vector128.IsHardwareAccelerated);
    }

    // Engine's 8-sample loop (TCTCache.cs:198-274) and decode (275-300) on four lanes: edges (samples 0-3) and corner
    // samples (4-7) one Vector128 each, lanes = face corners; set bit = side doesn't occlude. Corner 0: 0's
    // lower-or-right side (upper-or-left for up/down), 2's upper-or-left, sample 4 with its ambient and 7's front flag,
    // 1: other side of 0, 3's upper-or-left, 5, 6's flag; 2: 1's lower-or-right (upper-or-left), 2's lower-or-right, 6,
    // 5's flag; 3: other side of 1, 3's lower-or-right, 7, 4's flag.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long Shade(ref Side side, int t, int leaves, int front, int frontLight, ref Cells cells,
        ref Floats5 factors, int[] corners, int[] neighbours)
    {
        if (!Assert(corners.Length >= Edges) || !Assert(neighbours.Length > Samples)) return 0;
        var (zero, absorbs, leaf) = (Vector128<int>.Zero, Vector128.Create(Absorbs), Vector128.Create(leaves));
        var (frontV, own) = (Vector128.Create(front), Vector128.Create(frontLight));
        var edges = Vector128.LoadUnsafe(ref cells.Desc[0]);
        var shown = Vector128.Equals(edges & absorbs, zero); // no light-absorbing fluid over the sample
        var first = shown &
                    ~Vector128.Equals(edges & (Vector128.LoadUnsafe(ref side.First[0]) | leaf), zero); // occludes
        var second = shown & ~Vector128.Equals(edges & (Vector128.LoadUnsafe(ref side.Second[0]) | leaf), zero);
        var edgeLights = Vector128.LoadUnsafe(ref cells.Rgb[0]);
        var cornerLights = Vector128.LoadUnsafe(ref cells.Rgb[Edges]);
        Vector128.ConditionalSelect(first & second, own, edgeLights).StoreUnsafe(ref neighbours[1]);
        var cornerDesc = Vector128.LoadUnsafe(ref cells.Desc[Edges]);
        var cornerShown = Vector128.Equals(cornerDesc & absorbs, zero);
        var occludes = cornerShown &
                       ~Vector128.Equals(cornerDesc & (Vector128.LoadUnsafe(ref side.First[Edges]) | leaf), zero);
        var emits = ~Vector128.Equals(frontV & Vector128.LoadUnsafe(ref side.Second[Edges]), zero);
        Vector128.ConditionalSelect(occludes, own, cornerLights).StoreUnsafe(ref neighbours[1 + Edges]);
        var upperLeft = (int)~first.ExtractMostSignificantBits() & 15; // set: the side does not occlude
        var lowerRight = (int)~second.ExtractMostSignificantBits() & 15;
        var open = (int)~occludes.ExtractMostSignificantBits() & 15;
        var dark = (int)~emits.ExtractMostSignificantBits() & 15;
        var hidden = (int)~cornerShown.ExtractMostSignificantBits() & 15;
        var frontFlags = dark | ((front & FluidsAbsorb) != 0 ? 15 : 0);
        var ambientOne = ((~hidden & dark) | ((front & (Absorbs | side.Toward)) == 0 ? 15 : 0)) & 15;
        var side1 = t >= Up
            ? (upperLeft & 1) | ((lowerRight & 1) << 1) | ((upperLeft & 2) << 1) | ((lowerRight & 2) << 2)
            : (lowerRight & 1) | ((upperLeft & 1) << 1) | ((lowerRight & 2) << 1) | ((upperLeft & 2) << 2);
        var side2 = ((upperLeft >> 2) & 3) | (((lowerRight >> 2) & 3) << 2);
        var frontCorner = ((frontFlags & 8) >> 3) | ((frontFlags & 4) >> 1) | ((frontFlags & 2) << 1) |
                          ((frontFlags & 1) << 3);
        var keep = (side1 | side2) & (frontCorner | open);
        var (m1, m2, mb) = (Lanes(side1 & keep), Lanes(side2 & keep), Lanes(open & keep));
        var light1 = Vector128.Shuffle(edgeLights, Vector128.Create(0, 0, 1, 1));
        var light2 = Vector128.Shuffle(edgeLights, Vector128.Create(2, 3, 2, 3));
        var (channels, mask) = (Vector128.Create(Channels), Vector128.Create(0xFFFF));
        var low = (own & channels) + (light1 & channels & m1) + (light2 & channels & m2) +
                  (cornerLights & channels & mb);
        var high = (Vector128.ShiftRightLogical(own, 8) & channels) +
                   (Vector128.ShiftRightLogical(light1, 8) & channels & m1) +
                   (Vector128.ShiftRightLogical(light2, 8) & channels & m2) +
                   (Vector128.ShiftRightLogical(cornerLights, 8) & channels & mb);
        var added = -(Lanes(side1) + Lanes(side2) + Lanes(open));
        var factor = Vector128.ConditionalSelect(Vector128.Equals(added, Vector128<int>.One).AsSingle(),
            Vector128.Create(factors[1]), Vector128.Create(factors[2]));
        var ambient = Vector128.ConditionalSelect(Lanes(ambientOne).AsSingle(), Vector128.Create(factors[4]),
            Vector128.Create(factors[3]));
        factor = Vector128.ConditionalSelect(Lanes(side1 & side2 & open).AsSingle(), ambient, factor);
        factor = Vector128.ConditionalSelect(Lanes(keep).AsSingle(), factor, Vector128.Create(factors[0]));
        var result = (Scale(Vector128.ShiftRightLogical(high, 16), factor) << 24) |
                     (Scale(Vector128.ShiftRightLogical(low, 16), factor) << 16) |
                     (Scale(high & mask, factor) << 8) | Scale(low & mask, factor);
        result.StoreUnsafe(ref corners[0]);
        return (long)result.GetElement(0) + result.GetElement(1) + result.GetElement(2) + result.GetElement(3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Scale(Vector128<int> sums, Vector128<float> factor) =>
        Vector128.ConvertToInt32(Vector128.ConvertToSingle(sums) * factor);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Lanes(int bits)
    {
        var lane = Vector128.Create(1, 2, 4, 8);
        return Vector128.Equals(Vector128.Create(bits) & lane, lane);
    }

    // occ is a public field: the quotients are the engine's divisions of its current value, made again when it changes
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Quotients Divisions(float occ)
    {
        var quotients = Volatile.Read(ref _quotients);
        if (BitConverter.SingleToInt32Bits(quotients.Occ) == BitConverter.SingleToInt32Bits(occ)) return quotients;

        quotients = new Quotients(occ);
        Volatile.Write(ref _quotients, quotients);
        return quotients;
    }

    // The tesselator's halo: 34^3 cells in all three, as its constructor makes them
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool Arrays(ChunkTesselator? tesselator, [NotNullWhen(true)] out Block[]? solid,
        [NotNullWhen(true)] out Block[]? fluid, [NotNullWhen(true)] out int[]? rgb)
    {
        (solid, fluid, rgb) = (null, null, null);
        if (!NotNull(tesselator)) return false;

        (solid, fluid, rgb) = (BlocksExt(tesselator), FluidsExt(tesselator), RgbsExt(tesselator));
        return Assert(solid is { Length: ExtCells }) && Assert(fluid is { Length: ExtCells }) &&
               Assert(rgb is { Length: ExtCells });
    }

    // At world leave: the tables hold the world's blocks (and through them its api) until the next world's first build
    internal static void Forget() => Volatile.Write(ref _tables, null);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static Tables? Current(ChunkTesselator? tesselator)
    {
        if (!NotNull(tesselator)) return null;

        var (tables, blocks) = (Volatile.Read(ref _tables), BlocksFast(tesselator));
        if (!NotNull(blocks)) return null;

        if (tables is not null && ReferenceEquals(tables.Source, blocks)) return tables.Valid ? tables : null;

        tables = Tables.Build(blocks); // two threads may both build; either table is the same
        Volatile.Write(ref _tables, tables);
        return tables.Valid ? tables : null;
    }
}
