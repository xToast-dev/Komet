using System.Runtime.InteropServices;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static System.BitConverter;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// The row table: each pool's region of rows (one per location, moved whole into a larger block when it outgrows its
// room), the blocks let go for the next region, and the chunk table the rows point into for CullVisible bits. Changed
// rows are collected as dirty runs for the next upload.
internal static partial class OcclusionCulling
{
    private const int AboveLod = 4, BelowLod = 5, MinCapacity = 256, MaxGrowths = 8;
    private const float Sqrt3 = FrustumSweep.Sqrt3;
    private const uint SortedBit = 1 << 4; // the row's faces are sorted by direction (FaceSorting)
    private const int SlabSteps = 4; // a slab's planes in quarter blocks
    private const long LatchMs = 10_000; // a full table keeps its layout this long before trying again

    // Row and chunk arrays exist only while rows are ready (ReadyRows to ReleaseRows); the row arrays grow with the rows
    // used and start at the largest size this process needed, so a later renderer start or world does not grow them again
    private static uint[] _rowData = [];
    private static ModelDataPoolLocation?[] _rowLocations = [];
    private static int _peakRows = FirstRows;
    private static readonly Dictionary<ModelDataPoolLocation, int> RowOf = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<MeshDataPool, Region> Regions = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Bools, int> ChunkOf = new(ReferenceEqualityComparer.Instance);
    private static Bools?[] _chunkBools = [];
    private static int[] _chunkRefs = [];
    private static uint[] _chunkBits = [];
    private static readonly Stack<int> FreeChunks = [];
    private static readonly List<(int Start, int Count)> Dirty = [];
    private static readonly List<(int Base, int Capacity)> FreeBlocks = []; // rows let go, for the next region
    private static int[] _marks = [];
    private static int _mark;

    private static Region? RegionOf(MeshDataPool pool)
    {
        var locations = Locations(pool);
        if (!NotNull(locations) || locations.Count > MaxPoolRows) return null;

        var version = Version(locations);
        if (Regions.TryGetValue(pool, out var region) && ReferenceEquals(region.Owner, locations) &&
            region.Version == version)
        {
            return region.Waiting.Count == 0 || Appended(region) ? region : null;
        }

        if (region is not null && ReferenceEquals(region.Owner, locations) &&
            Resynced(region, locations, version))
        {
            return region;
        }

        if (RowsFull)
        {
            return null; // written whole only once the full table tries again
        }

        if (region is null) Regions[pool] = region = new Region();

        (region.Vao, region.Placed) = (ModelRef(pool) is VAO vao ? vao.VaoId : 0, PoolOrigin(pool));
        Rewrites++;
        Unrow(region);
        region.Waiting.Clear();
        if (!Room(region, locations.Count)) return null;

        var span = CollectionsMarshal.AsSpan(locations);
        for (var i = 0; i < Math.Min(span.Length, MaxPoolRows); i++)
        {
            if (span[i] is not { } location || !Append(region, location))
            {
                region.Owner = null; // its rows so far go when it comes again
                return null;
            }
        }

        (region.Owner, region.Version) = (locations, version);
        return region;
    }

    // Changed past TryAdd/RemoveLocation: patch only changed rows, not the whole region (up to 6 MB, a frame's stall)
    private static bool Resynced(Region region, List<ModelDataPoolLocation> locations, int version)
    {
        if (++_mark == int.MaxValue) return false;

        region.Waiting.Clear();
        var span = CollectionsMarshal.AsSpan(locations);
        for (var i = 0; i < Math.Min(span.Length, MaxPoolRows); i++)
        {
            if (span[i] is not { } location) return false;

            if (RowOf.TryGetValue(location, out var row) && row >= region.Base && row < region.Base + region.Count)
            {
                _marks[row] = _mark;
                Hide(row, Hidden(location));
            }
            else
            {
                region.Waiting.Add(location);
            }
        }

        var (top, count) = (region.Base + region.Count - 1, region.Count); // Unrow shrinks Count
        for (var k = 0; k < Math.Min(count, MaxPoolRows); k++) // from the top: the last, already kept, takes a gap
        {
            if (_marks[top - k] != _mark) Unrow(region, top - k);
        }

        Resyncs++;
        if (!Appended(region)) return false;

        (region.Owner, region.Version) = (locations, version);
        return Assert(region.Count == locations.Count);
    }

    private static bool Appended(Region region)
    {
        if (!Room(region, region.Count + region.Waiting.Count)) return false;

        foreach (var location in region.Waiting.Bounded(MaxPoolRows))
        {
            if (!Append(region, location))
            {
                region.Owner = null; // written anew when drawn next
                return false;
            }
        }

        region.Waiting.Clear();
        return true;
    }

    private static bool Append(Region region, ModelDataPoolLocation location)
    {
        if (!Assert(region.Count < region.Capacity) || !Row(region.Base + region.Count, location, region)) return false;

        Changed(region.Base + region.Count, 1);
        region.Count++;
        region.Triangles += (location.IndicesEnd - location.IndicesStart) / 3;
        return true;
    }

    // The region's own room, or a larger block (freed or past the last) its rows move to
    private static bool Room(Region region, int rows)
    {
        if (rows <= region.Capacity) return true;

        var capacity = Math.Max(rows + rows / 4 + 64, MinCapacity);
        var start = Freed(ref capacity);
        if (start < 0 && RowsUsed + capacity <= MaxRows && Grown(RowsUsed + capacity))
        {
            (start, RowsUsed) = (RowsUsed, RowsUsed + capacity);
        }

        if (start < 0) return Full(RowsUsed + capacity, false);

        for (var i = 0; i < Math.Min(region.Count, MaxPoolRows); i++)
        {
            var (from, to) = (region.Base + i, start + i);
            _rowData.AsSpan(from * RowUints, RowUints).CopyTo(_rowData.AsSpan(to * RowUints, RowUints));
            if (_rowLocations[from] is { } location) RowOf[location] = to;

            (_rowLocations[to], _rowLocations[from]) = (_rowLocations[from], null);
        }

        if (region.Capacity > 0 && FreeBlocks.Count < MaxBlocks) FreeBlocks.Add((region.Base, region.Capacity));

        Changed(start, region.Count);
        (region.Base, region.Capacity) = (start, capacity);
        return true;
    }

    // A freed block of at least capacity rows, adjacent ones merged when none is that large; a block far larger is split
    // and its rest stays free, else capacity becomes the whole block's. -1 when none fits.
    private static int Freed(ref int capacity)
    {
        var at = Fit(capacity);
        if (at < 0 && Merged()) at = Fit(capacity);

        if (at < 0) return -1;

        var (start, size) = FreeBlocks[at];
        if (size - capacity >= MinCapacity)
        {
            FreeBlocks[at] = (start + capacity, size - capacity);
        }
        else
        {
            FreeBlocks.RemoveAt(at);
            capacity = size;
        }

        return Assert(start + capacity <= RowsUsed) ? start : -1;
    }

    private static int Fit(int capacity)
    {
        var at = -1;
        for (var i = 0; i < Math.Min(FreeBlocks.Count, MaxBlocks) && at < 0; i++) // no closure
        {
            if (FreeBlocks[i].Capacity >= capacity) at = i;
        }

        return at;
    }

    // Free blocks in row order, touching ones joined; false when none touched
    private static bool Merged()
    {
        if (FreeBlocks.Count < 2) return false;

        FreeBlocks.Sort();
        var kept = 0;
        for (var i = 1; i < Math.Min(FreeBlocks.Count, MaxBlocks); i++)
        {
            var (last, next) = (FreeBlocks[kept], FreeBlocks[i]);
            if (last.Base + last.Capacity == next.Base)
            {
                FreeBlocks[kept] = (last.Base, last.Capacity + next.Capacity);
            }
            else
            {
                FreeBlocks[++kept] = next;
            }
        }

        var joined = FreeBlocks.Count - kept - 1;
        FreeBlocks.RemoveRange(kept + 1, joined);
        return joined > 0;
    }

    // Row arrays and the GPU's rows buffer doubled until rows fit; false past MaxRows. The buffer keeps its rows and
    // waits for the GPU (a stall, at most three a process: the arrays start at the process's peak)
    private static bool Grown(int rows)
    {
        var size = _rowLocations.Length;
        if (rows <= size) return true;

        for (var i = 0; i < MaxGrowths && size < rows; i++)
        {
            size = Math.Min(2 * Math.Max(size, FirstRows), MaxRows);
        }

        if (size < rows || !Assert(_rowBuffer != 0)) return false;

        Gpu.Upload<uint>(_rowBuffer, [], size * RowBytes);
        Array.Resize(ref _rowData, size * RowUints);
        Array.Resize(ref _rowLocations, size);
        Array.Resize(ref _marks, size);
        _peakRows = Math.Max(_peakRows, size);
        return true;
    }

    // No room for the rows or chunks: the next frame lays every region out anew. When that frame finds none either, the
    // layout stays for LatchMs (any call with a pool that needs new rows is culled by the engine), logged once, then a
    // relayout tries again. rows: the rows wanted, or with chunks the rows in use.
    private static bool Full(int rows, bool chunks)
    {
        if (RowsFull) return false;

        if (!_relaidOut)
        {
            _relayout = true;
            return false;
        }

        _fullUntil = Environment.TickCount64 + LatchMs;
        if (!_fullLogged && _logger is { } logger)
        {
            logger.Warning("Komet: GPU terrain culling's {0} ({1} of {2} rows {3}, {4} of {5} chunks held); for 10 s " +
                           "any terrain pass with a pool that needs new rows uses the engine's culling",
                chunks ? "chunk table is full" : "row table is full", rows, MaxRows, chunks ? "in use" : "wanted",
                _chunksUsed - 1 - FreeChunks.Count, MaxChunks);
        }

        _fullLogged = true;
        return false;
    }

    // false when the chunk table is full
    private static bool Row(int row, ModelDataPoolLocation location, Region region)
    {
        if (!Index(row, _rowLocations.Length)) return false;

        var chunk = ChunkSlot(CullVisible(location));
        if (chunk < 0) return false;

        var (at, sphere, lod) = (row * RowUints, location.FrustumCullSphere, Lod(location));
        var data = _rowData.AsSpan(at, RowUints);
        (data[0], data[1], data[2]) = (SingleToUInt32Bits(sphere.x), SingleToUInt32Bits(sphere.y),
            SingleToUInt32Bits(sphere.z));
        data[3] = (uint)(location.IndicesEnd - location.IndicesStart);
        (data[4], data[5], data[6]) = (SingleToUInt32Bits(sphere.radius / Sqrt3),
            SingleToUInt32Bits(sphere.radiusY / Sqrt3), SingleToUInt32Bits(sphere.radiusZ / Sqrt3));
        data[7] = (uint)location.IndicesStart;
        var code = lod switch
        {
            > 3 => AboveLod, < 0 => BelowLod, _ => lod
        }; // the far shadow pass draws any level >= 1
        data[8] = (Hidden(location) ? 1u : 0u) | ((uint)code << 1);
        (data[9], data[10], data[11]) = ((uint)chunk, 0, 0);
        data[8] |= Directed(data, location, region) ? SortedBit : 0;
        _rowLocations[row] = location;
        RowOf[location] = row;
        return Assert(data[3] < 1u << 31);
    }

    // FaceSorting slabs into the row: faces per slab (two per uint, direction d slab j at d * Slabs + j), and each
    // slab's min/max plane relative to the row centre in quarter blocks as signed bytes, rounded outward, two slabs per
    // uint. false (range drawn whole) when unsorted or a plane overflows a byte.
    private static bool Directed(Span<uint> data, ModelDataPoolLocation location, Region region)
    {
        const int slabs = FaceSorting.Directions * FaceSorting.Slabs;
        var into = data[12..];
        into.Clear(); // data[10..11] Row zeroed already
        var faces = (location.IndicesEnd - location.IndicesStart) / 6;
        if (region.Vao == 0 || region.Placed is not { } origin ||
            FaceSorting.Of(region.Vao, location.VerticesStart) is not { } sorted || sorted.Faces != faces)
        {
            return false;
        }

        var counts = sorted.SlabCounts;
        if (!Assert(counts.Length == slabs) || counts.Max() > ushort.MaxValue) return false;

        var sphere = location.FrustumCullSphere;
        (double X, double Y, double Z) centre = (sphere.x, sphere.y, sphere.z);
        for (var i = 0; i < slabs; i++)
        {
            into[i / 2] |= (uint)counts[i] << (16 * (i % 2));
            if (counts[i] == 0) continue;

            var (o, c) = (i / FaceSorting.Slabs / 2) switch
            {
                0 => (origin.X, centre.X), 1 => (origin.Y, centre.Y), _ => (origin.Z, centre.Z)
            };
            var low = Math.Floor((o + (double)sorted.Low[i] - c) * SlabSteps);
            var high = Math.Ceiling((o + (double)sorted.High[i] - c) * SlabSteps);
            if (!(low >= sbyte.MinValue && high <= sbyte.MaxValue)) return false;

            var (l, h) = ((byte)(sbyte)low, (byte)(sbyte)high);
            into[slabs / 2 + i / 2] |= (l | ((uint)h << 8)) << (16 * (i % 2));
        }

        return Assert(faces >= sorted.Counts.Sum());
    }

    private static void Unrow(Region region)
    {
        for (var row = region.Base; row < Math.Min(region.Base + region.Count, MaxRows); row++)
        {
            Forget(row);
        }

        (region.Count, region.Triangles, region.Owner) = (0, 0, null);
    }

    // The region's last row takes its place
    private static void Unrow(Region region, int row)
    {
        var last = region.Base + region.Count - 1;
        region.Triangles -= (int)(_rowData[row * RowUints + 3] / 3);
        Forget(row);
        if (row != last)
        {
            _rowData.AsSpan(last * RowUints, RowUints).CopyTo(_rowData.AsSpan(row * RowUints, RowUints));
            if (_rowLocations[last] is { } moved) RowOf[moved] = row;

            (_rowLocations[row], _rowLocations[last]) = (_rowLocations[last], null);
            Changed(row, 1);
        }

        region.Count--;
    }

    private static void Forget(int row)
    {
        if (_rowLocations[row] is { } location && RowOf.TryGetValue(location, out var at) && at == row)
        {
            _ = RowOf.Remove(location);
        }

        _rowLocations[row] = null;
        Release((int)_rowData[row * RowUints + 9]);
        _rowData[row * RowUints + 9] = 0;
    }

    // Chunk table entry for CullVisible bits (0: none, never visible); -1 when full
    private static int ChunkSlot(Bools? bools)
    {
        if (bools is null) return 0;

        if (!ChunkOf.TryGetValue(bools, out var slot))
        {
            if (FreeChunks.Count > 0)
            {
                slot = FreeChunks.Pop();
            }
            else if (_chunksUsed < _chunkBools.Length)
            {
                slot = _chunksUsed++;
            }
            else
            {
                _ = Full(RowsUsed, true);
                return -1;
            }

            (ChunkOf[bools], _chunkBools[slot]) = (slot, bools);
            _chunkBits[slot] = (uint)Bits(bools);
            (_chunksFrom, _chunksBefore) = (Math.Min(_chunksFrom, slot), Math.Max(_chunksBefore, slot + 1));
        }

        _chunkRefs[slot]++;
        return slot;
    }

    private static void Release(int chunk)
    {
        if (chunk <= 0 || !Index(chunk, _chunkRefs.Length) || --_chunkRefs[chunk] > 0) return;

        if (_chunkBools[chunk] is { } bools) _ = ChunkOf.Remove(bools);

        (_chunkBools[chunk], _chunkRefs[chunk]) = (null, 0);
        FreeChunks.Push(chunk);
    }

    private static void Changed(int start, int count)
    {
        if (count <= 0) return;

        if (Dirty.Count > 0 && Dirty[^1] is var (lastFrom, lastCount) && start <= lastFrom + lastCount &&
            start + count >= lastFrom)
        {
            var first = Math.Min(lastFrom, start); // touching the last run: it grows
            Dirty[^1] = (first, Math.Max(lastFrom + lastCount, start + count) - first);
            return;
        }

        if (Dirty.Count < MaxRuns)
        {
            Dirty.Add((start, count));
            return;
        }

        var (low, end) = (start, start + count);
        foreach (var (from, n) in Dirty.Bounded(MaxRuns))
        {
            (low, end) = (Math.Min(low, from), Math.Max(end, from + n));
        }

        Dirty.Clear();
        Dirty.Add((low, end - low)); // too many runs: one over all, from the lowest dirty row (not row 0)
    }

    private sealed class Region
    {
        public readonly List<ModelDataPoolLocation> Waiting = []; // added since the pool was last drawn
        public int Base, Capacity, Count, Version, Triangles, Vao; // Vao: the pool's, for FaceSorting's groups
        public List<ModelDataPoolLocation>? Owner;
        public Vec3i? Placed { get; set; } // the pool's origin (its vertices are relative to it)
    }
}
