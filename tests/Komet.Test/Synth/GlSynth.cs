using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FluffyClouds;
using Komet.Test.Gpu;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace Komet.Test.Synth;

// The features that save driver work, against the engine's own GL calls in a real OpenGL context: the engine's window class
// (GameWindowNative, an X11 window like the game's) hidden, 64 x 64. Run with mesa_glthread=true in the environment to measure
// them as under Mesa's glthread, which the process can only take from its environment.
[Category(Synth.Category)]
[NonParallelizable]
public sealed class GlSynth
{
    private const int Size = 64;
    private static int _skia;

    // The uniforms and samplers ShaderProgramBase.Use sets, by name
    private static readonly string[] UseNames =
    [
        "atlasHeight", "cameraUnderwater", "colorMapRects", "ditherSeed", "flatFogDensity", "flatFogStart", "fogSphereQuantity",
        "fogSpheres", "fogWaveCounter", "frameSize", "glitchStrength", "glitchStrengthFL", "glitchWaviness", "globalWarpIntensity",
        "horizontalResolution", "lightPosition", "nightVisionStrength", "perceptionEffectId", "perceptionEffectIntensity",
        "playerpos", "playerToSealevelOffset", "pointLightColors", "pointLightQuantity", "pointLights", "psychedelicStrength",
        "seaLevel", "seasonRel", "seasonTemperature", "shadowIntensity", "shadowMapHeightInv", "shadowMapWidthInv",
        "shadowRangeFar", "shadowRangeNear", "sunsetMod", "timeCounter", "toShadowMapSpaceMatrixFar",
        "toShadowMapSpaceMatrixNear", "viewDistance", "viewDistanceLod0", "waterMurkColor", "waterWaveCounter",
        "waterWaveIntensity", "windSpeed", "windWaveCounter", "windWaveCounterHighFreq", "windWaveIntensity", "zFar", "zNear",
        "liquidDepth", "sky", "glow", "shadowMapFar", "shadowMapNear"
    ];

    private static readonly string[] UseSamplers = ["liquidDepth", "sky", "glow", "shadowMapFar", "shadowMapNear"];

    private ClientPlatformAbstract? _platform;
    private FrameProfilerUtil? _profiler;
    private int _shadowQuality;
    private GameWindowNative? _window;

    [SetUp]
    public void Open()
    {
        (_platform, _profiler, _shadowQuality) = (ScreenManager.Platform, ScreenManager.FrameProfiler, ShaderProgramBase.shadowmapQuality);
        using (var probe = GlContext.Context()) // loads the game's GLFW
        {
            if (probe is null) Assert.Ignore("no OpenGL 4.6 context here: " + GlContext.Why);
        }

        // the window sets the game's icon through SkiaSharp, whose native library the game ships in Lib
        if (Interlocked.Exchange(ref _skia, 1) == 0)
            System.Runtime.Loader.AssemblyLoadContext.Default.ResolvingUnmanagedDll += static (_, name) =>
                name.Contains("SkiaSharp", StringComparison.Ordinal) ? NativeLibrary.Load(Path.Combine(GameInstall.Lib, "libSkiaSharp.so")) : 0;
        _window = new GameWindowNative(GameWindowSettings.Default, new NativeWindowSettings
        {
            StartVisible = false, APIVersion = new Version(4, 6), Profile = ContextProfile.Compatability,
            ClientSize = new OpenTK.Mathematics.Vector2i(Size, Size)
        });
        _window.MakeCurrent();
        _window.VSync = VSyncMode.Off;
        ScreenManager.FrameProfiler = new FrameProfilerUtil(static _ => { });
        global::Komet.Diagnostics.FrameClock.Begin(); // a frame counted: per-frame settings reads cache as in the game
        GL.Clear(ClearBufferMask.ColorBufferBit); // glthread starts its thread with the first commands
        var threads = Directory.GetDirectories("/proc/self/task").Select(t => File.ReadAllText(Path.Combine(t, "comm")).Trim());
        TestContext.Out.WriteLine($"GL {GL.GetString(StringName.Renderer)}, mesa_glthread={Environment.GetEnvironmentVariable("mesa_glthread")}, " +
                                  $"threads: {string.Join(' ', threads.Where(t => t.Contains("gl", StringComparison.OrdinalIgnoreCase)))}");
    }

    [TearDown]
    public void Close()
    {
        (ScreenManager.Platform, ScreenManager.FrameProfiler, ShaderProgramBase.shadowmapQuality) = (_platform!, _profiler!, _shadowQuality);
        ShaderProgramBase.CurrentShaderProgram = null;
        _window?.Dispose();
    }

    // A platform as the game has it, with the test's window
    private ClientPlatformWindows Platform()
    {
        var platform = (ClientPlatformWindows)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlatformWindows));
        platform.window = _window!;
        AccessTools.Field(typeof(ClientPlatformWindows), "windowsize").SetValue(platform, new Size2i(Size, Size));
        AccessTools.Field(typeof(ClientPlatformWindows), "ssaaLevel").SetValue(platform, 1f);
        AccessTools.Field(typeof(ClientPlatformWindows), "frameBuffers").SetValue(platform,
            Enumerable.Range(0, 16).Select(i => new FrameBufferRef { Width = 1920, Height = 1080, DepthTextureId = i }).ToList());
        platform.ShaderUniforms = new DefaultShaderUniforms { ColorMapRects4 = new float[160] };
        ScreenManager.Platform = platform;
        return platform;
    }

    // Switching frame buffers reads the window's size: the default and back, a switch per op
    [Test]
    public void WindowSize()
    {
        var platform = Platform();
        using (new FeatureRig("WindowSize")) Assert.That(global::Komet.Rendering.WindowSize.Rewritten, Is.True);
        _ = Synth.Run("WindowSize", "frameBuffers", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                platform.LoadFrameBuffer(EnumFrameBuffer.Default);
                platform.UnloadFrameBuffer(EnumFrameBuffer.Primary);
            }
        }, 500, () =>
        {
            var viewport = new int[4];
            GL.GetInteger(GetPName.Viewport, viewport);
            return viewport;
        });
    }

    // The game's standard program (fog and light, shadow coordinates, vertex warp, underwater effects) used and stopped as an entity
    // renderer does, within one frame, a use per op
    [Test]
    public void ShaderUseCache()
    {
        GameInstall.RequireAssets();
        _ = Platform();
        ShaderProgramBase.shadowmapQuality = 0;
        var (vertex, fragment) = EngineShaders.Program("standard", false);
        var id = Parity.Linked(vertex, fragment);
        var program = new ShaderProgram { PassId = 100, ProgramId = id, PassName = "synth-standard" };
        foreach (var file in (string[])["standard.vsh", "standard.fsh"])
        {
            foreach (Match include in Regex.Matches(File.ReadAllText(Path.Combine(EngineShaders.Folder, file)), @"^#include\s+(\S+)",
                         RegexOptions.Multiline))
                _ = program.includes.Add(include.Groups[1].Value.Trim().ToLowerInvariant());
        }

        // as the engine registers them: every uniform the sources declare and Use names, samplers numbered in order
        var names = Regex.Matches(vertex + fragment, @"^\s*uniform\s+\w+\s+(\w+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Concat(UseNames).Distinct();
        foreach (var name in names) program.uniformLocations[name] = GL.GetUniformLocation(id, name);
        var samplers = Regex.Matches(vertex + fragment, @"^\s*uniform\s+sampler\w*\s+(\w+)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Concat(UseSamplers).Distinct();
        foreach (var sampler in samplers) program.textureLocations[sampler] = program.textureLocations.Count;
        Assert.That(program.uniformLocations, Has.Count.GreaterThan(30));
        using (new FeatureRig("ShaderUseCache")) Assert.That(global::Komet.Rendering.ShaderUseCache.Matched, Is.True);
        try
        {
            _ = Synth.Run("ShaderUseCache", "standardUse", ops =>
            {
                for (var i = 0; i < ops; i++)
                {
                    program.Use();
                    program.Stop();
                }
            }, 2000, () => GL.GetError());
        }
        finally
        {
            GL.DeleteProgram(id);
        }
    }

    // The cloud map's thread has a new state every 40 ms; at view distance 1536 two 200 x 200 RGBA16 textures, their tiles unchanged
    // (the second, the undulating mode, nearly never changes), an upload of both per op
    [Test]
    public void CloudUploads()
    {
        const int length = 200;
        var map = (CloudRendererMap)RuntimeHelpers.GetUninitializedObject(typeof(CloudRendererMap));
        map.CloudTileLength = length;
        var make = AccessTools.Method(typeof(CloudRendererMap), "makeTexture");
        object[] format = [length, PixelInternalFormat.Rgba16, PixelFormat.Rgba, PixelType.Short];
        int[] textures = [(int)make.Invoke(map, format)!, (int)make.Invoke(map, format)!];
        AccessTools.Field(typeof(CloudRendererMap), "TextureData1").SetValue(map, textures[0]);
        AccessTools.Field(typeof(CloudRendererMap), "TextureData2").SetValue(map, textures[1]);
        AccessTools.Field(typeof(CloudRendererMap), "TextureDataBuffer1").SetValue(map, new short[length * length * 4]);
        AccessTools.Field(typeof(CloudRendererMap), "TextureDataBuffer2").SetValue(map, new short[length * length * 4]);
        var random = new Random(9);
        map.Tiles = [.. Enumerable.Range(0, length * length).Select(i => new CloudTile
        {
            GridXOffset = (short)(i / length - length / 2), GridZOffset = (short)(i % length - length / 2),
            ThinCloudMode = (short)random.Next(32767), SelfThickness = (short)random.Next(32767),
            CloudOpaqueness = (short)random.Next(32767), Brightness = (short)random.Next(32767), UndulatingCloudMode = 1000
        })];
        var write = AccessTools.Method(typeof(CloudRendererMap), "WriteTexture").CreateDelegate<Action>(map);
        using (new FeatureRig("CloudUploads")) Assert.That(global::Komet.World.CloudUploads.Rewritten, Is.True);
        try
        {
            _ = Synth.Run("CloudUploads", "unchanged200", ops =>
            {
                for (var i = 0; i < ops; i++) write();
            }, 50, () => GL.GetError());
        }
        finally
        {
            GL.DeleteTextures(2, textures);
        }
    }
}
