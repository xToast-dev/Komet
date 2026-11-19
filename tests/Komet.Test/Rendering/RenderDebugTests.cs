using Komet.Core;
using Komet.Rendering;
using Komet.Test.Gpu;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Rendering;

[NonParallelizable]
public sealed class RenderDebugTests
{
    private bool _developer;
    private ClientMain? _game;
    private TestHarmony _harmony = null!;
    private int _main;
    private IVulkanSide? _vulkan;

    [OneTimeSetUp]
    public void Language() => GameLang.EnsureLoaded();

    [SetUp]
    public void Start()
    {
        (_developer, _main) = (ClientSettings.DeveloperMode, RuntimeEnv.MainThreadId);
        _vulkan = Services.Swap<IVulkanSide>(new NoVulkan { NonSolidFill = true });
        ClientSettings.DeveloperMode = true;
        RuntimeEnv.MainThreadId = Environment.CurrentManagedThreadId;
        _game = null;
        _harmony = new TestHarmony("komet-render-debug-tests");
        RenderDebug.Install(_harmony,
            Answers.Of<ICoreClientAPI>(new Dictionary<string, System.Func<object?[]?, object?>>
                { ["get_World"] = _ => _game }));
    }

    [TearDown]
    public void Stop()
    {
        RenderDebug.Clear();
        _harmony.Dispose();
        ClientSettings.DeveloperMode = _developer;
        RuntimeEnv.MainThreadId = _main;
        _ = Services.Swap(_vulkan);
    }

    [Test]
    public void PassOverridesAreIndependentAndResetTogether()
    {
        RenderDebug.SetTerrain((int)EnumChunkRenderPass.Liquid, false);
        RenderDebug.SetShader("a", "grade", false);
        RenderDebug.SetNoCull(true);
        RenderDebug.SetNoDepth(true);
        Assert.That(RenderDebug.TerrainAllowed((int)EnumChunkRenderPass.Opaque), Is.True);
        Assert.That(RenderDebug.ShaderAllowed("b", "grade"), Is.True);
        Assert.That(RenderDebug.ShaderAllowed("a", "grade"), Is.False);
        RenderDebug.Reset();
        Assert.That(RenderDebug.Active, Is.False);
        Assert.That(RenderDebug.NoCull || RenderDebug.NoDepth, Is.False);
        Assert.That(RenderDebug.TerrainAllowed((int)EnumChunkRenderPass.Liquid), Is.True);
    }

    private MeshDataPoolManager Terrain(EnumChunkRenderPass pass)
    {
        _game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        var terrain = (ChunkRenderer)RuntimeHelpers.GetUninitializedObject(typeof(ChunkRenderer));
        var manager = (MeshDataPoolManager)RuntimeHelpers.GetUninitializedObject(typeof(MeshDataPoolManager));
        Fields.TerrainOf(_game) = terrain;
        Fields.Stage(_game) = EnumRenderStage.Opaque;
        var passes = new MeshDataPoolManager[16][];
        passes[(int)pass] = [manager];
        Fields.PassPools(terrain) = passes;
        return manager;
    }

    [Test]
    public void DisabledTerrainPassSkipsTheOriginalDrawBeforeItTouchesGraphics()
    {
        Assert.That(RenderDebug.Installed, Is.True);
        var manager = Terrain(EnumChunkRenderPass.Liquid);
        RenderDebug.SetTerrain((int)EnumChunkRenderPass.Liquid, false);
        Assert.DoesNotThrow(() => manager.Render(new Vec3d(), "origin"));
    }

    private static void ThrowDuringDraw()
    {
        Assert.That(RenderDebug.Save().Front, Is.EqualTo((int)PolygonMode.Line));
        throw new InvalidOperationException("test draw failure");
    }

    [Test]
    [Explicit("Requires a working OpenGL 4.6 display; run by name on a graphics-capable host.")]
    public void GraphicsStateIsRestoredEvenWhenTheDrawThrows()
    {
        using var window = GlContext.Context();
        if (window is null) Assert.Ignore("no OpenGL context: " + GlContext.Why);

        var initial = RenderDebug.Save();
        try
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
            GL.Enable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            var manager = Terrain(EnumChunkRenderPass.Opaque);
            RenderDebug.SetView(RenderDebug.View.Wireframe);
            RenderDebug.SetNoCull(true);
            RenderDebug.SetNoDepth(true);
            _ = _harmony.Patch(AccessTools.Method(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render)),
                new HarmonyMethod(AccessTools.Method(typeof(RenderDebugTests), nameof(ThrowDuringDraw)))
                    { priority = Priority.Last });
            _ = Assert.Throws<InvalidOperationException>(() => manager.Render(new Vec3d(), "origin"));
            var restored = RenderDebug.Save();
            Assert.That(restored.Front, Is.EqualTo((int)PolygonMode.Fill));
            Assert.That(restored is { Cull: true, Depth: true }, Is.True);
        }
        finally
        {
            RenderDebug.Restore(initial);
        }
    }

}
