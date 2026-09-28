using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// ChunkRenderer keeps each pass's chunk meshes in pools of ClientSettings.ModelDataPoolMaxVertexSize vertices (500 000 by default),
// and every pool is a buffer of its own: one draw call per pool and pass, one culling job, one origin uniform. At the bench world's
// view distance the big passes hold about 110 pools each, 1300 draws a frame over all of them. A chunk pass's second pool is now twice
// the size and every later one Scale times (vertices, indices and parts alike), so the same meshes fill a quarter of the pools: in the
// bench 134 -> 144 FPS, 1 % low 41 -> 45, frames over 25 ms 26 -> 15 a minute. A pass that never fills its first pool (liquids, meta
// blocks) keeps the size it had, so the memory held for nothing grows by at most one large pool per big pass. Where a mesh lands does
// not change what is drawn: every pass depth tests or blends order-independently, and the engine's own placement is first fit. The
// size is read when a pool is made, so a change applies to the pools made after it, fully after rejoining; Scale 1 is the engine.
internal static class PoolScale
{
    public const int Engine = 1, MaxScale = 8, DefaultScale = 4;
    private const int MaxInstructions = 512, MaxSites = 16;

    private static readonly ConditionalWeakTable<MeshDataPoolManager, object> Chunks = [];
    private static readonly object Marker = new();
    private static int _sites;

    public static int Scale { get; set; } = DefaultScale;
    public static bool Rewritten => _sites > 0;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
    private static extern ref List<MeshDataPool> Pools(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "defaultVertexPoolSize")]
    private static extern ref int VertexSize(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "defaultIndexPoolSize")]
    private static extern ref int IndexSize(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "maxPartsPerPool")]
    private static extern ref int PartCount(MeshDataPoolManager manager);

    public static void Install(Harmony harmony)
    {
        _sites = 0;
        var add = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.AddModel),
            [typeof(MeshData), typeof(Vec3i), typeof(int), typeof(Sphere)]);
        var made = AccessTools.DeclaredConstructor(typeof(ChunkRenderer), [typeof(int[]), typeof(ClientMain)]);
        var atlas = AccessTools.DeclaredMethod(typeof(ChunkRenderer), "RuntimeAddBlockTextureAtlas", [typeof(int[])]);
        if (!NotNull(harmony) || !NotNull(add) || !NotNull(made) || !NotNull(atlas)) return;
        _ = NotNull(harmony.Patch(made, postfix: new HarmonyMethod(Registered)));
        _ = NotNull(harmony.Patch(atlas, postfix: new HarmonyMethod(Registered)));
        _ = NotNull(harmony.Patch(add, transpiler: new HarmonyMethod(Rewrite)));
    }

    // Postfix on the ChunkRenderer constructor and RuntimeAddBlockTextureAtlas: its pool managers are the chunk passes
    internal static void Registered(ChunkRenderer __instance)
    {
        if (!NotNull(__instance) || __instance.poolsByRenderPass is not { } passes) return;
        foreach (var pass in passes.Bounded(MaxSites * MaxSites))
            foreach (var manager in (pass ?? []).Bounded(MaxSites * MaxSites))
                if (manager is not null) Chunks.AddOrUpdate(manager, Marker);
    }

    internal static void Register(MeshDataPoolManager manager)
    {
        if (NotNull(manager)) Chunks.AddOrUpdate(manager, Marker);
    }

    // AddModel reads the pool sizes only where it makes a new pool: each read goes through Vertices, Indices or Parts
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        _sites = 0;
        var type = typeof(MeshDataPoolManager);
        (string Field, string Method)[] reads = [("defaultVertexPoolSize", nameof(Vertices)),
            ("defaultIndexPoolSize", nameof(Indices)), ("maxPartsPerPool", nameof(Parts))];
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var found = 0;
        foreach (var (field, method) in reads.Bounded(MaxSites))
        {
            var (read, scaled) = (AccessTools.DeclaredField(type, field),
                AccessTools.Method(typeof(PoolScale), method));
            if (!NotNull(read) || !NotNull(scaled)) return code;
            for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
                if (code[i].LoadsField(read) && Il.Substitute(code, i, scaled)) found++;
        }

        _sites = found;
        return code;
    }

    // A null manager throws as the engine's ldfld did
    internal static int Vertices(MeshDataPoolManager manager) => VertexSize(manager) * Factor(manager);
    internal static int Indices(MeshDataPoolManager manager) => IndexSize(manager) * Factor(manager);
    internal static int Parts(MeshDataPoolManager manager) => PartCount(manager) * Factor(manager);

    // A chunk pass's first pool as configured, its second twice that, every later one Scale times
    internal static int Factor(MeshDataPoolManager manager)
    {
        var scale = Math.Clamp(Scale, Engine, MaxScale);
        if (scale == Engine || !Chunks.TryGetValue(manager, out _) || Pools(manager) is not { } pools) return Engine;
        return pools.Count switch
        {
            0 => Engine,
            1 => Math.Min(2, scale),
            _ => Assert(pools.Count > 1) ? scale : Engine
        };
    }
}
