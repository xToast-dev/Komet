using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// ChunkTesselator.CalculateVisibleFaces stores which of the six faces of every cell are drawn (bits N, E, S, W, U, D; 0x40 for
// JSONAndWater), running a loop over the six faces with an 11-way switch on the cell's FaceCullMode per non-air cell. For Default -
// almost every terrain block - that loop reduces to one bit formula over six field reads: the faces whose neighbour is not opaque
// toward the cell, plus, unless the cell is JSON or JSONAndSnowLayer, the faces the cell itself is not opaque on, plus 0x40 for
// JSONAndWater. The sweep computes that without a call or a branch per face. Every other cell - another mode, or a cell whose upper
// neighbour is JSONAndSnowLayer and opaque downward, for which the engine asks the cell's AllowSnowCoverage - goes through a
// line-for-line port of the engine's face loop, run for those cells after the sweep in the engine's cell order, so the same virtual
// calls reach the same blocks with the same arguments in the same order. An unknown mode sends the chunk to the engine before any
// call is made; the engine then rewrites every byte the sweep wrote.
//
// What stays exact: air writes 0 even in the skipped centre; a non-air centre cell under skipChunkCenter keeps the previous chunk's
// byte, as in the engine; the result is whether a non-air cell outside the skipped centre exists.
internal static class VisibleFaces
{
    private const int Origin = TessSeams.Plane + Ext + 1,
        Up = 4,
        Down = 5,
        North = 0,
        West = 3,
        Water = 0x40,
        UpOpaqueDown = 32;

    private const int Snowy = 1 << 16; // a queued cell whose upper neighbour is JSONAndSnowLayer and opaque downward
    private const int Engine = -1;

    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x03182CA4EDCC4566UL;

    private const int ChunksCounter = 0, FastCounter = 1, PortedCounter = 2, FallbacksCounter = 3;

    // Totals while Counting.Hud, every tessellation thread
    private static readonly Tally Counts = new(FallbacksCounter + 1);

    [ThreadStatic] private static int[]? _deferred; // per thread, as every tessellation thread runs its own tesselator
    private static MethodBase?[] _target = [];

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // Another prefix, transpiler or infix is on the method: the engine runs
    public static bool StoodDown { get; private set; }

    public static long Chunks => Counts.Total(ChunksCounter); // chunks the sweep answered
    public static long FastCells => Counts.Total(FastCounter); // Default cells by the formula
    public static long PortedCells => Counts.Total(PortedCounter); // other modes and snow-covered cells by the port
    public static long Fallbacks => Counts.Total(FallbacksCounter); // chunks left to the engine: an unknown mode

    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape)
    {
        (Installed, StoodDown) = (false, false);
        var target = Target();
        if (!NotNull(harmony) || !NotNull(target) || !Accessible(nameof(VisibleFaces), logger) ||
            !EngineShape.Matches(Shaped(), shape, nameof(VisibleFaces), logger)) return;
        _target = [target];
        _ = NotNull(harmony.Patch(target, new HarmonyMethod(typeof(VisibleFaces), nameof(Prefix))));
        Installed = true;
        Recheck();
    }

    // A prefix, transpiler or infix on the method would be skipped with the original; a postfix sees the same result
    internal static void Recheck()
    {
        if (!Installed) return;
        StoodDown = Report(nameof(VisibleFaces), StoodDown,
            EngineShape.Foreign(_target, EngineShape.Kinds.Replacing, null, typeof(VisibleFaces)));
    }

    internal static MethodInfo? Target()
    {
        var method = Method(typeof(ChunkTesselator), nameof(ChunkTesselator.CalculateVisibleFaces), typeof(bool),
            typeof(int), typeof(int),
            typeof(int));
        return NotNull(method) && Assert(method.ReturnType == typeof(bool)) ? method : null;
    }

    // The bodies the sweep and the port reproduce: the method, the opposite side, and SmallBoolArray's indexer and int conversion
    internal static MethodBase?[] Shaped()
    {
        var bits = typeof(SmallBoolArray);
        MethodBase?[] methods =
        [
            Target(), Method(typeof(TileSideEnum), nameof(TileSideEnum.GetOpposite), typeof(int)),
            AccessTools.DeclaredPropertyGetter(bits, "Item"), Method(bits, "op_Implicit", bits)
        ];
        _ = Assert(methods.Length <= EngineShape.MaxMethods) &&
            Assert(bits.IsValueType); // a missing one fingerprints as 0
        return methods;
    }

    // Harmony matches the parameters to the engine's by name
    private static bool Prefix(ChunkTesselator __instance, bool skipChunkCenter, int baseX, int baseY, int baseZ,
        ref bool __result)
    {
        if (!Enabled || StoodDown || !NotNull(__instance)) return true;
        var result = Compute(__instance, Draw(__instance), skipChunkCenter, baseX, baseY, baseZ);
        if (result == Engine || !Assert(result is 0 or 1)) return true;
        __result = result > 0;
        return false;
    }

    // 1 or 0 for the engine's result with the buffer filled as the engine fills it, or -1 when the engine has to do the chunk: then
    // nothing was called on any block, and the buffer holds only bytes the engine writes again
    internal static int Compute(ChunkTesselator tesselator, byte[]? draw, bool skip, int baseX, int baseY, int baseZ)
    {
        if (!NotNull(tesselator)) return Engine;
        var (blocks, palette, pos, game) = (BlocksExt(tesselator), BlocksFast(tesselator), TmpPos(tesselator),
            Game(tesselator));
        if (blocks is not { Length: ExtCells } || draw is not { Length: Cells } || palette is not { Length: > 0 } ||
            pos is null ||
            !Standard(TileSideEnum.MoveIndex)) return Engine; // what the engine would index or throw on, it does itself
        var deferred = _deferred ??= new int[Cells];
        if (!Sweep(blocks, palette[0], draw, skip, deferred, out var any, out var fast, out var count))
        {
            if (Counting.Hud) Counts.Add(FallbacksCounter, 1);
            return Engine;
        }

        var port = new Port(blocks, game, pos);
        for (var i = 0; i < Math.Min(count, Cells); i++)
        {
            var (d, snowy) = (deferred[i] & (Snowy - 1), (deferred[i] & Snowy) != 0);
            var (x, y, z) = (d & 31, d >> 10, (d >> 5) & 31);
            draw[d] = port.Cell((y * Ext + z) * Ext + Origin + x, d, snowy, baseX + x, baseY + y, baseZ + z);
        }

        if (Counting.Hud) Count(fast, count);
        return any ? 1 : 0;
    }

    private static void Count(int fast, int ported)
    {
        if (!Assert(fast >= 0) || !Assert(ported >= 0)) return;
        Counts.Add(ChunksCounter, 1);
        Counts.Add(FastCounter, fast);
        Counts.Add(PortedCounter, ported);
    }

    // The engine's cell loop with the Default cells answered in place and the others queued for the port, in the engine's order. False
    // when the engine has to do the chunk, found before anything was called.
    private static bool Sweep(Block[] blocks, Block air, byte[] draw, bool skip, int[] deferred, out bool any,
        out int fast,
        out int count)
    {
        (any, fast, count) = (false, 0, 0);
        if (!Assert(blocks.Length == ExtCells) || !Assert(draw.Length == Cells) ||
            !Assert(deferred.Length == Cells)) return false;
        var row = new Row(blocks, air, draw, deferred);
        for (var y = 0; y < Size; y++)
        {
            for (var z = 0; z < Size; z++)
            {
                var centre = skip && y * (y ^ 31) * z * (z ^ 31) != 0; // the engine's centre test: 1 <= y, z <= 30
                if (!row.Walk((y * Ext + z) * Ext + Origin, (y * Size + z) * Size, centre)) return false;
            }
        }

        (any, fast, count) = (row.Any, row.Fast, row.Deferred);
        return Assert(fast + count <= Cells);
    }

    // One row of 32 cells along x. A row starts at least one plane and one row inside the halo, whose length Sweep checked, so every
    // neighbour index is within it: the reads skip the bounds checks. The east and west sides roll along the row, each cell's
    // opacity read once.
    private ref struct Row(Block[] blocks, Block air, byte[] draw, int[] deferred)
    {
        private readonly ref Block _blocks = ref MemoryMarshal.GetArrayDataReference(blocks);
        private readonly ref byte _draw = ref MemoryMarshal.GetArrayDataReference(draw);
        private readonly ref int _queue = ref MemoryMarshal.GetArrayDataReference(deferred);

        public bool Any { get; private set; }
        public int Fast { get; private set; }
        public int Deferred { get; private set; }

        public bool Walk(int e, int d, bool centre)
        {
            if (!Index(e - Origin, ExtCells - 2 * Origin) || !Index(d, Cells - Size + 1)) return false;
            var cur = Unsafe.Add(ref _blocks, e);
            var (west, self) = ((int)Unsafe.Add(ref _blocks, e - 1).SideOpaque, (int)cur.SideOpaque);
            for (var x = 0; x < Size; x++, e++, d++)
            {
                var block = cur;
                cur = Unsafe.Add(ref _blocks, e + 1);
                var (w, own, east) = (west, self, (int)cur.SideOpaque);
                (west, self) = (own, east);
                if (ReferenceEquals(block, air))
                {
                    Unsafe.Add(ref _draw, d) = 0;
                    continue;
                }

                if (centre && x * (x ^ 31) != 0) continue;
                Any = true;
                var up = Unsafe.Add(ref _blocks, e + TessSeams.Plane);
                var above = (int)up.SideOpaque;
                var snowy = up.DrawType == EnumDrawType.JSONAndSnowLayer &&
                            (above & UpOpaqueDown) != 0; // AllowSnowCoverage decides
                var mode = block.FaceCullMode;
                if (mode == EnumFaceCullMode.Default && !snowy)
                {
                    var toward = ((Unsafe.Add(ref _blocks, e - Ext).SideOpaque >> 2) & 1) | ((east >> 2) & 2) |
                                 ((Unsafe.Add(ref _blocks, e + Ext).SideOpaque << 2) & 4) | ((w << 2) & 8) |
                                 ((above >> 1) & 16) |
                                 ((Unsafe.Add(ref _blocks, e - TessSeams.Plane).SideOpaque << 1) & 32);
                    Unsafe.Add(ref _draw, d) = Cull(toward, own, block.DrawType);
                    Fast++;
                }
                else if (Ported(mode) && Deferred < Cells)
                {
                    Unsafe.Add(ref _queue, Deferred++) = snowy ? d | Snowy : d;
                }
                else
                {
                    return false; // an unknown mode is the engine's
                }
            }

            return true;
        }

        // The modes of the engine's switch. A Default cell only reaches the port under a snowy neighbour.
        private static bool Ported(EnumFaceCullMode mode)
        {
            return mode is EnumFaceCullMode.Default or EnumFaceCullMode.NeverCull or EnumFaceCullMode.Merge
                or EnumFaceCullMode.Collapse or
                EnumFaceCullMode.MergeMaterial or EnumFaceCullMode.CollapseMaterial or EnumFaceCullMode.Liquid or
                EnumFaceCullMode.Callback or EnumFaceCullMode.MergeSnowLayer or EnumFaceCullMode.FlushExceptTop
                or EnumFaceCullMode.Stairs;
        }

        // Default: bit s is set when the neighbour on side s is not opaque toward the cell (toward: bit s is the neighbour's side
        // GetOpposite(s)), or when the cell is not opaque on side s and not JSON or JSONAndSnowLayer
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte Cull(int toward, int own, EnumDrawType type)
        {
            var faces = type is EnumDrawType.JSON or EnumDrawType.JSONAndSnowLayer ? 0 : ~own & 63;
            return (byte)((~toward & 63) | faces | (type == EnumDrawType.JSONAndWater ? Water : 0));
        }
    }

    // ChunkTesselator.CalculateVisibleFaces for the queued cells: faces 5 to 0, shift then set, the snow rule on the top face, then the
    // switch - the same reads and virtual calls with the same arguments, on the tesselator's game and tmpPos
    private readonly struct Port(Block[] blocks, ClientMain? game, BlockPos pos)
    {
        public byte Cell(int e, int d, bool snowy, int x, int y, int z)
        {
            if (!Index(e - Origin, ExtCells - 2 * Origin) || !Index(d, Cells))
                return 0; // Compute derives both from a cell of the chunk
            var block = blocks[e];
            var (mode, opaque, faces) =
                (block.FaceCullMode, block.SideOpaque, 0); // read once per cell, as the engine does
            for (var n = 0; n < Faces; n++)
            {
                var s = Faces - 1 - n;
                faces <<= 1;
                var neighbour = blocks[e + Moves[s]];
                var opposite = TileSideEnum.GetOpposite(s);
                var toward = neighbour.SideOpaque[opposite];
                if (s == Up && snowy && !block.AllowSnowCoverage(game, pos.Set(x, y, z)))
                    toward = false; // the sweep tested the rule
                if (Drawn(mode, opaque, block, neighbour, toward, e, d, s, opposite, (x, y, z))) faces++;
            }

            return (byte)(block.DrawType == EnumDrawType.JSONAndWater ? faces | Water : faces);
        }

        // The engine's switch on the cell's mode for side s
        private bool Drawn(EnumFaceCullMode mode, SmallBoolArray opaque, Block block, Block neighbour, bool toward,
            int e, int d, int s,
            int opposite, (int X, int Y, int Z) at)
        {
            var same = ReferenceEquals(neighbour, block);
            return mode switch
            {
                EnumFaceCullMode.Default => !toward ||
                                            (!opaque[s] &&
                                             block.DrawType is not (EnumDrawType.JSON
                                                 or EnumDrawType.JSONAndSnowLayer)),
                EnumFaceCullMode.NeverCull => true,
                EnumFaceCullMode.Merge => !same && (!opaque[s] || !toward),
                EnumFaceCullMode.Collapse => (same && s is Up or North or West) || (!same && (!opaque[s] || !toward)),
                EnumFaceCullMode.MergeMaterial => !block.SideSolid[s] ||
                                                  (neighbour.BlockMaterial != block.BlockMaterial &&
                                                   (!opaque[s] || !toward)) ||
                                                  !neighbour.SideSolid[opposite],
                EnumFaceCullMode.CollapseMaterial => neighbour.BlockMaterial == block.BlockMaterial
                    ? s is North or West
                    : !toward || (s < Up && !opaque[s]),
                EnumFaceCullMode.Liquid => Liquid(block, neighbour, s, opposite, at.X, at.Y, at.Z),
                EnumFaceCullMode.Callback => !block.ShouldMergeFace(s, neighbour, d),
                EnumFaceCullMode.MergeSnowLayer => MergeSnowLayer(block, neighbour, toward, e, s, at.X, at.Y, at.Z),
                EnumFaceCullMode.FlushExceptTop => s == Up || ((s == Down || !same) && !toward),
                EnumFaceCullMode.Stairs => (!toward && (!same || block.SideOpaque[s])) || s == Up,
                _ => !Assert(false) // Sweep defers only the modes above
            };
        }

        // Nothing between the same material, always the top, else what the neighbour's SideIsSolid says at its position
        private bool Liquid(Block block, Block neighbour, int s, int opposite, int x, int y, int z)
        {
            if (!Index(s, Faces) || !Index(opposite, Faces) || neighbour.BlockMaterial == block.BlockMaterial)
                return false;
            if (s == Up) return true;
            var offset = TileSideEnum.OffsetByTileSide[s];
            return !neighbour.SideIsSolid(pos.Set(x + offset.X, y + offset.Y, z + offset.Z), opposite);
        }

        // Short-circuited as the engine's: always the top; a neighbour not opaque toward the cell when it is below or holds less snow
        // (the neighbour's level asked first); or a JSONAndSnowLayer neighbour whose own lower cell - if within the halo - does not
        // allow snow cover at the cell's position
        private bool MergeSnowLayer(Block block, Block neighbour, bool toward, int e, int s, int x, int y, int z)
        {
            if (!Index(s, Faces) || !Index(e - Origin, ExtCells - 2 * Origin) || s == Up) return true;
            if (!toward && (s == Down || neighbour.GetSnowLevel(null) < block.GetSnowLevel(null))) return true;
            var below = e + Moves[s] - TessSeams.Plane;
            return neighbour.DrawType == EnumDrawType.JSONAndSnowLayer && below >= 0 && below < blocks.Length &&
                   !blocks[below].AllowSnowCoverage(game, pos.Set(x, y, z));
        }
    }
}
