using Komet.Diagnostics;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// FarLod: scaling the meshes and refining chunks as the player moves.
internal static partial class FarLod
{
    // BuildBlockPolygons postfix: the coarse meshes and bounds at full size
    private static void Scaled(ChunkTesselator __instance)
    {
        if (!Coarse || Vars(__instance) is not { } vars) return;

        var f = _band == Farthest ? 4 : 2;
        Scale(Centre(__instance), f);
        Scale(Edges(__instance), f);
        (vars.xMin, vars.xMax, vars.yMin, vars.yMax, vars.zMin, vars.zMax) =
            (vars.xMin * f, vars.xMax * f, vars.yMin * f, vars.yMax * f, vars.zMin * f, vars.zMax * f);
    }

    private static void Scale(MeshData?[]?[]?[]? byLod, int factor)
    {
        if (byLod is null) return;

        foreach (var byPass in byLod.Bounded(8))
        {
            if (byPass is null) continue;

            foreach (var byAtlas in byPass.Bounded(64))
            {
                if (byAtlas is null) continue;

                foreach (var mesh in byAtlas.Bounded(64))
                {
                    if (mesh?.xyz is not { } xyz) continue;

                    var n = Math.Min(3 * mesh.VerticesCount, xyz.Length);
                    for (var i = 0; i < Math.Min(n, 3 << 22); i++)
                    {
                        xyz[i] *= factor;
                    }
                }
            }
        }
    }

    // Main thread: the player and threshold, then a slice of the round re-marked where a chunk's band changed. The
    // band is announced at the mark, so neighbours tessellated before the chunk's own pass close their sides to it,
    // while the table keeps the band drawn until that pass.
    private static void Scan(float _)
    {
        if (_api is not { World.Player.Entity: { } entity } api || api.World is not ClientMain game) return;
        var threshold = Distance(api.Render.FrameHeight * FrameSettings.Ssaa, FrameSettings.FieldOfView, Pixels);
        var (px, pz) = (entity.Pos.X, entity.Pos.Z);
        Threshold = threshold;
        Volatile.Write(ref _x, px);
        Volatile.Write(ref _z, pz);
        var published = double.IsFinite(threshold) ? threshold : double.MaxValue;
        Volatile.Write(ref _threshold, published);
        if (Pixels <= 0 && Table.Bands.IsEmpty) return;
        var (marks, now, busy) = (0, Environment.TickCount64, Backlogged);
        if (Pixels > 0) Refine(game, px, pz, threshold, now);

        for (var n = 0; n < MaxUnannounced && marks < MaxMarks && Unannounced.TryDequeue(out var told); n++)
        {
            var (x, y, z) = ChunkKey.Unpack(told); marks += Neighbours(game, x, y, z, now);
        }

        Table.Begin(px, pz, published, now);
        for (var n = 0; n < PerScan && marks < MaxMarks && Table.TryNext(game, now, out var key, out var band); n++)
        {
            if (band == 0) continue;

            var (x, y, z) = ChunkKey.Unpack(key); var want = Pixels > 0 ? Want(Spread(x, z, px, pz), band, threshold) : Near;
            if (want == band && Pixels <= 0) Table.Forget(key);

            if (want == band || (want > band && busy))
            {
                if (Table.Drawn.ContainsKey(key)) Table.Announce(key, band);

                continue;
            }

            Table.Announce(key, (byte)want);
            if (Table.Mark(game, x, y, z, now) == 0) continue;
            marks += 1 + Neighbours(game, x, y, z, now);
        }
    }

    // Close terrain first: the round walks every tracked chunk in no order and over a far view takes seconds, so a chunk
    // the player came near stayed coarse that long. Columns inside the near disc are checked ring by ring from the
    // player, each one drawn coarser than wanted re-marked at once on a budget of its own; settled until the player
    // moves a few blocks (or a second passes), again next tick while the budget ran out.
    private static void Refine(ClientMain game, double px, double pz, double threshold, long now)
    {
        if (!double.IsFinite(threshold) || threshold <= 0) return;

        var moved = !(Math.Abs(px - _refinedX) < Band / 4 && Math.Abs(pz - _refinedZ) < Band / 4);
        if (!moved && now - _refinedAt < RefineMs) return;

        var (reach, height) = ((int)Math.Min(Math.Ceiling(threshold / (1 - Ragged) / Size) + 1, MaxReach),
            game.WorldMap.ChunkMapSizeY);
        var (ox, oz, marks) = ((int)Math.Floor(px / Size), (int)Math.Floor(pz / Size), 0);
        for (var r = 0; r <= Math.Min(reach, MaxReach) && marks < MaxRefines; r++)
        {
            for (var j = 0; j < Math.Min(2 * r + 1, 2 * MaxReach + 1) && marks < MaxRefines; j++)
            {
                for (var side = 0; side < 4 && marks < MaxRefines; side++)
                {
                    var i = j - r;
                    // the ring's four sides; the corners belong to the first two only
                    if (r > 0 && side >= 2 && (i == -r || i == r)) continue;

                    var (cx, cz) = side switch
                    {
                        0 => (ox + i, oz - r),
                        1 => (ox + i, oz + r),
                        2 => (ox - r, oz + i),
                        _ => (ox + r, oz + i)
                    };
                    if (r == 0 && side > 0) break;

                    var d = Spread(cx, cz, px, pz);
                    if (d >= threshold) continue;

                    for (var cy = 0; cy < Math.Min(height, Dimension) && marks < MaxRefines; cy++)
                    {
                        marks += Refined(game, cx, cy, cz, d, threshold, now);
                    }
                }
            }
        }

        if (marks < MaxRefines) (_refinedX, _refinedZ, _refinedAt) = (px, pz, now);
    }

    // A chunk drawn coarser than wanted at distance d marked with its banded neighbours: the marks made
    private static int Refined(ClientMain game, int x, int y, int z, double d, double threshold, long now)
    {
        var key = ChunkKey.Pack(x, y, z);
        if (key < 0 || !Table.Bands.ContainsKey(key) || Table.Waiting(key, now)) return 0;

        var drawn = Table.DrawnBand(key);
        if (!IsCoarse(drawn) || (Want(d, drawn, threshold) is var want && want >= drawn)) return 0;

        Table.Announce(key, (byte)want);
        if (Table.Mark(game, x, y, z, now) == 0) return 0;

        return 1 + Neighbours(game, x, y, z, now);
    }

    // The six facing a chunk whose band changed: a coarse one's walls, a full one's open side
    private static int Neighbours(ClientMain game, int x, int y, int z, long now) =>
        Neighbour(game, x - 1, y, z, now) + Neighbour(game, x + 1, y, z, now) +
               Neighbour(game, x, y, z - 1, now) +
               Neighbour(game, x, y, z + 1, now) + Neighbour(game, x, y - 1, z, now) +
               Neighbour(game, x, y + 1, z, now);

    private static int Neighbour(ClientMain game, int x, int y, int z, long now) =>
        ChunkKey.Pack(x, y, z) is >= 0 and var key && (Pixels <= 0 || Table.Bands.ContainsKey(key))
            ? Table.Mark(game, x, y, z, now)
            : 0;

    internal readonly record struct Sides(bool MinusX, bool PlusX, bool MinusZ, bool PlusZ, bool MinusY, bool PlusY)
    {
        public bool Any => MinusX || PlusX || MinusZ || PlusZ || MinusY || PlusY;
    }

    private readonly record struct Grid(
        Block[] Blocks,
        int[] Light,
        Block?[] SourceBlocks,
        int[] SourceLight);
}
