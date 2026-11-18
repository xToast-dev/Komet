using System.Reflection;
using System.Runtime.CompilerServices;
using Komet.Core;
using Komet.Api;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// FaceLight: override classification, engine accessors and the lookup tables.
internal static partial class FaceLight
{
    // 1: Block's AO methods; 2: plus constant-true ForFluidsLayer (kept to check its patches); 0: engine-lit
    internal static int Classify(Type type, List<MethodBase> overrides)
    {
        if (!NotNull(type) || !NotNull(overrides)) return 0;
        const BindingFlags instance = BindingFlags.Public | BindingFlags.Instance;
        try
        {
            if (!type.IsAssignableTo(typeof(Block))) return 0;
            var emit = type.GetMethod(nameof(Block.DoEmitSideAo), instance,
                [typeof(IGeometryTester), typeof(BlockFacing)]);
            var byFlag = type.GetMethod(nameof(Block.DoEmitSideAoByFlag), instance,
                [typeof(IGeometryTester), typeof(Vec3iAndFacingFlags), typeof(int)]);
            var fluids = type.GetMethod("get_" + nameof(Block.ForFluidsLayer), instance, Type.EmptyTypes);
            if (emit?.DeclaringType != typeof(Block) || byFlag?.DeclaringType != typeof(Block) ||
                fluids is null)
            {
                return 0;
            }

            if (fluids.DeclaringType == typeof(Block)) return 1;

            if (fluids.GetBaseDefinition().DeclaringType != typeof(Block) ||
                EngineShape.Foreign([fluids], KapiPatchKinds.All, null))
            {
                return 0;
            }

            var kind = fluids.GetMethodBody()?.GetILAsByteArray() switch
            {
                [ReturnTrue, Return] => 2,
                [ReturnFalse, Return] => 1,
                _ => 0
            };
            if (kind == 0 || overrides.Contains(fluids)) return kind;
            if (overrides.Count >= MaxOverrides) return 0;

            overrides.Add(fluids);
            return kind;
        }
        catch (Exception e) when (e is AmbiguousMatchException or TypeLoadException or FileNotFoundException or
                                      FileLoadException or BadImageFormatException or MissingMemberException or
                                      InvalidOperationException)
        {
            return 0;
        }
    }

    // The engine's face for Six and the own tesselators, via the method entry and so the prefix above
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CalcBlockFaceLight")]
    internal static extern long Calc(TCTCache vars, int tileSide, int extNeibIndex3D);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "neighbourLightRGBS")]
    private static extern ref int[] Neighbours(TCTCache vars);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "jsonLightRGB")]
    private static extern ref int[] JsonLight(JsonTesselator tesselator);

    private readonly record struct Entry(Block? Owner, bool FluidsLayer);

    [InlineArray(Samples)]
    internal struct Eight
    {
        private int _element;
    }

    [InlineArray(Around)]
    private struct Block27
    {
        private int _element;
    }

    [InlineArray(Factors5)]
    private struct Floats5
    {
        private float _element;
    }

    // One face's 8 samples: what the cell holds (EmitSideAo, Leaves, Absorbs, Custom bits) and its light
    internal struct Cells
    {
        public Eight Desc;
        public Eight Rgb;
    }

    // Face of blockFaceVerticesCentered: sample offsets, edge/corner side flags, 3x3x3 cell, DoEmitSideAo flag to test
    internal struct Side
    {
        public Eight Offset;
        public Eight First;
        public Eight Second;
        public Eight Local;
        public int Toward;
    }

    private sealed record Quotients(float Occ)
    {
        public float Half { get; } = Occ / 2;
        public float Third { get; } = Occ / 3;
        public float Fourth { get; } = Occ / 4;
        public bool Bounded { get; } = Occ is >= 0 and <= MaxFactor;
    }

    private sealed record Tables(Block?[] Source, Entry[] Kinds, Geometry Geometry, MethodBase[] Overrides)
    {
        public bool Valid { get; } = Geometry.Valid && Kinds.Length <= MaxBlocks;

        // blockFaceVerticesCentered is written only in its static constructor: read with the kinds, never again
        public static Tables Build(Block?[] blocks)
        {
            var kinds = new Entry[Math.Min(blocks.Length, MaxBlocks)];
            List<MethodBase> overrides = [];
            Dictionary<Type, int> byType = [];
            for (var i = 0; i < Math.Min(blocks.Length, MaxBlocks); i++)
            {
                var block = blocks[i];
                // The engine indexes blocksFast by id; a stray entry stays custom
                if (block is null || block.BlockId != i) continue;

                var type = block.GetType();
                if (!byType.TryGetValue(type, out var kind) && byType.Count < MaxTypes)
                {
                    byType[type] = kind = Classify(type, overrides);
                }

                if (kind != 0) kinds[i] = new Entry(block, kind == 2);
            }

            return new Tables(blocks, kinds, Geometry.Read(), [.. overrides]);
        }
    }

    private sealed class Geometry
    {
        private const int Rows = Samples + 1;

        private Geometry(Vec3iAndFacingFlags[][]? source)
        {
            Valid = source is { Length: Faces } && Fill(source);
            CellList = Valid ? Distinct() : [];
        }

        public Side[] Sides { get; } = new Side[Faces];
        public byte[] CellList { get; } // the cells some face samples, each once
        public bool Valid { get; }

        public static Geometry Read()
        {
            var source = CubeFaceVertices.blockFaceVerticesCentered;
            return new Geometry(NotNull(source) && Assert(source.Length == Faces) ? source : null);
        }

        // The index offset of a cell of the 3x3x3 block, numbered (y + 1) * 9 + (z + 1) * 3 + x + 1
        public static int OffsetOf(int cell) =>
            (cell / 9 - 1) * TessSeams.Plane + (cell / 3 % 3 - 1) * Ext + (cell % 3 - 1);

        private byte[] Distinct()
        {
            var seen = new bool[Around];
            List<byte> cells = [];
            for (var i = 0; i < Slots; i++)
            {
                var cell = Sides[i / Samples].Local[i % Samples];
                if (seen[cell]) continue;

                seen[cell] = true;
                cells.Add((byte)cell);
            }

            return [.. cells];
        }

        private bool Fill(Vec3iAndFacingFlags[][] source)
        {
            for (var t = 0; t < Faces; t++)
            {
                var row = source[t];
                var toward = BlockFacing.ALLFACES[t]?.Opposite?.Flag ?? -1;
                if (row is not { Length: >= Rows } || !Assert(toward is > 0 and < 64)) return false;

                Sides[t].Toward = toward;
                for (var k = 0; k < Samples; k++)
                {
                    if (!Take(t, k, row[k])) return false;
                }
            }

            return true;
        }

        // A sample lies within one cell of the lit one, offset and flags as the engine's constructor sets them
        private bool Take(int t, int k, Vec3iAndFacingFlags? vec)
        {
            if (!NotNull(vec)) return false;

            ref var side = ref Sides[t];
            var edge = k < Edges;
            (side.Offset[k], side.First[k], side.Second[k]) = (vec.extIndexOffset,
                edge ? vec.OppositeFlagsUpperOrLeft : vec.OppositeFlags,
                edge ? vec.OppositeFlagsLowerOrRight : vec.FacingFlags);
            side.Local[k] = (vec.Y + 1) * 9 + (vec.Z + 1) * 3 + vec.X + 1;
            return Math.Max(Math.Abs(vec.X), Math.Max(Math.Abs(vec.Y), Math.Abs(vec.Z))) <= 1 &&
                   vec.extIndexOffset == (vec.Y * Ext + vec.Z) * Ext + vec.X &&
                   (uint)side.First[k] < 64 && (uint)side.Second[k] < 64;
        }
    }
}
