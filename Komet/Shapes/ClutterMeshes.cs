using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace Komet.Shapes;

// Every clutter block entity (BEBehaviorShapeFromAttributes, survival mod: the pots, crates, books, bones and ruins decor the world
// generator scatters, rock rubble too) builds its mesh on its first tesselation - a copy of its block's mesh, rotated and scaled for
// the position - hands it to the chunk's pools (AddMeshData copies it) and keeps it for the rest of its life. In the world-join traces
// those copies were the largest block of memory the GC promoted that is not chunk data (2026-10-08: ~77 MB of 718 MB in 40 s, every
// one promoted twice), and promoted bytes set the pause length. Sharing equal copies does not work: most clutter types randomise their
// height by position, so hardly two are equal.
//
// The copy is only read while the block entity is tesselated. Where the engine built it during this tesselation (its
// loadMeshDuringTesselation path: no textures to upload, which needs the main thread), the block entity hands it back afterwards: an
// empty mesh in its place and the flag set again, so its next tesselation builds it anew, exactly as the first did. The copy dies
// young. The placeholder is not null, so FromTreeAttributes does not take it for a missing mesh (that would relight the block and
// redraw the chunk). A block entity whose mesh the main thread built keeps it.
//
// Built anew each time, the copies would only trade promotion for allocation (chunks are tesselated again and again while their
// neighbours arrive): inside such a tesselation createMesh's Clone writes into the thread's own scratch mesh instead, the arrays kept
// from one block entity to the next (MeshRecycle reuses the extra ones). The block entity holds the scratch only until the pools have
// copied it; it holds the placeholder afterwards, also when the call threw.
internal static class ClutterMeshes
{
    private const string Behavior = "Vintagestory.GameContent.BEBehaviorShapeFromAttributes";
    private const string Props = "Vintagestory.GameContent.IShapeTypeProps";

    private static readonly MeshData Placeholder = new(4, 6);

    public static bool Enabled { get; set; } = true;
    public static bool Patched { get; private set; }

    private const int MaxInstructions = 1024;

    [ThreadStatic] private static bool _building; // inside a tesselation that builds the mesh, on this thread
    [ThreadStatic] private static MeshData? _scratch;

    public static void Install(Harmony harmony)
    {
        Patched = false;
        if (!NotNull(harmony) || !Assert(Placeholder.VerticesCount == 0) || Target() is not { } target) return;
        Patched = NotNull(harmony.Patch(target, new HarmonyMethod(Built), finalizer: new HarmonyMethod(Drawn)));
        if (Patched && Create() is { } create) _ = NotNull(harmony.Patch(create, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static MethodInfo? Create()
    {
        var method = AccessTools.TypeByName(Behavior) is { } type && AccessTools.TypeByName(Props) is { } props
            ? AccessTools.DeclaredMethod(type, "createMesh", [props, typeof(bool)])
            : null;
        return method is null || Assert(method.ReturnType == typeof(MeshData)) ? method : null;
    }

    // Every MeshData.Clone() in createMesh, else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var (clone, copy) = (AccessTools.DeclaredMethod(typeof(MeshData), nameof(MeshData.Clone), []),
            AccessTools.Method(typeof(ClutterMeshes), nameof(Copy)));
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(clone) || !NotNull(copy)) return code;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(clone) && !Il.Substitute(code, i, copy))
                return code;
        return code;
    }

    // source.Clone(), into the thread's scratch inside a tesselation that builds the mesh. The basic arrays may exceed the counts
    // (the engine sizes them exactly; Rotate, Scale, Translate and AddMeshData read the counts); the extra data goes through
    // CloneExtraData, which MeshRecycle points at the scratch's own arrays and nulls where the source has none.
    internal static MeshData Copy(MeshData source)
    {
        if (!_building || !Enabled || !MeshRecycle.Enabled || !MeshRecycle.Patched || !NotNull(source) || source.xyz is null || source.Indices is null)
            return source.Clone();
        var mesh = _scratch ??= new MeshData(initialiseArrays: false) { Recyclable = true };
        (mesh.VerticesPerFace, mesh.IndicesPerFace) = (source.VerticesPerFace, source.IndicesPerFace);
        mesh.SetVerticesCount(source.VerticesCount);
        mesh.xyz = Into(mesh.xyz, source.xyz, source.XyzCount);
        mesh.Uv = source.Uv is null ? null : Into(mesh.Uv, source.Uv, source.UvCount);
        mesh.Rgba = source.Rgba is null ? null : Into(mesh.Rgba, source.Rgba, source.RgbaCount);
        mesh.Flags = source.Flags is null ? null : Into(mesh.Flags, source.Flags, source.FlagsCount);
        mesh.Indices = Into(mesh.Indices, source.Indices, source.IndicesCount);
        mesh.SetIndicesCount(source.IndicesCount);
        (mesh.VerticesMax, mesh.IndicesMax) = (source.VerticesCount, mesh.Indices.Length);
        // a fresh clone's counts where the source lacks the array; CloneExtraData sets the others
        (mesh.NormalsCount, mesh.XyzFacesCount, mesh.TextureIndicesCount, mesh.ColorMapIdsCount, mesh.RenderPassCount) =
            (0, 0, 0, 0, 0);
        Extra(source, mesh);
        return Assert(mesh.VerticesCount == source.VerticesCount) ? mesh : source.Clone();
    }

    private static T[] Into<T>(T[]? kept, T[] source, int count)
    {
        if (!Assert(count >= 0)) return (T[])source.Clone();
        var target = kept is not null && kept.Length >= count ? kept : new T[Math.Max(count, source.Length)];
        Array.Copy(source, target, Math.Min(count, source.Length));
        return Assert(count <= target.Length) ? target : (T[])source.Clone();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CloneExtraData")]
    private static extern void Extra(MeshData source, MeshData dest);

    // OnTesselation as BEBehaviorShapeFromAttributes declares it (the subclasses that override it call it), with the three fields
    // the patches touch
    internal static MethodInfo? Target()
    {
        var type = AccessTools.TypeByName(Behavior);
        if (!NotNull(type)) return null; // the survival mod is part of the game
        if (AccessTools.DeclaredField(type, "mesh")?.FieldType != typeof(MeshData) ||
            AccessTools.DeclaredField(type, "lod2mesh")?.FieldType != typeof(MeshData) ||
            AccessTools.DeclaredField(type, "loadMeshDuringTesselation")?.FieldType != typeof(bool)) return null;
        var method = AccessTools.DeclaredMethod(type, "OnTesselation", [typeof(ITerrainMeshPool), typeof(ITesselatorAPI)]);
        return method is null || Assert(method.ReturnType == typeof(bool)) ? method : null;
    }

    // Whether this tesselation builds the mesh off the main thread
    private static void Built(bool ___loadMeshDuringTesselation, out bool __state)
    {
        _ = NotNull(Placeholder); // the flag as the engine left it: set where loadMesh runs during this tesselation
        __state = Enabled && ___loadMeshDuringTesselation;
        _building = __state;
    }

    // The mesh built for this tesselation handed back once the pools have their copy (a finalizer: also when the call threw, so no
    // block entity keeps the scratch)
    private static Exception? Drawn(Exception? __exception, ref MeshData? ___mesh, ref MeshData? ___lod2mesh,
        ref bool ___loadMeshDuringTesselation, bool __state)
    {
        _building = false;
        if (!__state || ___mesh is null || ReferenceEquals(___mesh, Placeholder)) return __exception;
        _ = Assert(!ReferenceEquals(___lod2mesh, Placeholder));
        (___mesh, ___lod2mesh) = (Placeholder, null);
        ___loadMeshDuringTesselation = true;
        _ = Assert(Placeholder.VerticesCount == 0); // nothing ever adds to it: drawn by mistake it draws nothing
        return __exception;
    }
}
