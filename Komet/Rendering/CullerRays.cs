using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// The engine's CPU occlusion culling (ChunkCuller, on its own thread) casts three rays from the camera's chunk to every chunk of the
// view's shell and marks the chunks a ray passes visible until a chunk it cannot traverse stops it. Each step asks getExitingFace,
// which tests all six faces of the cell through Vec3d objects, BlockFacing properties and fields of the culler: in the world-join traces
// CullInvisibleChunks and getExitingFace were 11 % of every running sample of the process, a core taken from the tesselation workers.
//
// traverseRayAndMarkVisible is replaced by the same walk on locals: the same double expressions in the same order, the faces in
// BlockFacing.ALLFACES order, only those the ray can leave through (the engine skips the others by the same dot product, worked out
// here once per ray), the same chunk lookups, SetVisible and IsTraversable calls and the same stop conditions. The ray and the
// culler's position fields are left as the engine leaves them. A body that is not the one verified, or another mod's patch on it,
// leaves the engine's.
internal static class CullerRays
{
    // EngineShape of Seams() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0x095C271DADB9F4DAUL;

    private const int Faces = 6, SeamCount = 2, MaxSteps = 1 << 16; // a ray crosses a few hundred chunks at most
    private const double Parallel = 1E-05;

    // Per face in ALLFACES order: its normal and its plane centre, as the engine reads them
    private static readonly int[] NormalX = new int[Faces], NormalY = new int[Faces], NormalZ = new int[Faces];
    private static readonly double[] CenterX = new double[Faces], CenterY = new double[Faces], CenterZ = new double[Faces];
    private static readonly BlockFacing?[] Facings = new BlockFacing?[Faces];
    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    public static bool Enabled { get; set; } = true;

    // Not the verified body, or another mod patches it: the engine's walk
    public static bool Blocked { get; private set; } = true;

    public static bool Matched => _shaped;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunks")]
    private static extern ref Dictionary<long, ClientChunk>? Chunks(ClientWorldMap map);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetVisible")]
    private static extern void SetVisible(ClientChunk chunk, bool visible);

    public static void Install(Harmony harmony, ILogger? logger = null, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _foreign, _logger) = (false, true, false, logger);
        _seams = Seams();
        if (!NotNull(harmony) || _seams.Length != SeamCount || _seams[0] is not MethodInfo walk || !Faced()) return;
        _shaped = EngineShape.Matches(_seams, fingerprint, nameof(CullerRays), logger);
        if (!_shaped || !Assert(Facings.Length == Faces)) return;
        _ = NotNull(harmony.Patch(walk, new HarmonyMethod(Walk)));
        Recheck();
    }

    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == SeamCount);
        _foreign = EngineShape.Report(_logger, nameof(CullerRays), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.All, null, typeof(CullerRays)));
        Blocked = !seamed || _foreign;
    }

    // [0] is replaced, [1] is what it called per step
    internal static MethodBase?[] Seams()
    {
        var (culler, cell, d) = (typeof(ChunkCuller), typeof(Vec3i), typeof(double));
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(culler, "traverseRayAndMarkVisible", [cell, cell, d, d]),
            AccessTools.DeclaredMethod(culler, "getExitingFace", [cell])
        ];
        return Assert(seams.Length == SeamCount) ? seams : [];
    }

    // The six faces' normals and plane centres, read once (BlockFacing's are static and never written)
    internal static bool Faced()
    {
        if (BlockFacing.ALLFACES is not { Length: Faces } all) return false;
        for (var i = 0; i < Faces; i++)
        {
            if (all[i] is not { Normali: { } normal, PlaneCenter: { } center } face) return false;
            (Facings[i], NormalX[i], NormalY[i], NormalZ[i]) = (face, normal.X, normal.Y, normal.Z);
            (CenterX[i], CenterY[i], CenterZ[i]) = (center.X, center.Y, center.Z);
        }

        return Assert(Facings[Faces - 1] is not null);
    }

    // traverseRayAndMarkVisible(fromPos, toPosRel, yoffset, xoffset)
    private static bool Walk(ChunkCuller __instance, Vec3i fromPos, Vec3i toPosRel, double yoffset, double xoffset,
        ClientMain ___game, bool ___isAboveHeightLimit, Vec3i ___toPos)
    {
        if (!Enabled || Blocked || !NotNull(__instance) || ___game?.WorldMap is not { } map || Chunks(map) is not { } chunks ||
            fromPos is null || toPosRel is null || ___toPos is null || __instance.ray is not { origin: { } o, dir: { } dir } ||
            __instance.curpos is not { } cur) return true;
        _ = o.Set(fromPos.X + xoffset, fromPos.Y + yoffset, fromPos.Z + 0.5);
        _ = dir.Set(toPosRel.X + xoffset, toPosRel.Y + yoffset, toPosRel.Z + 0.5);
        _ = ___toPos.Set(fromPos.X + toPosRel.X, fromPos.Y + toPosRel.Y, fromPos.Z + toPosRel.Z);
        var (ox, oy, oz, dx, dy, dz) = (o.X, o.Y, o.Z, dir.X, dir.Y, dir.Z);

        Span<int> exits = stackalloc int[Faces];
        Span<double> dots = stackalloc double[Faces];
        var n = Exits(dx, dy, dz, exits, dots);

        var (fx, fy, fz) = (fromPos.X, fromPos.Y, fromPos.Z);
        var (cx, cy, cz) = (fx, fy, fz);
        var reach = Math.Abs(fx - ___toPos.X) + Math.Abs(fy - ___toPos.Y) + Math.Abs(fz - ___toPos.Z);
        _ = Assert(reach >= 0);
        var (mulX, mulZ) = (map.index3dMulX, map.index3dMulZ);
        BlockFacing? entry = null;
        for (var step = 0; step < MaxSteps; step++)
        {
            var walked = Math.Abs(cx - fx) + Math.Abs(cy - fy) + Math.Abs(cz - fz);
            if (walked > reach + 2) break;
            var face = Exit(exits[..n], dots[..n], cx, cy, cz, ox, oy, oz, dx, dy, dz);
            if (face < 0) break;
            var exit = Facings[face]!;
            var key = ((long)cy * mulZ + cz) * mulX + cx;
            _ = chunks.TryGetValue(key, out var chunk); // unlocked, as the engine reads it
            if (chunk != null)
            {
                SetVisible(chunk, true);
                if (walked > 1 && !chunk.IsTraversable(entry, exit)) break;
            }

            (cx, cy, cz) = (cx + NormalX[face], cy + NormalY[face], cz + NormalZ[face]);
            entry = exit.Opposite;
            if (!map.IsValidChunkPosFast(cx, cy, cz) && (!___isAboveHeightLimit || cy <= 0)) break;
        }

        _ = cur.Set(cx, cy, cz);
        return false;
    }

    // The faces a ray in this direction can leave a cell through, in ALLFACES order, with the engine's dot product; how many
    internal static int Exits(double dx, double dy, double dz, Span<int> exits, Span<double> dots)
    {
        var n = 0;
        _ = Assert(exits.Length >= 3) && Assert(dots.Length >= exits.Length);
        for (var i = 0; i < Faces; i++)
        {
            var dot = NormalX[i] * dx + NormalY[i] * dy + NormalZ[i] * dz;
            if (dot <= Parallel || !Index(n, exits.Length)) continue;
            (exits[n], dots[n]) = (i, dot);
            n++;
        }

        _ = Assert(n <= 3); // a ray leaves through at most one face per axis
        return n;
    }

    // getExitingFace: the first face, in ALLFACES order, whose plane the ray meets ahead within the face, or -1
    internal static int Exit(ReadOnlySpan<int> exits, ReadOnlySpan<double> dots, int cx, int cy, int cz, double ox, double oy,
        double oz, double dx, double dy, double dz)
    {
        for (var k = 0; k < Math.Min(exits.Length, Faces); k++)
        {
            var i = exits[k];
            _ = Index(i, Faces) && Assert(dots[k] > Parallel);
            var (px, py, pz) = (cx + CenterX[i], cy + CenterY[i], cz + CenterZ[i]);
            var (ax, ay, az) = (px - ox, py - oy, pz - oz);
            var t = (ax * NormalX[i] + ay * NormalY[i] + az * NormalZ[i]) / dots[k];
            if (t >= 0.0 && Math.Abs(ox + dx * t - px) <= 0.5 && Math.Abs(oy + dy * t - py) <= 0.5 &&
                Math.Abs(oz + dz * t - pz) <= 0.5)
                return i;
        }

        return -1;
    }
}
