namespace Komet.Test.Synth;

// The engine's methods as the scenarios call them. The off arm runs first and tiers its callers up, and a tier-1 caller may
// carry the engine body inlined, which a patch installed afterwards never reaches (in the game the patches come first). These
// wrappers are never optimised, so each call goes through the method's entry: the engine's or, once patched, the patch's.
internal static class SynthCalls
{
    private const MethodImplOptions Plain = MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization;

    [MethodImpl(Plain)]
    public static IWorldChunk? GetChunk(ClientWorldMap map, int x, int y, int z) => map.GetChunk(x, y, z);

    [MethodImpl(Plain)]
    public static IWorldChunk? GetChunk(ClientWorldMap map, long index) => map.GetChunk(index);

    [MethodImpl(Plain)]
    public static WorldChunk? GetChunk(ClientWorldMap map, BlockPos pos) => map.GetChunk(pos);

    [MethodImpl(Plain)]
    public static int ReadBlock(WorldChunk chunk, int index) => chunk.UnpackAndReadBlock(index, 0);

    [MethodImpl(Plain)]
    public static void Begin(FrameProfilerUtil profiler) => profiler.Begin();

    [MethodImpl(Plain)]
    public static void Mark(FrameProfilerUtil profiler, string code) => profiler.Mark(code);

    [MethodImpl(Plain)]
    public static void Mark(FrameProfilerUtil profiler, string code, object param) => profiler.Mark(code, param);

    [MethodImpl(Plain)]
    public static void Enter(FrameProfilerUtil profiler, string code) => profiler.Enter(code);

    [MethodImpl(Plain)]
    public static void Leave(FrameProfilerUtil profiler) => profiler.Leave();

    [MethodImpl(Plain)]
    public static void End(FrameProfilerUtil profiler) => profiler.End();

    [MethodImpl(Plain)]
    public static void Compile(Animation animation, Shape shape) => animation.GenerateAllFrames(shape.Elements, shape.JointsById);

    [MethodImpl(Plain)]
    public static void Init(Shape shape, ILogger logger, string name, string[] joints) =>
        shape.InitForAnimations(logger, name, null, joints);

    [MethodImpl(Plain)]
    public static bool Tesselate(BlockEntityBehavior behavior, ITerrainMeshPool pool, ITesselatorAPI tesselator) =>
        behavior.OnTesselation(pool, tesselator);

    [MethodImpl(Plain)]
    public static void Trigger(ClientEventManager events, EnumRenderStage stage) => events.TriggerRenderStage(stage, 0.016f);

    [MethodImpl(Plain)]
    public static ModelDataPoolLocation? Add(MeshDataPool pool, ICoreClientAPI api, MeshData mesh) =>
        pool.TryAdd(api, mesh, null, 0, default);

    [MethodImpl(Plain)]
    public static void Remove(MeshDataPool pool, ModelDataPoolLocation location) => pool.RemoveLocation(location);

    [MethodImpl(Plain)]
    public static MeshData Recycled(MeshData mesh) => mesh.CloneUsingRecycler();

    [MethodImpl(Plain)]
    public static void Dispose(MeshData mesh) => mesh.Dispose();

    [MethodImpl(Plain)]
    public static void Translate(ClientMain game, double x, double y, double z) => game.GlTranslate(x, y, z);
}

// Engine callers of a patched method compiled again: an empty postfix makes Harmony rebuild them, and the rebuild calls the
// patched method through its entry instead of a copy inlined before the patch. Again() runs in a scenario's reset, after the
// on arm installed its feature.
internal sealed class Regen(params MethodBase[] callers) : IDisposable
{
    private readonly Harmony _harmony = new("komet-synth-regen");

    public void Again()
    {
        _harmony.UnpatchAll(_harmony.Id);
        foreach (var caller in callers) _ = _harmony.Patch(caller, postfix: Foreign.Postfix);
    }

    public void Dispose() => _harmony.UnpatchAll(_harmony.Id);
}
