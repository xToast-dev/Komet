using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// CalcBlockFaceLight's AO for plain blocks. In vanilla only BlockMicroBlock overrides the AO virtuals and
// BlockForFluidsLayer makes ForFluidsLayer constant true, so a kind table by BlockId (holding the block against swaps)
// marks blocks using Block's own field tests; other blocks, or any face while a mod patches those methods, go to the
// engine. Decoding and float ops follow the engine, including the reversed corner front flags (corner 0: sample 7's
// flag, sample 4's ambient) and the multiplier quotient (occ or front ambient / 2, 3, 4, or full occlusion), each
// channel (int)((float)sum * multiplier); only plain faces write the engine's scratch. The fused SetUpLightRGBs reads
// the 26 cells once. Four Vector128 lanes repeat the engine's int and float steps (never fused): exact only for finite
// multipliers in [0, 16] (vanilla occ 0.67f), else, or without vector hardware, the engine.
internal static partial class FaceLight
{
    private const int Samples = 8, Edges = 4, Reach = TessSeams.Plane + Ext + 1, Around = 27, JsonLights = 25;
    private const int MaxBlocks = 1 << 20, MaxTypes = 1 << 16, Slots = Faces * Samples, AllFaces = (1 << Faces) - 1;
    private const int Channels = 0x00FF00FF, Factors5 = 5;

    private const int AbsorbsBit = 9, FluidsBit = 11, Leaves = 1 << 8, Absorbs = 1 << AbsorbsBit, Custom = 1 << 10;
    private const int FluidsAbsorb = 1 << FluidsBit, MaxOverrides = EngineShape.MaxMethods;
    private const byte ReturnTrue = 0x17, ReturnFalse = 0x16, Return = 0x2A; // ldc.i4.1 / ldc.i4.0, ret

    // The engine's 1f / (float)4 for a front that does not occlude
    private const float Quarter = 1f / 4, MaxFactor = 16;

    // EngineShape of Shaped() and FusedShaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x937CF4F5C7CB716DUL, FusedShape = 0xEE59F74AD862E162UL;

    private const int FastCounter = 0, EngineCounter = 1, FusedCounter = 2;

    private static readonly Tally Counts = new(FusedCounter + 1);

    // Both built whole before publishing, never written after: any tessellation thread may read another's
    private static Tables? _tables;
    private static Quotients _quotients = new(0.67f);
    private static MethodBase?[] _face = [], _fused = [], _corner = [], _ao = [];
    private static bool _foreign, _fusedForeign;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static bool FusedInstalled { get; private set; }

    // Another patch on a method: the engine lights all faces or JSON blocks
    public static bool StoodDown => _foreign || _fusedForeign;

    public static long FastFaces => Counts.Total(FastCounter);
    public static long EngineFaces => Counts.Total(EngineCounter);
    public static long FusedBlocks => Counts.Total(FusedCounter);

    public static void Install(Harmony harmony, ILogger? logger = null, ulong shape = Shape,
        ulong fusedShape = FusedShape)
    {
        (Installed, FusedInstalled, _foreign, _fusedForeign, _tables) = (false, false, false, false, null);
        var (face, fused) = (Target(), FusedTarget());
        if (!NotNull(face) || !Accessible(nameof(FaceLight), logger) ||
            !EngineShape.Matches(Shaped(), shape, nameof(FaceLight), logger))
        {
            return;
        }

        if (!Vector128.IsHardwareAccelerated)
        {
            logger?.Notification("Komet FaceLight: no vector hardware, the engine lights every face");
            return;
        }

        var shaped = fused is not null &&
                     EngineShape.Matches(FusedShaped(), fusedShape, nameof(FaceLight) + " fused", logger);
        (_face, _fused, _corner, _ao) = ([face], shaped ? [fused] : [], [CornerAo()], AoMethods());
        _ = harmony.Patch(face, new HarmonyMethod(Prefix));
        Installed = true;
        if (shaped)
        {
            _ = harmony.Patch(fused, new HarmonyMethod(FusedPrefix));
            FusedInstalled = true;
        }

        Recheck();
    }

    // Replacing patches are skipped with the original; postfixes see the same result but miss bypassed calls
    internal static void Recheck()
    {
        if (!Installed) return;

        const KapiPatchKinds replacing = KapiPatchKinds.Replacing, all = KapiPatchKinds.All;
        var (own, overrides) = (typeof(FaceLight), Volatile.Read(ref _tables)?.Overrides ?? []);
        var face = EngineShape.Foreign(_face, replacing, null, own) || EngineShape.Foreign(_corner, all, null, own) ||
                   EngineShape.Foreign(_ao, all, null) || EngineShape.Foreign(overrides, all, null);
        var fused = EngineShape.Foreign(_fused, replacing, null, own) || EngineShape.Foreign(_face, all, null, own);
        _foreign = EngineShape.Report(Logger, nameof(FaceLight), _foreign, face,
            face ? [.. _face, .. _corner, .. _ao, .. overrides] : [], all, null, [own]);
        _fusedForeign = EngineShape.Report(Logger, nameof(FaceLight) + " fused", _fusedForeign, fused,
            fused ? [.. _fused, .. _face] : [], all, null, [own]);
    }

    internal static MethodInfo? Target()
    {
        var method = Method(typeof(TCTCache), "CalcBlockFaceLight", typeof(int), typeof(int));
        return NotNull(method) && Assert(method.ReturnType == typeof(long)) ? method : null;
    }

    internal static MethodInfo? FusedTarget()
    {
        var method = Method(typeof(JsonTesselator), nameof(JsonTesselator.SetUpLightRGBs), typeof(TCTCache));
        return NotNull(method) && Assert(method.ReturnType == typeof(long)) ? method : null;
    }

    private static MethodInfo? CornerAo()
    {
        var method = AccessTools.DeclaredMethod(typeof(TCTCache), "CornerAoRGB");
        return NotNull(method) && Assert(method.ReturnType == typeof(int)) ? method : null;
    }

    private static MethodBase?[] AoMethods()
    {
        MethodBase?[] methods =
        [
            Method(typeof(Block), nameof(Block.DoEmitSideAo), typeof(IGeometryTester), typeof(BlockFacing)),
            Method(typeof(Block), nameof(Block.DoEmitSideAoByFlag), typeof(IGeometryTester),
                typeof(Vec3iAndFacingFlags), typeof(int)),
            AccessTools.DeclaredPropertyGetter(typeof(Block), nameof(Block.ForFluidsLayer))
        ];
        return methods;
    }

    // Fast face reproduces: CalcBlockFaceLight, CornerAoRGB, Block's AO methods, SmallBoolArray SideAo reads
    internal static MethodBase?[] Shaped()
    {
        var bits = typeof(SmallBoolArray);
        MethodBase?[] methods =
        [
            Target(), CornerAo(), .. AoMethods(), AccessTools.DeclaredPropertyGetter(bits, "Item"),
            Method(bits, "op_Implicit", bits)
        ];
        return methods; // a missing one fingerprints as 0
    }

    internal static MethodBase?[] FusedShaped() => [FusedTarget()];

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    // ReSharper disable once InconsistentNaming Harmony binds the engine's parameter name
    private static bool Prefix(TCTCache __instance, int tileSide, int extNeibIndex3d, ref long __result)
    {
        if (!Enabled || _foreign || !NotNull(__instance.CurrentLightRGBByCorner)) return true;

        if (!Face(__instance, tileSide, extNeibIndex3d, __instance.CurrentLightRGBByCorner, Neighbours(__instance),
                out var sum))
        {
            if (Counting.Hud) Counts.Add(EngineCounter, 1);

            return true;
        }

        if (Counting.Hud) Counts.Add(FastCounter, 1);

        __result = sum;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool FusedPrefix(JsonTesselator __instance, TCTCache vars, ref long __result)
    {
        if (!Enabled || _foreign || _fusedForeign || !NotNull(vars)) return true;

        if (!Six(vars, JsonLight(__instance), out var sum)) return true;

        __result = sum;
        return false;
    }

    // Face for OwnTessellation cubes and topsoil (no engine entry), gated and counted as the prefix; false: engine
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Lit(TCTCache vars, int tileSide, int front)
    {
        if (!Enabled || !Installed || _foreign || !NotNull(vars.CurrentLightRGBByCorner) ||
            !Face(vars, tileSide, front, vars.CurrentLightRGBByCorner, Neighbours(vars), out _))
        {
            return false;
        }

        if (Counting.Hud) Counts.Add(FastCounter, 1);

        return true;
    }

    // Six for OwnTessellation JSON blocks (no SetUpLightRGBs), gated as the fused prefix; false: engine's loop
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Fused(TCTCache vars, int[] json, out long sum)
    {
        sum = 0;
        if (!Enabled || !FusedInstalled || _foreign || _fusedForeign) return false;

        return Six(vars, json, out sum);
    }

    // One face's AO into corners[0..3], neighbours[1..8]; false, unwritten: no AO, non-plain, bad multiplier or index
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Face(TCTCache vars, int tileSide, int front, int[] corners, int[] neighbours, out long sum)
    {
        sum = 0;
        var self = vars.block;
        if (!vars.aoAndSmoothShadows || self is null || (uint)tileSide >= Faces ||
            (self.SideAo & (1 << tileSide)) == 0)
        {
            return false;
        }

        var tables = Current(vars.tct);
        var e = vars.extIndex3d;
        if (tables is null || !Arrays(vars.tct, out var solid, out var fluid, out var rgb) || !Index(front, ExtCells) ||
            !Index(e - Reach, ExtCells - 2 * Reach) || !Assert(corners.Length >= Edges) ||
            !Assert(neighbours.Length > Samples))
        {
            return false;
        }

        ref var side = ref tables.Geometry.Sides[tileSide];
        var kinds = tables.Kinds;
        var facing = Front(kinds, solid[front], fluid[front]);
        if ((facing & Custom) != 0) return false;

        Cells cells = default;
        Block? known = null;
        ref var solids = ref MemoryMarshal.GetArrayDataReference(solid);
        ref var fluids = ref MemoryMarshal.GetArrayDataReference(fluid);
        ref var lights = ref MemoryMarshal.GetArrayDataReference(rgb);
        for (var k = 0; k < Samples; k++)
        {
            var p = e + side.Offset[k];
            var block = Unsafe.Add(ref solids, p);
            if (!ReferenceEquals(block, known))
            {
                if (!Plain(kinds, block)) return false;

                known = block;
            }

            (cells.Desc[k], cells.Rgb[k]) = (Sample(block, Unsafe.Add(ref fluids, p)), Unsafe.Add(ref lights, p));
        }

        if (!Multipliers(vars, self, out var factors)) return false;

        var leaves = self.BlockMaterial == EnumBlockMaterial.Leaves ? Leaves : 0;
        sum = Shade(ref side, tileSide, leaves, facing, rgb[front], ref cells, ref factors, corners, neighbours);
        return true;
    }

    // SetUpLightRGBs into json[0..24], each face lit here or by the engine; false, unwritten: engine (also without AO)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool Six(TCTCache vars, int[] json, out long sum)
    {
        sum = 0;
        var self = vars.block;
        if (!vars.aoAndSmoothShadows || self is null || (self.SideAo & AllFaces) == 0 ||
            !Multipliers(vars, self, out var factors))
        {
            return false;
        }

        var (tables, corners, neighbours, e) = (Current(vars.tct), vars.CurrentLightRGBByCorner, Neighbours(vars),
            vars.extIndex3d);
        if (tables is null || !Arrays(vars.tct, out var solid, out var fluid, out var rgb) ||
            !Index(e - Reach, ExtCells - 2 * Reach) || !Assert(json.Length >= JsonLights) ||
            !Assert(corners.Length >= Edges) || !Assert(neighbours.Length > Samples) ||
            !Standard(TileSideEnum.MoveIndex))
        {
            return false;
        }

        Block27 desc = default, light = default;
        Gather(tables, solid, fluid, rgb, e, ref desc, ref light);
        var (sideAo, leaves, lit) = ((int)self.SideAo, self.BlockMaterial == EnumBlockMaterial.Leaves ? Leaves : 0, 0);
        for (var t = 0; t < Faces; t++)
        {
            ref var side = ref tables.Geometry.Sides[t];
            var p = e + Moves[t];
            var facing = Front(tables.Kinds, solid[p], fluid[p]);
            Cells cells = default;
            var custom = facing;
            for (var k = 0; k < Samples; k++)
            {
                var cell = side.Local[k];
                (cells.Desc[k], cells.Rgb[k]) = (desc[cell], light[cell]);
                custom |= desc[cell];
            }

            var fast = (sideAo & (1 << t)) != 0 && (custom & Custom) == 0;
            sum += fast
                ? Shade(ref side, t, leaves, facing, rgb[p], ref cells, ref factors, corners, neighbours)
                : Calc(vars, t, p);
            lit += fast ? 1 : 0;
            corners.AsSpan(0, Edges).CopyTo(json.AsSpan(t * Edges));
        }

        json[JsonLights - 1] = rgb[e];
        sum += json[JsonLights - 1];
        // The engine's faces count themselves in the prefix they pass through
        if (Counting.Hud)
        {
            Counts.Add(FusedCounter, 1);
            Counts.Add(FastCounter, lit);
        }

        return true;
    }
}
