using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Test.Rendering;

// Use runs for every entity, held item and block entity, 118 times a frame, and must not allocate. The platform is a real
// ClientPlatformWindows without a window: Fill only reads its shader uniforms and frame buffer sizes, and the programs here take no
// texture, the one path of Stage that talks to GL.
[SuppressMessage("Reliability", "CA2000",
    Justification = "ShaderProgram.Dispose deletes the GL program, and a test has no GL context")]
public sealed class ShaderUseCacheTests
{
    private const int Calls = 10_000, Passes = 3;

    private static readonly string[] Includes =
        ["fogandlight.fsh", "fogandlight.vsh", "shadowcoords.vsh", "vertexwarp.vsh", "colormap.vsh"];

    private float _lodBias;

    private ClientPlatformAbstract? _platform;
    private int _shadowQuality, _viewDistance;

    [SetUp]
    public void FakePlatform()
    {
        (_platform, _shadowQuality) = (ScreenManager.Platform, ShaderProgramBase.shadowmapQuality);
        (_viewDistance, _lodBias) = (ClientSettings.ViewDistance, ClientSettings.LodBias);
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        var buffers = Enumerable.Range(0, 16)
            .Select(i => new FrameBufferRef { Width = 1920, Height = 1080, DepthTextureId = i }).ToList();
        AccessTools.Field(typeof(ClientPlatformWindows), "frameBuffers").SetValue(platform, buffers);
        platform.ShaderUniforms = new DefaultShaderUniforms { ColorMapRects4 = new float[160] };
        ScreenManager.Platform = platform;
        ShaderProgramBase.shadowmapQuality = 0; // with shadows on, Fill binds the shadow maps, which needs GL
        ShaderUseCache.Enabled = true;
    }

    [TearDown]
    public void Restore()
    {
        (ScreenManager.Platform, ShaderProgramBase.shadowmapQuality) = (_platform, _shadowQuality);
        (ClientSettings.ViewDistance, ClientSettings.LodBias) = (_viewDistance, _lodBias);
        ShaderUseCache.Enabled = true;
    }

    private static ShaderProgram Program(int pass, int ubos = 0)
    {
        var program = new ShaderProgram { PassId = pass, ProgramId = 7 };
        foreach (var file in Includes) _ = program.includes.Add(file);
        for (var i = 0; i < ubos; i++) _ = program.ubos.Add("ubo" + i, new CountingUbo());
        return program;
    }

    private static long Allocated(Action action, int calls = Calls)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++) action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The last of several passes: tiered compilation promotes the methods during the first ones, and the runtime allocates for that
    private static long Steady(Action action)
    {
        long bytes = 0;
        for (var pass = 0; pass < Passes; pass++) bytes = Allocated(action);
        return bytes;
    }

    [Test]
    public void StagingAFrameAllocatesNothingOnceBuilt()
    {
        var program = Program(100);
        ShaderUseCache.NextFrame();
        Assert.That(ShaderUseCache.Stage(program), Is.Not.Null, "the layout overflowed");
        Assert.That(Steady(() => ShaderUseCache.Changed(ShaderUseCache.Stage(program)!, false)), Is.Zero);
    }

    // Engine code writes some default uniforms outside Use with values Fill would not produce (SystemRenderSunMoon zeroes Standard's
    // shadowIntensity); those go up on every Use, as the engine's Use resets them. Uniforms other code writes only with Fill's own value
    // (lightPosition, windWaveCounter) stay cached, and a changed one (zNear) goes up again.
    [Test]
    public void UniformsWrittenOutsideUseAreSentOnEveryUse()
    {
        string[] external =
        [
            "shadowIntensity", "flatFogDensity", "flatFogStart", "waterWaveCounter", "globalWarpIntensity",
            "glitchWaviness", "windWaveIntensity"
        ];
        var program = Program(105);
        for (var i = 0; i < external.Length; i++) program.uniformLocations[external[i]] = 100 + i;
        (program.uniformLocations["zNear"], program.uniformLocations["lightPosition"],
                program.uniformLocations["windWaveCounter"]) =
            (99, 90, 91);
        ShaderUseCache.NextFrame();
        var s = ShaderUseCache.Stage(program)!;
        var first = ShaderUseCache.Changed(s, true);
        ScreenManager.Platform.ShaderUniforms.ZNear = 0.05f;
        s = ShaderUseCache.Stage(program)!;
        var again = ShaderUseCache.Changed(s, false);
        Assert.Multiple(() =>
        {
            Assert.That(first, Is.GreaterThan(again), "the first Use of a frame sends everything");
            Assert.That(s.Sends.Take(again).Select(i => s.Slots[i].Loc),
                Is.EquivalentTo(Enumerable.Range(99, external.Length + 1)));
        });
    }

    // Each buffer once, in the dictionary's order, as the engine's foreach binds them
    [Test]
    public void UniformBuffersAreBoundInOrderWithoutAllocating()
    {
        var program = Program(101, 3);
        var bound = new List<int>();
        CountingUbo.Order = bound;
        ShaderUseCache.BindUbos(program);
        CountingUbo.Order = null; // the recording itself must not show up in the measurement
        var binds = CountingUbo.Binds;
        var bytes = Steady(() => ShaderUseCache.BindUbos(program));
        Assert.Multiple(() =>
        {
            Assert.That(bound, Is.EqualTo(program.ubos.Values.Cast<CountingUbo>().Select(u => u.Id)));
            Assert.That(CountingUbo.Binds - binds, Is.EqualTo(3L * Calls * Passes));
            Assert.That(bytes, Is.Zero);
        });
    }

    // A change made during a frame reaches the uniforms with the next frame, the way the uploads already trail by one
    [Test]
    public void SettingsAreReadOncePerFrame()
    {
        var program = Program(102);
        (ClientSettings.ViewDistance, ClientSettings.LodBias) = (256, 0.5f);
        ShaderUseCache.NextFrame();
        Assert.That(ShaderUseCache.FrameSettings, Is.EqualTo((256f, 128f)));
        ClientSettings.ViewDistance = 1024;
        _ = ShaderUseCache.Stage(program);
        Assert.That(ShaderUseCache.FrameSettings, Is.EqualTo((256f, 128f)), "read again within the frame");
        ShaderUseCache.NextFrame();
        Assert.That(ShaderUseCache.FrameSettings, Is.EqualTo((1024f, 320f)),
            "the view distance is capped at 640 for LOD 0");
    }

    // A world's first Use() can come before its first uniform update; the frame's settings are read right then
    [Test]
    public void AFrameWithoutUpdateReadsTheSettingsOnFirstUse()
    {
        var program = Program(103);
        ClientSettings.ViewDistance = 384;
        ShaderUseCache.Enabled = false; // the update skips the read while the cache is off
        ShaderUseCache.NextFrame();
        ShaderUseCache.Enabled = true;
        Assert.That(ShaderUseCache.Stage(program), Is.Not.Null);
        Assert.That(ShaderUseCache.FrameSettings.ViewDistance, Is.EqualTo(384f));
    }

    private sealed class CountingUbo : UBORef
    {
        private static int _next;
        public static List<int>? Order { get; set; }
        public static long Binds { get; private set; }
        public int Id { get; } = Interlocked.Increment(ref _next);

        public override void Bind()
        {
            Binds++;
            Order?.Add(Id);
        }

        public override void Unbind()
        {
        }

        public override void Update<T>(T data)
        {
        }

        public override void Update<T>(T data, int offset, int size)
        {
        }

        public override void Update(object data, int offset, int size)
        {
        }
    }
}
