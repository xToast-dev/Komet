using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Komet's edits to engine shader programs at load, in this order, from one LoadShaderProgram postfix that LeanVaryings
// installs for all four (every feature installs; their switches decide). PointLights and DistantShadows edit the
// shared includes in their own prefixes, through Includes. A switch changed in-world asks for one reload, batched with
// the settings page's other changes.
internal static class ShaderEdits
{
    private static ICoreClientAPI? _api;
    private static bool? _hasIncludes;

    // The LoadShaderProgram postfix is in place, which the four features' probes report
    public static bool Patched { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.StaticField, Name = "includes")]
    private static extern ref Dictionary<string, string>? IncludesField(ShaderRegistry? registry);

    // The engine's include texts by file name, which every program load reads; null before the first load. The field is
    // looked up once: an engine without it (renamed or retyped) gives null, so the include edits stand down instead of
    // throwing out of every shader program load.
    internal static Dictionary<string, string>? Includes()
    {
        if (_hasIncludes is null)
        {
            try
            {
                _ = IncludesField(null);
                _hasIncludes = true;
            }
            catch (MissingFieldException e)
            {
                _hasIncludes = false;
                _api?.Logger.Warning("Komet ShaderEdits: ShaderRegistry.includes not found ({0}); point lights and distant " +
                                     "shadows stand down", e.Message);
            }
        }

        return _hasIncludes == true ? IncludesField(null) : null;
    }

    public static void Install(Harmony harmony, ICoreClientAPI? api)
    {
        (_api, Patched) = (api, false);
        var (load, edit) = (AccessTools.DeclaredMethod(typeof(ShaderRegistry), "LoadShaderProgram",
            [typeof(ShaderProgram), typeof(bool)]), AccessTools.DeclaredMethod(typeof(ShaderEdits), nameof(Edit)));
        if (!NotNull(harmony) || !NotNull(load) || !NotNull(edit)) return;
        Patched = harmony.Patch(load, postfix: new HarmonyMethod(edit)) is not null;
    }

    // LeanVaryings' Stop, before the unpatch: a closed world's API is not kept
    internal static void Forget() => (_api, Patched) = (null, false);

    internal static void Edit(ShaderProgram program)
    {
        if (!NotNull(program) || !Assert(program.VertexShader is null || NotNull(program.VertexShader.Code))) return;
        LeanVaryings.Edit(program);
        SharpUpscale.Edit(program);
        LeanGodRays.Edit(program);
        LeanPost.Edit(program);
        ModShaders.Edit(program); // other mods' defines and edits (KometShaders), after Komet's own
        Mark(program);
    }

    // Every program knows Komet runs (#ifdef KOMET), and under its Vulkan renderer KOMET_VULKAN: a mod keeps its normal
    // path for the game alone and a tuned one for Komet in the same file
    private static void Mark(ShaderProgram program)
    {
        var defines = Komet.Api.KometShaders.Renderer == Komet.Api.KometRenderer.Vulkan ? "#define KOMET 1\r\n#define KOMET_VULKAN 1\r\n" : "#define KOMET 1\r\n";
        Shader?[] stages = [program.VertexShader, program.FragmentShader, program.GeometryShader];
        foreach (var shader in stages.Where(s => s is not null && !(s.PrefixCode ?? "").Contains("#define KOMET 1", StringComparison.Ordinal)).Bounded(3))
            shader!.PrefixCode += defines;
    }

    public static void Reload()
    {
        if (_api?.World is null || !NotNull(_api.World.Logger) || !NotNull(_api.Shader)) return;
        Links.ReloadShaders(_api);
    }
}

// God rays march up to 172 steps a pixel sunward, adding colour * glow.g. Only sky (and its fringe) has glow.g,
// elsewhere a step adds exactly 0, so colour is read only where glow.g != 0: same sum, fewer reads (one-level target,
// so the branched read takes level 0 as texture() did).
internal static class LeanGodRays
{
    private const string Step = "col += texture(inputTexture, uv) * texture(glowParts, uv).g * weight;";

    private const string LeanStep =
        "float kometGlow = texture(glowParts, uv).g; if (kometGlow != 0.0) col += textureLod(inputTexture, uv, 0.0) * kometGlow * weight;";

    public static bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            if (ClientSettings.GodRayQuality > 0) ShaderEdits.Reload();
        }
    } = true;

    internal static void Edit(ShaderProgram program)
    {
        if (!Enabled || program.PassName != "godrays" ||
            program.FragmentShader is not { Code: { } code } shader || !code.Contains(Step, StringComparison.Ordinal)) return;
        shader.Code = code.Replace(Step, LeanStep, StringComparison.Ordinal);
    }
}

// Post-processing and particles at a fraction of the work, a little softer: god rays march half the steps, each twice as
// long and weighed twice (decay squared), so a ray reaches as far and sums about as bright; the SSAO's bilateral blur
// drops its four outer taps of eleven (3.8 % of the kernel's weight, renormalised by wsum), 14 reads a pixel instead of
// 22 over its two passes; quad particles (rain: up to 80 000 alpha-blended quads close to the camera) read each shadow
// cascade once instead of 3 x 3 (2 shadow reads a pixel instead of 18), the same light and shadow without the soft
// edge, which a falling drop does not show. Each edit needs every line it changes, else that shader stays the engine's.
internal static class LeanPost
{
    private static readonly (string From, string To)[] GodRays =
    [
        ("float weight = intensity / 23.0 / 1.5;", "float weight = intensity / 23.0 / 1.5 * 2.0;"),
        ("int samples = int(180 * min(1, intensity * 1.2));", "int samples = int(90 * min(1, intensity * 1.2));"),
        ("vec2 sdTuv = clampDeltas((nSunPos - uv) * intensity / 200 * direction);",
            "vec2 sdTuv = 2.0 * clampDeltas((nSunPos - uv) * intensity / 200 * direction);"),
        ("vec2 ldTuv = clampDeltas((nSunPos - uv) * intensity / 64 * direction);",
            "vec2 ldTuv = 2.0 * clampDeltas((nSunPos - uv) * intensity / 64 * direction);"),
        ("weight *= decay;", "weight *= decay * decay;")
    ];

    // Before main in particlesquad.fsh, after its includes: getBrightnessFromShadowMap with one read per cascade
    private const string ParticleShadow = """

        float kometParticleShadow() {
        #if SHADOWQUALITY > 0
        	float b = 1.0;
        	if (shadowCoordsFar.w > 0) b -= shadowIntensity * (1.0 - texture(shadowMapFar, vec3(shadowCoordsFar.xy, shadowCoordsFar.z - 0.0009))) * shadowCoordsFar.w * 0.5;
        #if SHADOWQUALITY > 1
        	if (shadowCoordsNear.w > 0) b -= shadowIntensity * (1.0 - texture(shadowMapNear, vec3(shadowCoordsNear.xy, shadowCoordsNear.z - 0.0005))) * shadowCoordsNear.w * 0.5;
        #endif
        	return clamp(b + blockBrightness, 0, 1);
        #else
        	return 1.0;
        #endif
        }

        vec4 kometParticleFogAndShadow(vec4 rgbaPixel, float fogWeight) {
        	float b = kometParticleShadow();
        	return applyFog(rgbaPixel * vec4(b, b, b, 1), fogWeight);
        }

        """;

    // What ParticleShadow reads from fogandlight.fsh: a mod's own include without them keeps the engine's particles
    private static readonly string[] ParticleNeeds =
        ["in vec4 shadowCoordsFar;", "sampler2DShadow shadowMapFar;", "in vec4 shadowCoordsNear;", "sampler2DShadow shadowMapNear;",
            "in float blockBrightness;", "uniform float shadowIntensity", "vec4 applyFog(vec4 rgbaPixel, float fogWeight)"];

    private static readonly int[] Taps = [0, 1, 9, 10];
    private static readonly string[] TapWeights = ["0.003456", "0.015715", "0.015715", "0.003456"];
    private static readonly (string From, string To)[] Blur = OuterTaps();

    public static bool Enabled
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            ShaderEdits.Reload();
        }
    } = true;

    internal static void Edit(ShaderProgram program)
    {
        if (!Enabled || program.FragmentShader is not { Code: { } code } shader) return;
        if (program.PassName == "particlesquad")
        {
            Particles(shader, code);
            return;
        }

        var edits = program.PassName switch
        {
            "godrays" => GodRays,
            "bilateralblur" => Blur,
            _ => null
        };
        if (edits is null || !Array.TrueForAll(edits, e => code.Contains(e.From, StringComparison.Ordinal))) return;
        foreach (var (from, to) in edits.Bounded(16)) code = code.Replace(from, to, StringComparison.Ordinal);
        shader.Code = code;
    }

    // The function before main, both applyFogAndShadow calls to it. By the postfix the registry has expanded the
    // includes (HandleIncludes: no #include line is left, an anchor on it never matched), so what the function reads is
    // looked for in the code before main.
    private static void Particles(Shader shader, string code)
    {
        const string Call = "applyFogAndShadow(color, ";
        var main = code.IndexOf("void main", StringComparison.Ordinal);
        if (main < 0 || code.IndexOf(Call, main, StringComparison.Ordinal) < 0 ||
            !Array.TrueForAll(ParticleNeeds, n => code.IndexOf(n, 0, main, StringComparison.Ordinal) >= 0)) return;
        shader.Code = code[..main] + ParticleShadow +
                      code[main..].Replace(Call, "kometParticleFogAndShadow(color, ", StringComparison.Ordinal);
    }

    // Taps 0, 1, 9 and 10: weight 0 without the depth read, their colour read gone
    private static (string, string)[] OuterTaps()
    {
        var edits = new List<(string, string)>();
        for (var i = 0; i < Taps.Length; i++)
        {
            var (n, weight) = (Taps[i], TapWeights[i]);
            edits.Add(($"float w{n} = (1 - clamp(abs(texture(depthTexture, texCoords[{n}]).r - refDepth) * fac, 0, 1)) * {weight};",
                $"float w{n} = 0.0;"));
            edits.Add(($"out_colour += texture(inputTexture, texCoords[{n}]) * w{n};", ""));
        }

        return [.. edits];
    }
}
