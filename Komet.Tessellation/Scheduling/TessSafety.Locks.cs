using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Komet.Core;
using Komet.Api;
using Vintagestory.API.MathTools;

namespace Komet.Tessellation;

// Which lock a block's OnJsonTesselation takes: none (Block's own, unpatched, or an empty override), the fence family's,
// the block's own, or none with a per-thread copy of the mesh's flags for the FlagWriters overrides
internal static partial class TessSafety
{
    private const int MaxBlocks = 1 << 20, MaxDepth = 32, MaxEmpty = 16; // MaxDepth: of a block's class hierarchy
    private const int MaxCopies = 4096;
    private const string SharedFamily = "Vintagestory.GameContent.BlockFenceStackAware";

    private static readonly object FenceGate = new();

    // Block.OnJsonTesselation's parameters, before JsonHook: static fields are initialised in order
    internal static readonly Type[] JsonArgs =
    [
        typeof(MeshData).MakeByRefType(), typeof(int[]).MakeByRefType(), typeof(BlockPos), typeof(Block[]), typeof(int)
    ];

    private static readonly MethodInfo? JsonHook =
        AccessTools.DeclaredMethod(typeof(Block), nameof(Block.OnJsonTesselation), JsonArgs);

    private static readonly string[] FlagWriters =
    [
        "Vintagestory.GameContent.BlockPlant", "Vintagestory.GameContent.BlockFern",
        "Vintagestory.GameContent.BlockFernTree",
        "Vintagestory.GameContent.BlockSeaweed", "Vintagestory.GameContent.BlockWithLeavesMotion",
        "Vintagestory.GameContent.BlockHangingLichen" // MeshData.ToggleWindModeSetWindData: the flags, from the xyz
    ];

    private static readonly int[] CallsBlock = new int[FlagWriters.Length]; // 1: calls Block's own, -1: not, 0: unread
    private static readonly object OwnFlags = new(); // GateOf for FlagWriters: no lock, per-thread flags

    private static readonly System.Func<object, object>? Shallow =
        AccessTools.Method(typeof(object), "MemberwiseClone") is { } clone
            ? AccessTools.MethodDelegate<System.Func<object, object>>(clone)
            : null;

    // This thread's copies of shared meshes, flags its own, for FlagWriters overrides
    [ThreadStatic] private static Dictionary<MeshData, MeshData>? _flagCopies;

    private static BlockGate?[] _gates = [];

    // FlagWriters block (override called via Json); not while a rewrite is broken: doMesh may hit the shared mesh
    internal static bool WritesFlags(Block block) => !Broken && ReferenceEquals(GateOf(block), OwnFlags);

    // Replaces doMesh's virtual call (null throws alike); FlagWriters blocks get the thread's copy of the handed mesh
    internal static void Json(Block block, ref MeshData? sourceMesh, ref int[] lights, BlockPos pos, Block[] blocks,
        int index)
    {
        if (_guarded && sourceMesh is { Flags: not null } shared && ReferenceEquals(GateOf(block), OwnFlags))
        {
            sourceMesh = FlagCopy(shared);
        }

        block.OnJsonTesselation(ref sourceMesh, ref lights, pos, blocks, index);
    }

    private static MeshData FlagCopy(MeshData shared)
    {
        var copies = _flagCopies ??= new Dictionary<MeshData, MeshData>(ReferenceEqualityComparer.Instance);
        if (!copies.TryGetValue(shared, out var mine) || !Alike(mine, shared))
        {
            if (copies.Count >= MaxCopies || Shallow is null) copies.Clear();

            if (Shallow?.Invoke(shared) is not MeshData copy) return shared;

            copy.Flags = new int[shared.Flags.Length];
            copies[shared] = mine = copy;
        }

        // only the flags in use: overrides and mesher index below VerticesCount; the rest is capacity
        var used = Math.Min(Math.Max(shared.VerticesCount, shared.FlagsCount),
            Math.Min(shared.Flags.Length, mine.Flags.Length));
        Array.Copy(shared.Flags, mine.Flags, Math.Max(used, 0));
        return mine;
    }

    // All arrays but flags, and the counts, still the shared mesh's; else it was rebuilt in place: copy anew
    private static bool Alike(MeshData mine, MeshData shared) =>
        ReferenceEquals(mine.xyz, shared.xyz) && ReferenceEquals(mine.Uv, shared.Uv) &&
               ReferenceEquals(mine.Rgba, shared.Rgba) && ReferenceEquals(mine.Indices, shared.Indices) &&
               mine.VerticesCount == shared.VerticesCount && mine.IndicesCount == shared.IndicesCount &&
               mine.FlagsCount == shared.FlagsCount && mine.Flags.Length == shared.Flags.Length;

    // Block's meshing lock: none for Block's own OnJsonTesselation (unless patched), the fence family's, or the block
    private static object? GateOf(Block block)
    {
        var (gates, id) = (Volatile.Read(ref _gates), block.BlockId);
        if ((uint)id < (uint)gates.Length && gates[id] is { } known && ReferenceEquals(known.Owner, block))
        {
            return known.Monitor;
        }

        var gate = new BlockGate(block, Pick(block));
        if (!Index(id, MaxBlocks)) return gate.Monitor;

        lock (TableGate)
        {
            var table = _gates;
            if (id >= table.Length) Array.Resize(ref table, Math.Min(Math.Max(id + 1, 2 * table.Length), MaxBlocks));

            table[id] = gate;
            Volatile.Write(ref _gates, table);
        }

        return gate.Monitor;
    }

    private static object? Pick(Block block)
    {
        try
        {
            var type = block.GetType();
            var family = type;
            for (var depth = 0; depth < MaxDepth && family is not null; depth++)
            {
                if (family.FullName == SharedFamily) return FenceGate;

                family = family.BaseType;
            }

            var hook = type.GetMethod(nameof(Block.OnJsonTesselation), BindingFlags.Public | BindingFlags.Instance,
                JsonArgs);
            // Block's own patched by another mod: neither lock-free nor own flags, the patch may share anything
            if (hook?.DeclaringType == typeof(Block))
            {
                return EngineShape.Foreign([JsonHook], KapiPatchKinds.All, null) ? block : null;
            }

            if (hook is not null && Empty(hook)) return null;
            // Block's own patch matters only where an override calls it (BlockFernTree does; BlockPlant, leaves don't)
            return FlagsOnly(type, out var reaches) &&
                   (!reaches || !EngineShape.Foreign([JsonHook], KapiPatchKinds.All, null))
                ? OwnFlags
                : block;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return block;
        }
    }

    // An override that does nothing (BlockFence's): no IL but nops before its ret, and no other mod's patch on it
    private static bool Empty(MethodInfo hook)
    {
        var il = hook.GetMethodBody()?.GetILAsByteArray();
        if (il is not { Length: > 0 } || il[^1] != (byte)OpCodes.Ret.Value) return false;

        for (var i = 0; i < Math.Min(il.Length - 1, MaxEmpty); i++)
        {
            if (il[i] != (byte)OpCodes.Nop.Value) return false;
        }

        return il.Length - 1 <= MaxEmpty && !EngineShape.Foreign([hook], KapiPatchKinds.All, null);
    }

    // Every override down to Block's own is a FlagWriter, none patched by another mod; reaches: one calls Block's own
    private static bool FlagsOnly(Type type, out bool reaches)
    {
        var family = type;
        reaches = false;
        for (var depth = 0; depth < MaxDepth && family is not null && family != typeof(Block); depth++)
        {
            var declared = family.GetMethod(nameof(Block.OnJsonTesselation),
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, JsonArgs);
            var writer = Array.IndexOf(FlagWriters, family.FullName);
            if (declared is not null && (writer < 0 ||
                                         EngineShape.Foreign([declared], KapiPatchKinds.All, null)))
            {
                return false;
            }

            if (declared is not null) reaches |= Reaches(writer, declared);

            family = family.BaseType;
        }

        return family == typeof(Block);
    }

    // Whether a FlagWriters override's original IL calls Block's own; read once per writer (racing reads agree)
    private static bool Reaches(int writer, MethodInfo declared)
    {
        if (JsonHook is not { } hook) return true;

        var known = Volatile.Read(ref CallsBlock[writer]);
        if (known != 0) return known > 0;

        var calls = Il.Count(PatchProcessor.GetOriginalInstructions(declared), c => c.Calls(hook)) !=
                    0; // -1, unreadable: as if it did
        Volatile.Write(ref CallsBlock[writer], calls ? 1 : -1);
        return calls;
    }

    private sealed record BlockGate(Block Owner, object? Monitor);
}
