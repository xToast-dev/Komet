using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Komet.World;

// Three of the integrated server's generators and its chunk thread make garbage the client pays for in singleplayer (one heap, one
// set of GC pauses): together ~1 GB a minute flying into new terrain, measured with allocation traces.
// - ChunkIlluminator.SpreadSunLightInColumn pushed a new BlockPos for every position its sunlight flood reached (200 MB a minute). The
//   positions come from a pool per thread now, and a popped one goes back to it when the flood pops the next: the flood reads a popped
//   position only during its turn (GetLightAbsorptionAt reads its coordinates), and the callers only push new positions and hand over
//   the stack. The flood's order and values stay the engine's.
// - GenRockStrataNew.genBlockColumn made a float[] of province weights per block column (95 MB): the thread's array of that length,
//   cleared as a new one is, which LerpedWeightedIndex2DMap.WeightsAt fills and nothing keeps.
// - GenVegetationAndPatches.genPatches asked RegistryObject.LastCodePart(0) for every rock it looks under a patch at, which splits the
//   block's code anew each time (130 MB): the last part of each block's code is kept, codes do not change once the game runs.
// - ServerSystemSupplyChunks.moveRequestsToGeneratingQueue made a new List<long> on every chunk thread tick, and the thread ticks
//   without sleeping while it has work (11 MB/s, 2026-10-08 trace): the thread's list, emptied. The list only collects the requests
//   it hands on in the same call (Add, Count and the indexer, checked on the IL).
// Another mod's patch on one of the four found at Install leaves that one as shipped.
internal static class WorldGenScratch
{
    private const int MaxInstructions = 4096, MaxPooled = 1 << 14, MaxWeights = 4096;
    private const string Rock = "Vintagestory.ServerMods.GenRockStrataNew", Vegetation = "Vintagestory.ServerMods.GenVegetationAndPatches",
        Supply = "Vintagestory.Server.ServerSystemSupplyChunks";

    public const int AllBits = 15;

    [ThreadStatic] private static Stack<BlockPos>? _free;
    [ThreadStatic] private static BlockPos? _popped;
    [ThreadStatic] private static float[]? _weights;
    [ThreadStatic] private static List<long>? _requests;
    private static readonly ConditionalWeakTable<RegistryObject, string> LastParts = [];

    public static bool Enabled { get; set; } = true;
    public static int Rewritten { get; private set; } // 1 sunlight, 2 rock weights, 4 code parts, 8 chunk requests

    internal static (MethodBase? Sun, MethodBase? Rock, MethodBase? Patches, MethodBase? Requests) Targets() =>
    (
        AccessTools.DeclaredMethod(typeof(ChunkIlluminator), "SpreadSunLightInColumn"),
        AccessTools.TypeByName(Rock) is { } rock && Assert(Rock.Length > 0) ? AccessTools.DeclaredMethod(rock, "genBlockColumn") : null,
        AccessTools.TypeByName(Vegetation) is { } veg ? AccessTools.DeclaredMethod(veg, "genPatches") : null,
        AccessTools.TypeByName(Supply) is { } supply ? AccessTools.DeclaredMethod(supply, "moveRequestsToGeneratingQueue", []) : null
    );

    public static void Install(Harmony harmony)
    {
        Rewritten = 0;
        if (!NotNull(harmony) || !Assert(MaxPooled > 0)) return;
        var (sun, rock, patches, requests) = Targets();
        Patch(harmony, sun, nameof(RewriteSun));
        Patch(harmony, rock, nameof(RewriteRock));
        Patch(harmony, patches, nameof(RewritePatches));
        Patch(harmony, requests, nameof(RewriteRequests));
    }

    private static void Patch(Harmony harmony, MethodBase? target, string transpiler)
    {
        if (target is null || !NotNull(harmony) || !Assert(transpiler.Length > 0) || EngineShape.FirstForeign([target], harmony.Id) >= 0) return;
        _ = NotNull(harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(WorldGenScratch), transpiler))));
    }

    // The one newobj BlockPos(x, y, z, dim) and the one Stack<BlockPos>.Pop
    internal static List<CodeInstruction> RewriteSun(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var made = Il.Single(code, static c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo { DeclaringType: var t } ctor &&
                                               t == typeof(BlockPos) && ctor.GetParameters().Length == 4);
        var pop = Il.Single(code, static c => c.operand is MethodInfo { Name: "Pop" } m && m.DeclaringType == typeof(Stack<BlockPos>));
        if (made < 0 || pop < 0 || !Assert(made != pop)) return code;
        var (take, popped) = (AccessTools.Method(typeof(WorldGenScratch), nameof(Take)), AccessTools.Method(typeof(WorldGenScratch), nameof(Pop)));
        if (NotNull(take) && Il.Substitute(code, made, take) && Il.Substitute(code, pop, popped)) Rewritten |= 1;
        return code;
    }

    // The one newarr float32
    internal static List<CodeInstruction> RewriteRock(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var at = Il.Single(code, static c => c.opcode == OpCodes.Newarr && c.operand is Type t && t == typeof(float));
        if (at >= 0 && Index(at, code.Count) && Il.Substitute(code, at, AccessTools.Method(typeof(WorldGenScratch), nameof(Weights))))
            Rewritten |= 2;
        return code;
    }

    // The one RegistryObject.LastCodePart(int)
    internal static List<CodeInstruction> RewritePatches(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var at = Il.Single(code, static c => c.operand is MethodInfo { Name: nameof(RegistryObject.LastCodePart) } m &&
                                             m.DeclaringType == typeof(RegistryObject));
        if (at >= 0 && Index(at, code.Count) && Il.Substitute(code, at, AccessTools.Method(typeof(WorldGenScratch), nameof(LastCodePart)))) Rewritten |= 4;
        return code;
    }

    // The one newobj List<long>(), its local only counted, indexed and added to
    internal static List<CodeInstruction> RewriteRequests(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var made = Il.Single(code, static c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo { DeclaringType: var t } ctor &&
                                               t == typeof(List<long>) && ctor.GetParameters().Length == 0);
        if (made < 0 || !Index(made + 1, code.Count) || !code[made + 1].IsStloc() ||
            !Il.Confined(code, made + 1, static (use, operand) => operand == 0 && use.operand is MethodInfo
                { Name: "Add" or "get_Count" or "get_Item", DeclaringType: var t } && t == typeof(List<long>)))
            return code;
        if (Il.Substitute(code, made, AccessTools.Method(typeof(WorldGenScratch), nameof(Requests)))) Rewritten |= 8;
        return code;
    }

    internal static List<long> Requests()
    {
        if (!Enabled) return [];
        var requests = _requests ??= [];
        requests.Clear();
        _ = Assert(requests.Count == 0);
        return requests;
    }

    internal static BlockPos Take(int x, int y, int z, int dimension)
    {
        if (!Enabled || _free is not { Count: > 0 } free || !Assert(free.Count <= MaxPooled)) return new BlockPos(x, y, z, dimension);
        var pos = free.Pop();
        (pos.X, pos.Y, pos.Z, pos.dimension) = (x, y, z, dimension);
        return pos;
    }

    // The flood's previous position goes back as it takes the next
    internal static BlockPos Pop(Stack<BlockPos> stack)
    {
        if (!NotNull(stack)) return stack.Pop();
        if (Enabled && _popped is { } done)
        {
            var free = _free ??= new Stack<BlockPos>();
            if (free.Count < MaxPooled) free.Push(done);
        }

        var next = stack.Pop();
        _ = Assert(_free is null || _free.Count <= MaxPooled);
        _popped = next;
        return next;
    }

    internal static float[] Weights(int length)
    {
        if (!Enabled || !Index(length, MaxWeights)) return new float[length];
        if (_weights is not { } weights || weights.Length != length) return _weights = new float[length];
        _ = Assert(weights.Length == length);
        Array.Clear(weights);
        return weights;
    }

    internal static string LastCodePart(RegistryObject block, int posFromRight)
    {
        if (!Enabled || posFromRight != 0 || !NotNull(block)) return block.LastCodePart(posFromRight);
        if (LastParts.TryGetValue(block, out var known)) return known;
        var part = block.LastCodePart(0);
        LastParts.AddOrUpdate(block, part);
        return part;
    }
}
