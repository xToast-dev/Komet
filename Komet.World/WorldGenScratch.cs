using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Common;

namespace Komet.World;

// Integrated-server garbage the client pays for in singleplayer (~1 GB a minute flying):
// - SpreadSunLightInColumn: a BlockPos per flood step (200 MB/min); pooled, a popped one returns on the next pop.
// - GenRockStrataNew.genBlockColumn: float[] weights per column (95 MB); the thread's cleared array.
// - GenVegetationAndPatches.genPatches: LastCodePart(0) splits the code per rock (130 MB); kept per block.
// - moveRequestsToGeneratingQueue: a List<long> per tick of a spinning thread (11 MB/s); the thread's list.
// A foreign patch on one found at Install leaves it as shipped.
internal static class WorldGenScratch
{
    private const int MaxInstructions = 4096, MaxPooled = 1 << 14, MaxWeights = 4096;
    private const string Rock = "Vintagestory.ServerMods.GenRockStrataNew", Vegetation = "Vintagestory.ServerMods.GenVegetationAndPatches",
        Supply = "Vintagestory.Server.ServerSystemSupplyChunks";

    private const int SunBit = 1, RockBit = 2, PartsBit = 4, RequestsBit = 8;
    public const int AllBits = SunBit | RockBit | PartsBit | RequestsBit;

    [ThreadStatic] private static Stack<BlockPos>? _free;
    [ThreadStatic] private static BlockPos? _popped;
    [ThreadStatic] private static float[]? _weights;
    [ThreadStatic] private static List<long>? _requests;
    private static readonly ConditionalWeakTable<RegistryObject, string> LastParts = [];

    public static bool Enabled { get; set; } = true;
    public static int Rewritten { get; private set; } // the bits of the methods rewritten
    public static int Foreign { get; private set; } // the same bits: another mod patched that one first

    internal static (MethodBase? Sun, MethodBase? Rock, MethodBase? Patches, MethodBase? Requests) Targets() =>
    (
        AccessTools.DeclaredMethod(typeof(ChunkIlluminator), "SpreadSunLightInColumn"),
        AccessTools.TypeByName(Rock) is { } rock ? AccessTools.DeclaredMethod(rock, "genBlockColumn") : null,
        AccessTools.TypeByName(Vegetation) is { } veg ? AccessTools.DeclaredMethod(veg, "genPatches") : null,
        AccessTools.TypeByName(Supply) is { } supply ? AccessTools.DeclaredMethod(supply, "moveRequestsToGeneratingQueue", []) : null
    );

    public static void Install(Harmony harmony)
    {
        (Rewritten, Foreign) = (0, 0);
        if (!NotNull(harmony)) return;
        var (sun, rock, patches, requests) = Targets();
        Patch(harmony, sun, nameof(RewriteSun), SunBit);
        Patch(harmony, rock, nameof(RewriteRock), RockBit);
        Patch(harmony, patches, nameof(RewritePatches), PartsBit);
        Patch(harmony, requests, nameof(RewriteRequests), RequestsBit);
        Ascii85Encode.Install(harmony); // structures' block entity text, the same switch
    }

    private static void Patch(Harmony harmony, MethodBase? target, string transpiler, int bit)
    {
        if (target is null) return;
        if (EngineShape.FirstForeign([target], harmony.Id) >= 0)
        {
            Foreign |= bit;
            return;
        }

        _ = harmony.Patch(target, transpiler: new HarmonyMethod(AccessTools.Method(typeof(WorldGenScratch), transpiler)));
    }

    // The one newobj BlockPos(x, y, z, dim) and the one Stack<BlockPos>.Pop
    internal static List<CodeInstruction> RewriteSun(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        var made = Il.Single(code, static c => c.opcode == OpCodes.Newobj && c.operand is ConstructorInfo { DeclaringType: var t } ctor &&
                                               t == typeof(BlockPos) && ctor.GetParameters().Length == 4);
        var pop = Il.Single(code, static c => c.operand is MethodInfo { Name: "Pop" } m && m.DeclaringType == typeof(Stack<BlockPos>));
        if (made < 0 || pop < 0) return code;
        var (take, popped) = (AccessTools.Method(typeof(WorldGenScratch), nameof(Take)), AccessTools.Method(typeof(WorldGenScratch), nameof(Pop)));
        if (NotNull(take) && Il.Substitute(code, made, take) && Il.Substitute(code, pop, popped)) Rewritten |= SunBit;
        return code;
    }

    // The one newarr float32
    internal static List<CodeInstruction> RewriteRock(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        if (Il.SubstituteSingle(code, static c => c.opcode == OpCodes.Newarr && c.operand is Type t && t == typeof(float),
                AccessTools.Method(typeof(WorldGenScratch), nameof(Weights))))
            Rewritten |= RockBit;
        return code;
    }

    // The one RegistryObject.LastCodePart(int)
    internal static List<CodeInstruction> RewritePatches(IEnumerable<CodeInstruction> instructions)
    {
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code))) return code;
        if (Il.SubstituteSingle(code, static c => c.operand is MethodInfo { Name: nameof(RegistryObject.LastCodePart) } m &&
                m.DeclaringType == typeof(RegistryObject), AccessTools.Method(typeof(WorldGenScratch), nameof(LastCodePart))))
            Rewritten |= PartsBit;
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
        if (Il.Substitute(code, made, AccessTools.Method(typeof(WorldGenScratch), nameof(Requests)))) Rewritten |= RequestsBit;
        return code;
    }

    internal static List<long> Requests()
    {
        if (!Enabled) return [];
        var requests = _requests ??= [];
        requests.Clear();
        return requests;
    }

    internal static BlockPos Take(int x, int y, int z, int dimension)
    {
        if (!Enabled || _free is not { Count: > 0 } free) return new BlockPos(x, y, z, dimension);
        var pos = free.Pop();
        (pos.X, pos.Y, pos.Z, pos.dimension) = (x, y, z, dimension);
        return pos;
    }

    // The flood's previous position goes back as it takes the next
    internal static BlockPos Pop(Stack<BlockPos> stack)
    {
        if (!NotNull(stack)) throw new ArgumentNullException(nameof(stack)); // the engine's own Pop would throw too
        if (Enabled && _popped is { } done)
        {
            var free = _free ??= new Stack<BlockPos>();
            if (free.Count < MaxPooled) free.Push(done);
        }

        var next = stack.Pop();
        _popped = next;
        return next;
    }

    internal static float[] Weights(int length)
    {
        if (!Enabled || !Index(length, MaxWeights)) return new float[length];
        if (_weights is not { } weights || weights.Length != length) return _weights = new float[length];
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
