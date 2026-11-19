using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Plane = Vintagestory.API.Client.Plane;

namespace Komet.Rendering;

// Engine private fields shared by culling and pool patches (an accessor on a missing field throws at first use)
internal static class Fields
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "game")]
    internal static extern ref ClientMain? Game(ClientSystem system);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "currentRenderStage")]
    internal static extern ref EnumRenderStage Stage(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "chunkRenderer")]
    internal static extern ref ChunkRenderer? TerrainOf(ClientMain game);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolsByRenderPass")]
    internal static extern ref MeshDataPoolManager?[]?[]? PassPools(ChunkRenderer renderer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pools")]
    internal static extern ref List<MeshDataPool> Pools(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolLocations")]
    internal static extern ref List<ModelDataPoolLocation> Locations(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "dimensionId")]
    internal static extern ref int Dimension(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "modelRef")]
    internal static extern ref MeshRef ModelRef(MeshDataPool pool);

    // 1.23 renamed the field to poolOriginInternal and made it nullable
    private static readonly bool NewOrigin =
        AccessTools.Field(typeof(MeshDataPool), "poolOriginInternal") is not null;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolOrigin")]
    private static extern ref Vec3i OldPoolOrigin(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolOriginInternal")]
    private static extern ref Vec3i? NewPoolOrigin(MeshDataPool pool);

    internal static Vec3i? PoolOrigin(MeshDataPool pool) =>
        NewOrigin ? NewPoolOrigin(pool) : OldPoolOrigin(pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "poolId")]
    internal static extern ref int PoolId(MeshDataPool pool);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
    internal static extern ref int Version(List<ModelDataPoolLocation> list);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "centerModelPoolLocations")]
    internal static extern ref ModelDataPoolLocation[]? Center(ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "edgeModelPoolLocations")]
    internal static extern ref ModelDataPoolLocation[]? Edge(ClientChunk chunk);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustumCuller")]
    internal static extern ref FrustumCulling Culler(MeshDataPoolManager manager);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "frustum")]
    internal static extern ref Plane[] Planes(FrustumCulling culler);

    // 1.23 dropped BlockPos playerPos and kept only the player's x/z as doubles
    private static readonly bool CullerOrigin =
        AccessTools.Field(typeof(FrustumCulling), "originX") is not null;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "playerPos")]
    private static extern ref BlockPos? OldPlayerPos(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "originX")]
    private static extern ref double OriginX(FrustumCulling culler);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "originZ")]
    private static extern ref double OriginZ(FrustumCulling culler);

    [ThreadStatic] private static BlockPos? _origin;

    internal static BlockPos? PlayerPos(FrustumCulling culler)
    {
        if (!CullerOrigin) return OldPlayerPos(culler);

        // originX/Z are set from BlockPos.X/Z, so the cast is exact. y/dimension are unused by the sweep and
        // the frustum planes are compared separately when a staged batch is reused.
        var pos = _origin ??= new BlockPos(0);
        (pos.X, pos.Y, pos.Z, pos.dimension) = ((int)OriginX(culler), 0, (int)OriginZ(culler), 0);
        return pos;
    }

    // 1.23 moved Hide, LodLevel and CullVisible into PoolCullData slots and turned them into properties; a compiled
    // ldfld would throw MissingFieldException there. Only one accessor of each pair is ever called.
    private static readonly bool SlotFlags =
        AccessTools.Property(typeof(ModelDataPoolLocation), "CullVisible") is not null;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "Hide")]
    private static extern ref bool OldHidden(ModelDataPoolLocation loc);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Hide")]
    private static extern bool NewHidden(ModelDataPoolLocation loc);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "LodLevel")]
    private static extern ref int OldLod(ModelDataPoolLocation loc);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_LodLevel")]
    private static extern int NewLod(ModelDataPoolLocation loc);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "CullVisible")]
    private static extern ref Bools? OldCullVisible(ModelDataPoolLocation loc);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CullVisible")]
    private static extern Bools? NewCullVisible(ModelDataPoolLocation loc);

    internal static bool Hidden(ModelDataPoolLocation loc) => SlotFlags ? NewHidden(loc) : OldHidden(loc);

    internal static int Lod(ModelDataPoolLocation loc) => SlotFlags ? NewLod(loc) : OldLod(loc);

    internal static Bools? CullVisible(ModelDataPoolLocation loc) =>
        SlotFlags ? NewCullVisible(loc) : OldCullVisible(loc);
}
