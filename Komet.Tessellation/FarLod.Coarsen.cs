using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// FarLod: coarsening a far chunk's blocks into half-resolution cells.
internal static partial class FarLod
{
    private static void Coarsen(ChunkTesselator __instance, bool skipChunkCenter)
    {
        if (!_tracked || _air is not { } air) return;

        if (BlocksExt(__instance) is not { Length: ExtCells } blocks ||
            FluidsExt(__instance) is not { Length: ExtCells } fluids ||
            RgbsExt(__instance) is not { Length: ExtCells } light ||
            Vars(__instance) is not { } vars)
        {
            _band = FullDetail;
            return;
        }

        if (_band != FullDetail && HasFluids(fluids, skipChunkCenter)) _band = FullDetail;

        if (!IsCoarse(_band))
        {
            Skirted(blocks, fluids, air);
            return;
        }

        var (sb, sl) = (_blocks ??= new Block[ExtCells], _light ??= new int[ExtCells]);
        var open = new Sides(BandOf(_chunkX - 1, _chunkY, _chunkZ) != _band,
            BandOf(_chunkX + 1, _chunkY, _chunkZ) != _band,
            BandOf(_chunkX, _chunkY, _chunkZ - 1) != _band, BandOf(_chunkX, _chunkY, _chunkZ + 1) != _band,
            BandOf(_chunkX, _chunkY - 1, _chunkZ) != _band, BandOf(_chunkX, _chunkY + 1, _chunkZ) != _band);
        var grid = new Grid(blocks, light, sb, sl);
        Halve(grid, air, Cells, Ext - 1, open);
        if (_band == Farthest) Halve(grid, air, Cells / 2, Cells + 1, open);

        _entities = Entities(vars);
        Entities(vars) = null;
        Coarse = true;
    }

    internal static bool HasFluids(Block[] fluids, bool edgeOnly)
    {
        if (!edgeOnly) return Array.Exists(fluids, static block => block is { BlockId: not 0 });

        // Edge-only builds refresh the halo and two inner layers. The rest still belongs to a previous pass.
        for (var y = 0; y < Ext; y++)
        {
            for (var z = 0; z < Ext; z++)
            {
                var start = (y * Ext + z) * Ext;
                var edge = y < 3 || y >= Ext - 3 || z < 3 || z >= Ext - 3;
                if (edge && HasFluid(fluids.AsSpan(start, Ext))) return true;

                if (!edge && (HasFluid(fluids.AsSpan(start, 3)) || HasFluid(fluids.AsSpan(start + Ext - 3, 3))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasFluid(ReadOnlySpan<Block> row)
    {
        foreach (var block in row.Bounded(Ext))
        {
            if (block is { BlockId: not 0 }) return true;
        }

        return false;
    }

    // The grid's cells 0..last halved into cells 0..cells+1 of the same arrays, the rest air
    private static void Halve(Grid grid, Block air, int cells, int last, Sides open)
    {
        var (blocks, light, sb, sl) = grid;
        blocks.CopyTo(sb, 0);
        light.CopyTo(sl, 0);
        Array.Fill(blocks, air);
        // a cell lit nowhere (all its blocks solid ones that hold no light, as a border layer inside the neighbour's
        // ground) takes the light of the first lit cell above it, else faces next to it draw black: top-down, each
        // column carries the light of the cell above
        Span<int> above = stackalloc int[(Cells + 2) * (Cells + 2)];
        above.Clear();
        for (var down = 0; down <= Math.Min(cells + 1, Cells + 1); down++)
        {
            for (var z = 0; z <= Math.Min(cells + 1, Cells + 1); z++)
            {
                for (var x = 0; x <= Math.Min(cells + 1, Cells + 1); x++)
                {
                    var (y, column) = (cells + 1 - down, z * (cells + 2) + x);
                    var at = (y * Ext + z) * Ext + x;
                    var lit = Brightest(sl, x, y, z, cells, last);
                    if (lit == 0) lit = above[column];

                    (above[column], light[at]) = (lit, lit);
                    var wall = (x == 0 && open.MinusX) || (x == cells + 1 && open.PlusX) || (z == 0 && open.MinusZ) ||
                               (z == cells + 1 && open.PlusZ) || (y == 0 && open.MinusY) || (y == cells + 1 && open.PlusY);
                    if (wall) continue;

                    blocks[at] = Cell(sb, x, y, z, cells, last) ?? air;
                }
            }
        }
    }

    // BuildBlockPolygons prefix: a coarse chunk draws its 16³ (farthest 8³) cells only. The border layer past them (the
    // neighbours' cells above, +x and +z) hides the faces it covers but would draw its own inside the neighbour once scaled.
    private static void Inside(ChunkTesselator __instance)
    {
        if (!Coarse) return;

        var cells = _band == Farthest ? Cells / 2 : Cells;
        if (Draw(__instance) is { Length: Size * Size * Size } draw) ClearPastCells(draw, cells);

        if (DrawFluids(__instance) is { Length: Size * Size * Size } fluids) ClearPastCells(fluids, cells);
    }

    // Clears the visible-face bytes, (y * 32 + z) * 32 + x, of every cell with a local x, y or z of `cells` or more
    internal static void ClearPastCells(byte[] draw, int cells = Cells)
    {
        if (!Assert(draw.Length == Size * Size * Size) || !Assert(cells is > 0 and <= Cells)) return;

        draw.AsSpan(cells * Size * Size).Clear(); // y >= cells
        for (var y = 0; y < Math.Min(cells, Cells); y++)
        {
            draw.AsSpan((y * Size + cells) * Size, (Size - cells) * Size).Clear(); // z >= cells
            for (var z = 0; z < Math.Min(cells, Cells); z++)
            {
                draw.AsSpan((y * Size + z) * Size + cells, Size - cells).Clear(); // x >= cells
            }
        }
    }

    private static void Skirted(Block[] blocks, Block[] fluids, Block air)
    {
        var sides = new Sides(
            OpenSide(_chunkX - 1, _chunkY, _chunkZ, fluids, 0, 1, Ext, TessSeams.Plane),
            OpenSide(_chunkX + 1, _chunkY, _chunkZ, fluids, Ext - 1, -1, Ext, TessSeams.Plane),
            OpenSide(_chunkX, _chunkY, _chunkZ - 1, fluids, 0, Ext, 1, TessSeams.Plane),
            OpenSide(_chunkX, _chunkY, _chunkZ + 1, fluids, TessSeams.Plane - Ext, -Ext, 1, TessSeams.Plane),
            OpenSide(_chunkX, _chunkY - 1, _chunkZ, fluids, 0, TessSeams.Plane, 1, Ext),
            OpenSide(_chunkX, _chunkY + 1, _chunkZ, fluids, ExtCells - TessSeams.Plane, -TessSeams.Plane, 1, Ext));
        if (!sides.Any) return;

        for (var y = 0; y < Ext; y++)
        {
            for (var i = 0; i < Ext; i++)
            {
                if (sides.MinusX) blocks[(y * Ext + i) * Ext] = air;

                if (sides.PlusX) blocks[(y * Ext + i) * Ext + Ext - 1] = air;

                if (sides.MinusZ) blocks[y * Ext * Ext + i] = air;

                if (sides.PlusZ) blocks[(y * Ext + Ext - 1) * Ext + i] = air;
            }
        }

        if (sides.MinusY) blocks.AsSpan(0, Ext * Ext).Fill(air);

        if (sides.PlusY) blocks.AsSpan((Ext - 1) * Ext * Ext, Ext * Ext).Fill(air);
    }

    private static bool OpenSide(int x, int y, int z, Block[] fluids, int start, int inward, int across, int down) =>
        IsCoarse(BandOf(x, y, z)) && (_band != FullDetail || !FluidAlongSide(fluids, start, inward, across, down));

    // Keep the solid samples that a liquid block on the inner layer can see, including diagonal halo samples.
    internal static bool FluidAlongSide(Block[] fluids, int start, int inward, int across, int down)
    {
        for (var row = 0; row < Ext; row++)
        {
            for (var column = 0; column < Ext; column++)
            {
                var at = start + row * down + column * across;
                if (fluids[at] is { BlockId: not 0 } || fluids[at + inward] is { BlockId: not 0 }) return true;
            }
        }

        return false;
    }

    // Source coordinates of a coarse one on one axis, of `cells` cells halving a grid whose border layer is at `last`:
    // the border layer, or the cell's two
    private static (int From, int Count) Source(int c, int cells, int last)
    {
        if (c == 0) return (0, 1);

        if (c == cells + 1) return (last, 1);

        return (2 * c - 1, 2);
    }

    // The cell's top-most block of a kind where at least half of it holds one, else null
    internal static Block? Cell(Block?[] source, int x, int y, int z, int cells = Cells, int last = Ext - 1)
    {
        var ((x0, nx), (y0, ny), (z0, nz)) = (Source(x, cells, last), Source(y, cells, last), Source(z, cells, last));
        var (count, sparse, total) = (0, 0, nx * ny * nz);
        Span<int> layers = stackalloc int[6]; // solid blocks per layer: y 0, 1; z 0, 1; x 0, 1
        layers.Clear();
        Block? top = null;
        for (var dy = 0; dy < Math.Min(ny, 2); dy++)
        {
            for (var dz = 0; dz < Math.Min(nz, 2); dz++)
            {
                for (var dx = 0; dx < Math.Min(nx, 2); dx++)
                {
                    var block = source[((y0 + dy) * Ext + z0 + dz) * Ext + x0 + dx];
                    if (block is null || !Solidish(block)) continue;

                    count++;
                    (layers[dy], layers[2 + dz], layers[4 + dx]) = (layers[dy] + 1, layers[2 + dz] + 1, layers[4 + dx] + 1);
                    if (KindOf(block) == Sparse) sparse++;

                    top = block; // y runs upward last-written wins: the top-most
                }
            }
        }

        // a border cell sees only the neighbour's adjacent layer, where the neighbour's own cell weighs two: solid only
        // when that layer is all solid (then the neighbour's cell is too), else this side's face is drawn, never a hole
        if (nx == 1 || ny == 1 || nz == 1) return count == total ? top : null;
        // a trunk or a thin crown fills a quarter of a cell: kept, or far trees float as loose leaves; a whole layer (a
        // ground one block thick over a cave, a thin wall) keeps the cell too, else one looks into the dark below
        var layered = false;
        foreach (var n in layers.Bounded(6))
        {
            layered |= n == 4;
        }

        return 2 * count >= total || (sparse > 0 && 4 * count >= total) || layered ? top : null;
    }

    // Brightest of the cell's light, byte by byte
    internal static int Brightest(int[] source, int x, int y, int z, int cells = Cells, int last = Ext - 1)
    {
        var ((x0, nx), (y0, ny), (z0, nz)) = (Source(x, cells, last), Source(y, cells, last), Source(z, cells, last));
        uint best = 0;
        for (var dy = 0; dy < Math.Min(ny, 2); dy++)
        {
            for (var dz = 0; dz < Math.Min(nz, 2); dz++)
            {
                for (var dx = 0; dx < Math.Min(nx, 2); dx++)
                {
                    var v = (uint)source[((y0 + dy) * Ext + z0 + dz) * Ext + x0 + dx];
                    best = Math.Max(best & 0xFF, v & 0xFF) | Math.Max(best & 0xFF00, v & 0xFF00) |
                           Math.Max(best & 0xFF0000, v & 0xFF0000) | Math.Max(best & 0xFF000000, v & 0xFF000000);
                }
            }
        }

        return (int)best;
    }

    // Blocks a coarse cell may become: whole cubes, topsoil, snow layers, leaves; nothing with a block entity
    private static bool Solidish(Block block) => KindOf(block) is Solid or Sparse;

    private static byte KindOf(Block block)
    {
        var id = block.BlockId;
        if (id == 0) return Air;

        var kinds = Kinds;
        if ((uint)id >= (uint)kinds.Length) kinds = Classify(id);

        return (uint)id < (uint)kinds.Length ? kinds[id] : Air;
    }

    private static byte[] Classify(int id)
    {
        if (_api?.World?.Blocks is not { } all || !Index(id, MaxKinds)) return [];

        var kinds = new byte[Math.Max(all.Count, id + 1)];
        for (var i = 0; i < Math.Min(all.Count, MaxKinds); i++)
        {
            if (all[i] is { } b && b.BlockId == i) kinds[i] = Kind(b);
        }

        Kinds = kinds;
        return kinds;
    }

    private static byte Kind(Block b)
    {
        if (b.BlockId == 0 || b.EntityClass is not null) return Air;

        var leaves = b is { DrawType: EnumDrawType.JSON, BlockMaterial: EnumBlockMaterial.Leaves };
        var solid = b.DrawType is EnumDrawType.Cube or EnumDrawType.TopSoil ||
                    b.DrawType is >= EnumDrawType.BlockLayer_1 and <= EnumDrawType.BlockLayer_7;
        if (leaves || (solid && b.BlockMaterial == EnumBlockMaterial.Wood)) return Sparse;

        return solid ? Solid : Air;
    }
}
