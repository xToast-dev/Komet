using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;

namespace Komet.World;

// A map piece's pixels (int[1024], GenerateChunkImage) live until the map saves them (toSaveList, up to 4 s) and its tile
// uploads them: long enough to survive gen0 and gen1, so while chunks stream the map promoted ~3 MB/s of soon-dead arrays
// (136 MB in a 40 s join trace). Here a piece made by GenerateChunkImage comes from a pool and goes back once both holders
// are done with it: the save (MapSaveScratch) and the upload (MapTileUpload). A piece one of them never reports (the engine's
// own save or upload, a piece replaced before either, a tile dropped) stays the collector's: only reported arrays are reused.
internal static class MapPixels
{
    public const int Length = 32 * 32;
    private const string Map = "Vintagestory.GameContent.ChunkMapLayer";
    private const int Holders = 2, MaxPooled = 512, MaxInstructions = 4096;

    private static readonly Lock Gate = new();
    private static readonly Stack<int[]> Pooled = new(MaxPooled);
    private static readonly ConditionalWeakTable<int[], StrongBox<int>> Held = [];

    private static long _rented, _reused;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }
    public static long Rented => Interlocked.Read(ref _rented);
    public static long Reused => Interlocked.Read(ref _reused);

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        Clear();
        if (!NotNull(harmony) || Target() is not { } target) return;
        _ = harmony.Patch(target, transpiler: new HarmonyMethod(Rewrite));
    }

    // ChunkMapLayer.GenerateChunkImage, the one maker of map pieces
    internal static MethodInfo? Target()
    {
        var type = AccessTools.TypeByName(Map);
        var method = type is null
            ? null
            : AccessTools.DeclaredMethod(type, "GenerateChunkImage", [typeof(FastVec2i), typeof(IMapChunk), typeof(bool)]);
        return method is null || Assert(method.ReturnType == typeof(int[])) ? method : null;
    }

    // The one `newarr int32` becomes Rent; any other shape keeps the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        if (!Il.Take(instructions, MaxInstructions, out var code)) return code;
        Rewritten = Il.SubstituteSingle(code, c => c.opcode == OpCodes.Newarr && c.operand is Type t && t == typeof(int),
            AccessTools.Method(typeof(MapPixels), nameof(Rent)));
        return code;
    }

    // In place of `new int[length]` in GenerateChunkImage: a cleared pooled piece, or a new one
    internal static int[] Rent(int length)
    {
        if (!Enabled || length != Length) return new int[length];
        int[]? pixels = null;
        lock (Gate)
            if (Pooled.Count > 0)
                pixels = Pooled.Pop();
        if (pixels is null) pixels = new int[Length];
        else
        {
            Array.Clear(pixels);
            _ = Interlocked.Increment(ref _reused);
        }

        Held.AddOrUpdate(pixels, new StrongBox<int>(Holders));
        _ = Interlocked.Increment(ref _rented);
        return pixels;
    }

    // One holder is done (saved, or uploaded); the last one hands the piece back
    internal static void Release(int[]? pixels)
    {
        if (pixels is null || !Held.TryGetValue(pixels, out var holders)) return;
        lock (Gate)
        {
            if (--holders.Value > 0) return;
            _ = Held.Remove(pixels);
            if (Pooled.Count < MaxPooled) Pooled.Push(pixels);
        }
    }

    // Leaves no pieces behind (a world goes)
    internal static void Clear()
    {
        lock (Gate)
        {
            Pooled.Clear();
            Held.Clear();
        }
    }
}
