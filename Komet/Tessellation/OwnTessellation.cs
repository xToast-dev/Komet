using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using static Komet.Tessellation.TessSeams;

namespace Komet.Tessellation;

// Komet's own tesselators for the commonest draw types: every ChunkTesselator keeps one tesselator per draw type in a private array,
// and a postfix on its Start puts OwnCube in the slots of the engine's CubeTesselator (the layers 1-7 and Transparent) and
// BleedingCubeTesselator (Cube), OwnTopsoil in that of the TopsoilTesselator, OwnCross in that of the CrossTesselator (Cross), and
// OwnJson and OwnJsonSnow in those of the JsonTesselator (JSON) and the JsonAndSnowLayerTesselator - on the engine's instance at
// install (it is made before the mods start) and on every pool thread's (TessWorkers). The engine's TesselateBlock still sets up
// every block as before and calls the slot, so whatever it decides for a block stays the engine's; decors reach the slots the same
// way. Each replacement keeps the engine's tesselator it replaced and hands it every block it does not reproduce, and every block
// while the switch is off or another mod patches one of the bodies it skips: the meshes come out byte for byte the engine's. The JSON
// slots have bodies of their own to match and watch: they stay the engine's where those changed, and stand down on their own.
//
// Measured motivation: the cube and topsoil tesselators took 18-22 % of the tessellation time in the world-join traces, the JSON
// tesselator 47-49 %, nearly all of it per-face and per-vertex calls; JIT tiering kept them quick-jitted for about 12 s after a world
// join (JitWarm), so every method the replacements run per block, face or vertex - the emitters, the JSON blocks' checks and tables,
// FaceLight's light - is optimized from its first call.
internal static class OwnTessellation
{
    // EngineShape of Shaped() and JsonShaped() in Vintage Story 1.22.7
    internal const ulong Shape = 0x3E893F7C264EACD3UL, JsonShape = 0xC31CF35953A07E07UL;

    private const int MaxSlots = 64, OursCounter = 0, EngineCounter = 1, JsonCounter = 2, Counters = 3;

    private static readonly Tally Counts = new(Counters);
    private static MethodBase?[] _skipped = [], _jsonSkipped = [];

    public static bool Enabled { get; set; } = true;
    // Cleared by the feature's Stop, leaving the world or the mod: the replacements still in a tesselator hand every block back
    public static bool Installed { get; internal set; }

    // Another mod patches a body the replacements skip: the engine's tesselators draw every block
    public static bool StoodDown { get; private set; }

    public static long Blocks => Counts.Total(OursCounter);
    public static long EngineBlocks => Counts.Total(EngineCounter);

    // Of Blocks, the JSON blocks (with snow or not)
    public static long JsonBlocks => Counts.Total(JsonCounter);

    // The JSON slots' bodies are those of 1.22.7 and the engine's tables and fields they read are there
    public static bool JsonInstalled { get; private set; }

    // Another mod patches a body the JSON replacements skip: the engine's tesselators draw every JSON block
    public static bool JsonStoodDown { get; private set; }

    // Asked by every block the replacements see
    internal static bool Active => Enabled && Installed && !StoodDown;

    internal static bool JsonActive => Enabled && Installed && JsonInstalled && !JsonStoodDown;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockTesselators")]
    private static extern ref IBlockTesselator?[]? Slots(ChunkTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockHeight")]
    private static extern ref float CubeHeight(CubeTesselator tesselator);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "blockHeight")]
    private static extern ref float BleedingHeight(BleedingCubeTesselator tesselator);

    // game: the world's, whose tesselator was made before the mods started (null in the tests, which swap their own)
    public static void Install(Harmony harmony, ILogger? logger = null, ClientMain? game = null, ulong shape = Shape,
        ulong jsonShape = JsonShape)
    {
        (Installed, StoodDown, JsonInstalled, JsonStoodDown) = (false, false, false, false);
        var start = Method(typeof(ChunkTesselator), nameof(ChunkTesselator.Start));
        if (!NotNull(harmony) || !NotNull(start) || !Accessible(nameof(OwnTessellation), logger) || !Fields(logger) ||
            !EngineShape.Matches(Shaped(), shape, nameof(OwnTessellation), logger)) return;
        _skipped = Shaped();
        JsonInstalled = JsonMesher.Fields(logger) && OwnJsonSnow.Fields(logger) && JsonMesh.Tables(logger) &&
                        EngineShape.Matches(JsonShaped(), jsonShape, nameof(OwnTessellation) + " JSON", logger);
        _jsonSkipped = JsonInstalled ? JsonShaped() : [];
        _ = NotNull(harmony.Patch(start, postfix: new HarmonyMethod(Started)));
        Installed = true;
        Recheck();
        if (game?.TerrainChunkTesselator is { } engine) _ = Swap(engine);
    }

    // Any patch on a skipped body, also a postfix: the replacements never call them. JitWarm's identity transpilers, FaceLight's
    // prefixes, whose results the replacements take from FaceLight directly, and TessSafety's locks and rewrites, which guard what
    // the JSON replacement hands to the engine and give each thread its own corners in DrawCross (OwnCross keeps its own), are
    // Komet's own.
    internal static void Recheck()
    {
        if (!Installed || !Assert(_skipped.Length > 0)) return;
        StoodDown = EngineShape.Report(Logger, nameof(OwnTessellation), StoodDown,
            EngineShape.Foreign(_skipped, EngineShape.Kinds.All, null, typeof(JitWarm), typeof(FaceLight), typeof(TessSafety)));
        if (!JsonInstalled || !Assert(_jsonSkipped.Length > 0)) return;
        JsonStoodDown = EngineShape.Report(Logger, nameof(OwnTessellation) + " JSON", JsonStoodDown,
            EngineShape.Foreign(_jsonSkipped, EngineShape.Kinds.All, null, typeof(JitWarm), typeof(FaceLight),
                typeof(TessSafety)));
    }

    internal static void Count(bool ours, bool json = false)
    {
        var counter = ours ? OursCounter : EngineCounter;
        if (!Counting.Hud || !Index(counter, Counters)) return;
        Counts.Add(counter, 1);
        if (ours && json) Counts.Add(JsonCounter, 1);
    }

    // The bodies the replacements reproduce and skip: the four tesselators with their face and texture helpers, the face light, the
    // pool lookup, the MeshData appends they write in place, the chunk bounds, and the SmallBoolArray reads of SideAo
    internal static MethodBase?[] Shaped()
    {
        var (t, i, f, b, bits) = (typeof(TCTCache), typeof(int), typeof(float), typeof(bool), typeof(SmallBoolArray));
        var (cube, face, mesh) = (typeof(CubeTesselator), typeof(FastVec3f[]), typeof(MeshData));
        MethodBase?[] methods =
        [
            Method(cube, nameof(CubeTesselator.Tesselate), t),
            Method(cube, nameof(CubeTesselator.DrawBlockFace), t, i, face, typeof(TextureAtlasPosition), i, i,
                typeof(MeshData[]), f),
            Method(typeof(BleedingCubeTesselator), nameof(BleedingCubeTesselator.Tesselate), t),
            Method(typeof(BleedingTextureHelper), nameof(BleedingTextureHelper.GetFaceTextureSubId), t, i),
            Method(typeof(BleedingTextureHelper), nameof(BleedingTextureHelper.ComputeBleedForFace), t, i, i, i),
            Method(typeof(BleedingTextureHelper), nameof(BleedingTextureHelper.IsValidTextureSubId),
                typeof(TextureAtlasPosition[]), i),
            Method(typeof(TopsoilTesselator), nameof(TopsoilTesselator.Tesselate), t),
            Method(typeof(TopsoilTesselator), "DrawBlockFaceTopSoil", t, i, face, i, i, i, typeof(MeshData[]), i),
            Method(typeof(CrossTesselator), nameof(CrossTesselator.Tesselate), t),
            Method(typeof(CrossTesselator), nameof(CrossTesselator.DrawCross), t, f),
            FaceLight.Target(), Method(t, nameof(TCTCache.UpdateChunkMinMax), f, f, f),
            Method(typeof(ChunkTesselator), nameof(ChunkTesselator.GetPoolForPass), typeof(EnumChunkRenderPass), i),
            Method(mesh, nameof(MeshData.AddVertexWithFlags), f, f, f, f, f, i, i),
            Method(mesh, nameof(MeshData.AddVertexWithFlagsSkipColor), f, f, f, f, f, i),
            Method(mesh, nameof(MeshData.AddQuadIndices), i),
            Method(typeof(CustomMeshDataPart<int>), nameof(CustomMeshDataPart<>.Add4), i),
            Method(typeof(CustomMeshDataPart<short>), nameof(CustomMeshDataPart<>.Add), typeof(short)),
            Method(typeof(CustomMeshDataPartShort), nameof(CustomMeshDataPartShort.AddPackedUV), f, f, b, b),
            Method(typeof(TileSideEnum), nameof(TileSideEnum.ToFlags), i),
            AccessTools.DeclaredPropertyGetter(bits, "Item"), Method(bits, "op_Implicit", bits)
        ];
        _ = Assert(methods.Length <= EngineShape.MaxMethods) && Assert(bits.IsValueType); // a missing one fingerprints as 0
        return methods;
    }

    // The bodies the JSON replacements reproduce and skip: JsonTesselator's Tesselate, light, doMesh and mesh copy with its helpers, the
    // snow layer's tesselator and the cube face it draws, the pool lookups, the default mesh's getter (read from its array), Block's
    // OnJsonTesselation and the wind flag helper it calls (run once per mesh), the interpolation of an even face's light, the face
    // light, the chunk bounds, the appends written in place and the SmallBoolArray reads of SideAo
    internal static MethodBase?[] JsonShaped()
    {
        var (t, i, f, b, json, mesh) = (typeof(TCTCache), typeof(int), typeof(float), typeof(bool), typeof(JsonTesselator),
            typeof(MeshData));
        var (supplier, pass, matrix, bits) = (typeof(IMeshPoolSupplier), typeof(EnumChunkRenderPass), typeof(float[]),
            typeof(SmallBoolArray));
        MethodBase?[] methods =
        [
            Method(json, nameof(JsonTesselator.Tesselate), t), FaceLight.FusedTarget(),
            Method(json, nameof(JsonTesselator.doMesh), t, mesh, i),
            Method(json, nameof(JsonTesselator.AddJsonModelDataToMesh), mesh, i, t, supplier, matrix),
            Method(json, nameof(JsonTesselator.AddJsonModelDataToMesh), mesh, i, t, supplier, matrix,
                typeof(IJsonTesselatorHooks), i),
            Method(json, "AdjustWindWaveForFluids", mesh, typeof(Block)),
            Method(json, nameof(JsonTesselator.FindDecorMaxPos), t, mesh, matrix, matrix, i, f, f, b),
            Method(typeof(JsonAndSnowLayerTesselator), nameof(JsonAndSnowLayerTesselator.Tesselate), t),
            Method(typeof(CubeTesselator), nameof(CubeTesselator.DrawBlockFace), t, i, typeof(FastVec3f[]),
                typeof(TextureAtlasPosition), i, i, typeof(MeshData[]), f),
            Method(typeof(TerrainMesherHelper), nameof(TerrainMesherHelper.GetMeshPoolForPass), i, pass, i),
            Method(typeof(ChunkTesselator), nameof(ChunkTesselator.GetMeshPoolForPass), i, pass, i),
            Method(typeof(ChunkTesselator), nameof(ChunkTesselator.GetPoolForPass), pass, i),
            Method(typeof(ShapeTesselatorManager), nameof(ShapeTesselatorManager.GetDefaultBlockMesh), typeof(Block)),
            Method(typeof(Block), nameof(Block.OnJsonTesselation), mesh.MakeByRefType(), typeof(int[]).MakeByRefType(),
                typeof(BlockPos), typeof(Block[]), i),
            Method(typeof(MeshUtil), nameof(MeshUtil.SetWindFlag), mesh, f, i),
            Method(typeof(GameMath), nameof(GameMath.BiLerpRgbaColor), f, f, i, i, i, i),
            FaceLight.Target(), Method(t, nameof(TCTCache.UpdateChunkMinMax), f, f, f),
            Method(mesh, nameof(MeshData.AddVertexWithFlags), f, f, f, f, f, i, i),
            Method(mesh, nameof(MeshData.AddQuadIndices), i), Method(mesh, nameof(MeshData.AddIndices), b, i, i, i, i, i, i),
            Method(typeof(CustomMeshDataPart<int>), nameof(CustomMeshDataPart<>.Add4), i),
            Method(typeof(CustomMeshDataPart<int>), nameof(CustomMeshDataPart<>.Add), i),
            Method(typeof(TileSideEnum), nameof(TileSideEnum.ToFlags), i),
            AccessTools.DeclaredPropertyGetter(bits, "Item"), Method(bits, "op_Implicit", bits)
        ];
        _ = Assert(methods.Length <= EngineShape.MaxMethods) && Assert(bits.IsValueType); // a missing one fingerprints as 0
        return methods;
    }

    // The engine's cube, layer, topsoil, cross and JSON tesselators of one ChunkTesselator replaced, by their exact types (a mod's
    // subclass or replacement stays); one already replaced stays as it is. The number replaced.
    internal static int Swap(ChunkTesselator tesselator)
    {
        var slots = NotNull(tesselator) ? Slots(tesselator) : null;
        if (!NotNull(slots) || !Assert(slots.Length <= MaxSlots)) return 0;
        var swapped = 0;
        for (var i = 0; i < Math.Min(slots.Length, MaxSlots); i++)
        {
            if (Own(slots[i]) is not { } own) continue;
            slots[i] = own;
            swapped++;
        }

        return swapped;
    }

    // A height that is not finite stays the engine's, the JSON tesselators while their bodies are not the ones matched
    private static IBlockTesselator? Own(IBlockTesselator? engine) => engine switch
    {
        JsonTesselator when engine.GetType() == typeof(JsonTesselator) && JsonInstalled => new OwnJson(engine),
        JsonAndSnowLayerTesselator snow when engine.GetType() == typeof(JsonAndSnowLayerTesselator) && JsonInstalled =>
            new OwnJsonSnow(snow),
        BleedingCubeTesselator bleeding when engine.GetType() == typeof(BleedingCubeTesselator) &&
                                             Finite(BleedingHeight(bleeding)) =>
            new OwnCube(bleeding, BleedingHeight(bleeding), true),
        CubeTesselator cube when engine.GetType() == typeof(CubeTesselator) && Finite(CubeHeight(cube)) =>
            new OwnCube(cube, CubeHeight(cube), false),
        TopsoilTesselator topsoil when engine.GetType() == typeof(TopsoilTesselator) => new OwnTopsoil(topsoil),
        CrossTesselator when OwnCross.Replaces(engine) => new OwnCross(engine),
        _ => null
    };

    // ChunkTesselator.Start, which also runs for the pool threads' instances; harmless before the instance has started
    private static void Started(ChunkTesselator __instance)
    {
        if (Installed && NotNull(__instance)) _ = Swap(__instance);
    }

    // The private engine fields the accessors reach, with their types (a missing one would throw on a game thread)
    private static bool Fields(ILogger? logger) => TessSeams.Fields(nameof(OwnTessellation), logger, "the engine's tesselators run",
    [
        (typeof(ChunkTesselator), "blockTesselators", typeof(IBlockTesselator[])),
        (typeof(ChunkTesselator), "currentModeldataByRenderPassByLodLevel", typeof(MeshData[][][])),
        (typeof(CubeTesselator), "blockHeight", typeof(float)),
        (typeof(BleedingCubeTesselator), "blockHeight", typeof(float))
    ]);
}
