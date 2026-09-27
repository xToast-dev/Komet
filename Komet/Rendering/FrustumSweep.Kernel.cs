using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Vintagestory.API.MathTools;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// The cull itself. Sweep marks the rows whose geometry survives the planes (cell boxes first, then a vector of rows at a time); Emit
// takes IsVisible's other terms for those rows and writes the pool's index ranges.
internal static partial class FrustumSweep
{
    // InFrustumShadowPass's range test, in its float operations, ahead of any plane
    private static void Range(Mirror m, FrustumCulling culler, int from, int to)
    {
        var player = PlayerPos(culler);
        if (!NotNull(player) || !Assert(to <= m.Slots) || !Assert(m.Outside.Length >= m.Slots)) return;
        float px = player.X, pz = player.Z;
        for (var i = from; i < Math.Min(to, MaxSlots); i++)
            m.Outside[i] = Math.Abs(px - m.Cx[i]) >= culler.shadowRangeX ||
                           Math.Abs(pz - m.Cz[i]) >= culler.shadowRangeZ ? -1 : 0;
    }

    // Plane.AABBisOutside a vector at a time, the normal's sign picking the corner; rows Diff added since the grid sit behind it
    private static void Sweep(Mirror m, ReadOnlySpan<Plane> planes, bool ranged, FrustumCulling culler)
    {
        var w = Vector<double>.Count;
        if (!Assert(planes.Length is > 0 and <= PlaneCount) || !Assert(m.Outside.Length >= m.Slots)) return;
        Span<PlaneV> pv = stackalloc PlaneV[PlaneCount];
        var count = Math.Min(planes.Length, PlaneCount);
        for (var p = 0; p < Math.Min(count, PlaneCount); p++) pv[p] = new PlaneV(planes[p]);
        var allOut = new Vector<long>(-1);
        Array.Clear(m.Cand, 0, (m.Length + WordBits - 1) / WordBits);
        var from = 0;
        if (m.Cells > 0)
        {
            SweepBoxes(m, pv, count, w, allOut);
            for (var b = 0; b < Math.Min(m.Cells, MaxCells); b++)
            {
                var (at, padded) = (m.BStart[b], (m.BCount[b] + w - 1) / w * w);
                if (!Assert(at + padded <= m.Base)) return;
                if (m.BOutside[b] != 0)
                {
                    if (Counting.Hud) m.SkippedRows += m.BCount[b];
                    continue;
                }

                if (ranged) Range(m, culler, at, at + padded);
                for (var i = at; i < Math.Min(at + padded, MaxSlots); i += Vector<double>.Count)
                    SweepVector(m, pv, count, ranged, i, allOut);
            }

            from = m.Base;
        }

        if (ranged) Range(m, culler, from, m.Slots);
        for (var i = from; i < Math.Min(m.Slots, MaxSlots); i += Vector<double>.Count)
            SweepVector(m, pv, count, ranged, i, allOut);
    }

    // The plane kernel over the cell boxes. No range test: that runs in float on a sphere centre, which a box is not.
    private static void SweepBoxes(Mirror m, ReadOnlySpan<PlaneV> pv, int count, int w, Vector<long> allOut)
    {
        var padded = (m.Cells + w - 1) / w * w;
        if (!Assert(padded <= m.BCx.Length) || !Assert(padded <= m.BOutside.Length)) return;
        for (var i = m.Cells; i < Math.Min(padded, MaxCells + MaxLanes); i++)
        {
            (m.BCx[i], m.BCy[i], m.BCz[i]) = (0, 0, 0);
            (m.BHx[i], m.BHy[i], m.BHz[i]) = (0, 0, 0);
        }

        for (var i = 0; i < Math.Min(padded, MaxCells + MaxLanes); i += Vector<double>.Count)
        {
            var outside = Vector<long>.Zero;
            Vector<double> cx = new(m.BCx, i), cy = new(m.BCy, i), cz = new(m.BCz, i);
            Vector<double> hx = new(m.BHx, i), hy = new(m.BHy, i), hz = new(m.BHz, i);
            for (var p = 0; p < Math.Min(count, PlaneCount); p++)
            {
                var d = (cx + hx * pv[p].Sx) * pv[p].Nx + (cy + hy * pv[p].Sy) * pv[p].Ny +
                        (cz + hz * pv[p].Sz) * pv[p].Nz + pv[p].D;
                outside |= Vector.LessThan(d, Vector<double>.Zero);
                if (Vector.EqualsAll(outside, allOut)) break;
            }

            outside.CopyTo(m.BOutside, i);
        }
    }

    private static void SweepVector(Mirror m, ReadOnlySpan<PlaneV> pv, int count, bool ranged, int i,
        Vector<long> allOut)
    {
        if (!Assert(i + Vector<double>.Count <= m.Outside.Length)) return;
        var outside = ranged ? new Vector<long>(m.Outside, i) : Vector<long>.Zero;
        if (ranged && Vector.EqualsAll(outside, allOut)) return;
        Vector<double> cx = Widen(m.Cx, i), cy = Widen(m.Cy, i), cz = Widen(m.Cz, i);
        Vector<double> hx = Widen(m.Hx, i), hy = Widen(m.Hy, i), hz = Widen(m.Hz, i);
        for (var p = 0; p < Math.Min(count, PlaneCount); p++)
        {
            var d = (cx + hx * pv[p].Sx) * pv[p].Nx + (cy + hy * pv[p].Sy) * pv[p].Ny +
                    (cz + hz * pv[p].Sz) * pv[p].Nz + pv[p].D;
            outside |= Vector.LessThan(d, Vector<double>.Zero);
        }

        Mark(m, outside, i, allOut);
    }

    // A vector of doubles from as many floats at i. The width is a JIT constant, so this folds to one load and one convert.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<double> Widen(float[] values, int i)
    {
        if (Vector<double>.Count == Vector256<double>.Count)
            return Vector256.WidenLower(Vector128.Create(values.AsSpan(i, 4)).ToVector256Unsafe()).AsVector();
        if (Vector<double>.Count == Vector128<double>.Count)
            return Vector128.WidenLower(Vector64.Create(values.AsSpan(i, 2)).ToVector128Unsafe()).AsVector();
        if (Vector<double>.Count == Vector512<double>.Count)
            return Vector512.WidenLower(Vector256.Create(values.AsSpan(i, 8)).ToVector512Unsafe()).AsVector();
        Span<double> wide = stackalloc double[MaxLanes];
        for (var k = 0; k < Math.Min(Vector<double>.Count, MaxLanes); k++) wide[k] = values[i + k];
        return new Vector<double>(wide);
    }

    private static void Mark(Mirror m, Vector<long> outside, int i, Vector<long> allOut)
    {
        if (Vector.EqualsAll(outside, allOut) || !Assert(i + Vector<long>.Count <= m.Inv.Length)) return;
        var keep = ~Lanes(outside) & (uint.MaxValue >> (32 - Vector<long>.Count));
        for (var k = 0; k < MaxLanes && keep != 0; k++)
        {
            var row = m.Inv[i + BitOperations.TrailingZeroCount(keep)];
            keep &= keep - 1;
            if (row >= 0 && Index(row, m.Length)) m.Cand[row >> 6] |= 1UL << row;
        }
    }

    // One bit per lane outside. The width is a JIT constant, so this folds to one movemask.
    private static uint Lanes(Vector<long> outside)
    {
        if (!Assert(Vector<long>.Count <= MaxLanes)) return uint.MaxValue;
        if (Vector<long>.Count == Vector256<long>.Count) return outside.AsVector256().ExtractMostSignificantBits();
        if (Vector<long>.Count == Vector128<long>.Count) return outside.AsVector128().ExtractMostSignificantBits();
        if (Vector<long>.Count == Vector512<long>.Count)
            return (uint)outside.AsVector512().ExtractMostSignificantBits();
        var bits = 0u;
        for (var k = 0; k < Math.Min(Vector<long>.Count, MaxLanes); k++)
            if (outside[k] != 0)
                bits |= 1u << k;
        return bits;
    }

    // IsVisible's terms are a side-effect-free conjunction (FrustumVisible's only reader, ClientChunk.IsFrustumVisible, has no caller),
    // so LOD and range run first on the flat arrays and only the rest read Hide and Bools, branch-free so the misses overlap.
    // VisibleBufIndex is read once: ChunkCuller swaps it on its own thread, so one snapshot per pool is as good as the engine's reads.
    private static int Emit(Mirror m, FrustumCulling culler, EnumFrustumCullMode mode, int[] starts, int[] sizes,
        out int rendered)
    {
        rendered = 0;
        if (!Scratch(m) || !Assert(starts.Length >= 2 * m.Length) || !Assert(sizes.Length >= m.Length)) return -1;
        var k = Candidates(m, mode, culler);
        if (k < 0) return -1;
        var (rows, ok, refs, buffer) = (m.Rows, m.Ok, m.Refs, ModelDataPoolLocation.VisibleBufIndex);
        for (var q = 0; q < Math.Min(k, MaxLocations); q++)
        {
            ref readonly var row = ref refs[rows[q]];
            ok[q] = !row.Loc.Hide;
            ok[q] &= row.Vis[buffer]; // no short circuit: a branch on the first load would hold up the second
        }

        var groups = 0;
        for (var q = 0; q < Math.Min(k, MaxLocations); q++)
        {
            if (!ok[q]) continue;
            var i = rows[q];
            (starts[groups * 2], sizes[groups]) = (m.Start[i], m.Count[i]);
            rendered += m.Count[i] / 3;
            groups++;
        }

        return groups;
    }

    private static bool Scratch(Mirror m)
    {
        var n = m.Length;
        if (!Assert(n <= MaxLocations)) return false;
        if (m.Rows.Length >= n) return true;
        var cap = Capacity(n, m.Rows.Length, MaxLocations);
        (m.Rows, m.Ok) = (new int[cap], new bool[cap]);
        return Assert(m.Ok.Length >= n);
    }

    // The rows the planes left (Cand) that pass the mode's LOD test, into Rows; how many. CullNormal takes IsVisible's range and LOD
    // test; the shadow passes had their range test in the sweep, and only the far one drops LOD 0; CullInstant ignores LodLevel.
    private static int Candidates(Mirror m, EnumFrustumCullMode mode, FrustumCulling culler)
    {
        var (k, words, player) = (0, (m.Length + WordBits - 1) / WordBits, PlayerPos(culler));
        var (normal, minLod) = (mode == EnumFrustumCullMode.CullNormal,
            mode == EnumFrustumCullMode.CullInstantShadowPassFar ? 1 : int.MinValue);
        if ((normal && !NotNull(player)) || !Assert(words <= m.Cand.Length) || !Assert(m.Rows.Length >= m.Length))
            return -1;
        for (var word = 0; word < Math.Min(words, MaxWords); word++)
        {
            var bits = m.Cand[word];
            for (var b = 0; b < WordBits && bits != 0; b++)
            {
                var i = word * WordBits + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                if (normal ? LodVisible(m, i, player, culler) : m.Lod[i] >= minLod) m.Rows[k++] = i;
            }
        }

        return k;
    }

    // IsVisible's CullNormal range and LOD test with the engine's operations
    private static bool LodVisible(Mirror m, int i, BlockPos player, FrustumCulling culler)
    {
        if (!Index(i, m.Length)) return false;
        var slot = m.Slot[i];
        if (!Index(slot, m.Slots)) return false;
        double d = player.HorDistanceSqTo(m.Cx[slot], m.Cz[slot]);
        return m.Lod[i] switch
        {
            0 => culler.lod0BiasSq > 0f && d < culler.lod0BiasSq + 1024f,
            1 => d < culler.ViewDistanceSq,
            2 => d <= culler.lod2BiasSq,
            3 => d > culler.lod2BiasSq && d < culler.ViewDistanceSq,
            _ => false
        };
    }

    private readonly struct PlaneV(Plane p)
    {
        public readonly Vector<double> Nx = new(p.normalX), Ny = new(p.normalY), Nz = new(p.normalZ), D = new(p.D);

        public readonly Vector<double> Sx = new(p.normalX > 0 ? 1.0 : -1.0), Sy = new(p.normalY > 0 ? 1.0 : -1.0),
            Sz = new(p.normalZ > 0 ? 1.0 : -1.0);
    }
}
