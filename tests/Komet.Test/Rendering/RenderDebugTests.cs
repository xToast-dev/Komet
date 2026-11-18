using System.Collections;
using Komet.Core;
using Komet.Hud;
using Komet.Api;
using Komet.Options;
using Komet.Rendering;
using Komet.Test.Gpu;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Rendering;

[NonParallelizable]
public sealed class RenderDebugTests
{
    private static readonly string[] DiagnosticUniforms = ["strength"], DiagnosticIncludes = ["fogandlight.fsh"];
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
    public void DisablingDeveloperModeClearsOverridesBeforeItIsEnabledAgain()
    {
        RenderDebug.SetView(RenderDebug.View.Points);
        RenderDebug.SetTerrain(0, false);
        RenderDebug.SetShader("mod", "pass", false);
        ClientSettings.DeveloperMode = false;
        ClientSettings.DeveloperMode = true;
        Assert.That(RenderDebug.Active, Is.False);
        Assert.That(RenderDebug.Mode, Is.EqualTo(RenderDebug.View.Filled));
        Assert.That(RenderDebug.TerrainAllowed(0), Is.True);
        Assert.That(RenderDebug.ShaderAllowed("mod", "pass"), Is.True);
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

    [Test]
    public void UnsupportedNonSolidFillKeepsTheNormalView()
    {
        _ = Services.Swap<IVulkanSide>(new NoVulkan { NonSolidFill = false });
        RenderDebug.SetView(RenderDebug.View.Wireframe);
        Assert.That(RenderDebug.Mode, Is.EqualTo(RenderDebug.View.Filled));
        RenderDebug.SetView(RenderDebug.View.Points);
        Assert.That(RenderDebug.Mode, Is.EqualTo(RenderDebug.View.Filled));
    }

    [Test]
    public void DisabledShaderPassDoesNotInvokeItsCallback()
    {
        var calls = 0;
        using var registration = ModPasses.Add("render-debug-unit", new KapiPass("grade", "void main() {}")
        {
            Enabled = () =>
            {
                calls++;
                return true;
            }
        });
        using var renderer = new ModPasses();
        var snapshot = AccessTools.Method(typeof(ModPasses), "Snapshot");
        RenderDebug.SetShader("render-debug-unit", "grade", false);
        var off = ((int Count, bool Any))snapshot.Invoke(renderer, null)!;
        Assert.That(off.Any, Is.False);
        Assert.That(calls, Is.Zero);
        RenderDebug.SetShader("render-debug-unit", "grade", true);
        var on = ((int Count, bool Any))snapshot.Invoke(renderer, null)!;
        Assert.That(on.Any, Is.True);
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void PassDiagnosticsExposeMetadataWithoutExecutingModCallbacks()
    {
        var calls = 0;
        const string mod = "render-debug-metadata";
        var pass = new KapiPass("grade", "void main() { color = vec4(1); }")
        {
            Order = 7, Enabled = () =>
            {
                calls++;
                return true;
            },
            Uniforms =
            {
                ["strength"] = () =>
                {
                    calls++;
                    return [1];
                }
            }
        };
        using var registration = ModPasses.Add(mod, pass);
        var before = ModPasses.Diagnostics().Single(p => p.Mod == mod);
        Assert.That(calls, Is.Zero);
        Assert.That(before.LastEnabled, Is.Null);
        Assert.That(before.Order, Is.EqualTo(7));
        Assert.That(before.Uniforms, Is.EqualTo(DiagnosticUniforms));
        Assert.That(before.Fragment, Is.EqualTo(pass.Fragment));
        using var renderer = new ModPasses();
        _ = AccessTools.Method(typeof(ModPasses), "Snapshot").Invoke(renderer, null);
        var after = ModPasses.Diagnostics().Single(p => p.Mod == mod);
        Assert.That(after.LastEnabled, Is.True);
        Assert.That(calls, Is.EqualTo(1));
        pass.Uniforms.Clear();
        Assert.That(before.Uniforms, Has.Length.EqualTo(1));
    }

    [Test]
    public void ShaderRegistrationInspectionDoesNotEvaluateCallbacks()
    {
        var calls = 0;
        const string mod = "debug-metadata";
        using var define = ModShaders.Define(mod, "QUALITY", () =>
        {
            calls++;
            return "1";
        }, ["pass:grade"]);
        using var uniform = ModShaders.Uniform(mod, "strength", () =>
        {
            calls++;
            return [1];
        });
        using var edit = ModShaders.Edit(mod, "terrain", source =>
        {
            calls++;
            return source;
        }, null);
        var rows = ModShaders.Registrations(mod);
        Assert.That(calls, Is.Zero);
        Assert.That(rows, Does.Contain("define QUALITY → pass:grade"));
        Assert.That(rows, Does.Contain("uniform strength"));
        Assert.That(rows, Does.Contain("edit terrain → vertex "));
    }

    [Test]
    public void TrackedProgramDiagnosticsRetainIncludesAndCompileStatus()
    {
        var programs = (IDictionary)AccessTools.Field(typeof(ShaderMods), "Programs").GetValue(null)!;
        var type = AccessTools.Inner(typeof(ShaderMods), "Program");
        const string key = "render-debug-unit:grade";
        programs.Add(key,
            Activator.CreateInstance(type, 12, "render-debug-unit", "grade", DiagnosticIncludes, true, 1)!);
        try
        {
            var api = Answers.Of<ICoreClientAPI>([]);
            var item = ShaderMods.Diagnostics(api).Single(p => p.Domain == "render-debug-unit");
            Assert.That(item.Id, Is.EqualTo(12));
            Assert.That(item.Compiled, Is.True);
            Assert.That(item.Includes, Is.EqualTo(DiagnosticIncludes));
            Assert.That(item.Owners, Is.Empty);
        }
        finally
        {
            programs.Remove(key);
        }
    }

    [Test]
    public void ComparisonRestoresBothStatesAndKeepsTheReferenceIndependent()
    {
        RenderDebug.SetNoCull(true);
        RenderDebug.SaveReference();
        RenderDebug.SetNoCull(false);
        RenderDebug.SetNoDepth(true);
        RenderDebug.SetView(RenderDebug.View.Wireframe);
        RenderDebug.SetTerrain((int)EnumChunkRenderPass.Liquid, false);
        RenderDebug.SetShader("compare", "grade", false);
        Assert.That(RenderDebug.OverrideCount, Is.EqualTo(4));
        RenderDebug.Compare();
        Assert.That(RenderDebug.ShowingReference, Is.True);
        Assert.That(RenderDebug.NoCull, Is.True);
        Assert.That(RenderDebug.NoDepth, Is.False);
        Assert.That(RenderDebug.TerrainAllowed((int)EnumChunkRenderPass.Liquid), Is.True);
        Assert.That(RenderDebug.ShaderAllowed("compare", "grade"), Is.True);
        Assert.That(RenderDebug.OverrideCount, Is.EqualTo(1));
        RenderDebug.SetNoCull(true);
        RenderDebug.SetNoDepth(false);
        RenderDebug.SetView(RenderDebug.View.Filled);
        Assert.That(RenderDebug.ShowingReference, Is.True);
        RenderDebug.Compare();
        Assert.That(RenderDebug.ShowingReference, Is.False);
        Assert.That(RenderDebug.NoCull, Is.False);
        Assert.That(RenderDebug.NoDepth, Is.True);
        Assert.That(RenderDebug.Mode, Is.EqualTo(RenderDebug.View.Wireframe));
        Assert.That(RenderDebug.TerrainAllowed((int)EnumChunkRenderPass.Liquid), Is.False);
        Assert.That(RenderDebug.ShaderAllowed("compare", "grade"), Is.False);
    }

    [Test]
    public void EditingAReferenceCreatesANewWorkingStateAndResetClearsComparison()
    {
        RenderDebug.SaveReference();
        RenderDebug.SetNoDepth(true);
        RenderDebug.Compare();
        RenderDebug.SetNoCull(true);
        Assert.That(RenderDebug.ShowingReference, Is.False);
        RenderDebug.Compare();
        Assert.That(RenderDebug.NoCull, Is.False);
        RenderDebug.Compare();
        Assert.That(RenderDebug.NoCull, Is.True);
        Assert.That(RenderDebug.NoDepth, Is.False);
        RenderDebug.Reset();
        Assert.That(RenderDebug.HasReference, Is.False);
        Assert.That(RenderDebug.OverrideCount, Is.Zero);
        RenderDebug.Compare();
        Assert.That(RenderDebug.ShowingReference, Is.False);
    }

    [Test]
    public void ComparisonIsClearedWhenDeveloperModeEndsAndRespectsDeviceSupport()
    {
        RenderDebug.SetView(RenderDebug.View.Wireframe);
        RenderDebug.SaveReference();
        RenderDebug.SetView(RenderDebug.View.Filled);
        _ = Services.Swap<IVulkanSide>(new NoVulkan { NonSolidFill = false });
        RenderDebug.Compare();
        Assert.That(RenderDebug.Mode, Is.EqualTo(RenderDebug.View.Filled));
        Assert.That(RenderDebug.OverrideCount, Is.Zero);
        ClientSettings.DeveloperMode = false;
        ClientSettings.DeveloperMode = true;
        Assert.That(RenderDebug.HasReference, Is.False);
        RenderDebug.Clear();
        Assert.That(RenderDebug.ShowingReference, Is.False);
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

    [Test]
    public void TheDebugPageIsOnlyListedInDeveloperMode()
    {
        var actions = new KometActions(() => { }, _ => { }, () => { }, () => { }, () => { });
        var pages = new KometPages(new HudSettings(), actions);
        string[] developer = [CompatibilityPage.Id, ConnectedModsPage.Id, RenderDebugPage.Id];
        Assert.That(pages.DeveloperPages().Select(p => p.Id), Is.EqualTo(developer));
        Assert.That(pages.DeveloperPages(), Has.All.Matches<OptionPage>(p => p.Section == KometPages.DeveloperSection));
        Assert.That(pages.Build().Select(p => p.Id), Does.Not.Contain(RenderDebugPage.Id), "not among Komet's own");
        ClientSettings.DeveloperMode = false;
        Assert.That(pages.DeveloperPages(), Is.Empty);
    }
}
