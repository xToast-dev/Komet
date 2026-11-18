using System.Text.RegularExpressions;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// A completion entry: its name, kind (f function, t type, k keyword, u uniform, d define, v in/out, s snippet), the
// signature shown in the doc pane, its doc text, where it comes from, and for a snippet the code and what it needs
internal sealed record ShaderSymbol(
    string Name,
    char Kind,
    string Signature,
    string Doc,
    string Source,
    string? Include = null,
    string? Declaration = null,
    string[]? Keys = null);

// IntelliSense's words: GLSL's functions, types and keywords, the game's include symbols (fogandlight.fsh and the
// uniforms every shader gets), the open file's own symbols and the snippets
internal static class ShaderDocs
{
    public const int MaxSymbols = 256;
    public const string FogInclude = "fogandlight.fsh";

    private static ShaderSymbol[]? _glsl, _engine, _snippets;

    public static ShaderSymbol[] Glsl => _glsl ??= BuildGlsl();
    public static ShaderSymbol[] Engine => _engine ??= BuildEngine();
    public static ShaderSymbol[] Snippets => _snippets ??= BuildSnippets();

    // Komet's game values (GAME VALUES declares them on a click)
    public static ShaderSymbol[] Komet => _komet ??=
    [
        .. Rendering.GameValues.All.Select(v => new ShaderSymbol(v.Name, 'u', "uniform " + v.Type + " " + v.Name, v.Doc,
            "Komet", null, "uniform " + v.Type + " " + v.Name + ";"))
    ];

    // The game's include files, completed after #include
    public static ShaderSymbol[] Includes => _includes ??=
    [
        .. StudioLink.List(Path.Join(StudioLink.AssetsRoot, "game", "shaderincludes")).Where(e => !e.Folder)
            .Select(e => new ShaderSymbol(e.Name, 'k', "#include " + e.Name, L("doc-include"), "Vintage Story"))
    ];

    private static ShaderSymbol[]? _komet, _includes;

    private static ShaderSymbol F(string name, string signature) =>
        new(name, 'f', signature, L("doc-" + name), "GLSL");

    private static ShaderSymbol[] BuildGlsl()
    {
        List<ShaderSymbol> list =
        [
            F("texture", "vec4 texture(sampler2D tex, vec2 uv)"),
            F("mix", "genType mix(genType x, genType y, float a)"),
            F("clamp", "genType clamp(genType x, float lo, float hi)"),
            F("smoothstep", "genType smoothstep(float edge0, float edge1, genType x)"),
            F("length", "float length(genType v)"), F("normalize", "genType normalize(genType v)"),
            F("dot", "float dot(genType a, genType b)"), F("cross", "vec3 cross(vec3 a, vec3 b)"),
            F("max", "genType max(genType a, genType b)"), F("min", "genType min(genType a, genType b)"),
            F("pow", "genType pow(genType x, genType y)"), F("abs", "genType abs(genType x)"),
            F("step", "genType step(float edge, genType x)"), F("fract", "genType fract(genType x)"),
            F("floor", "genType floor(genType x)"), F("sin", "genType sin(genType a)"),
            F("cos", "genType cos(genType a)"), F("sqrt", "genType sqrt(genType x)"),
            F("exp", "genType exp(genType x)")
        ];
        foreach (var type in (ReadOnlySpan<string>)
                 ["float", "int", "bool", "vec2", "vec3", "vec4", "ivec2", "mat3", "mat4", "sampler2D"])
        {
            list.Add(new ShaderSymbol(type, 't', type, L("doc-type"), ""));
        }

        foreach (var word in (ReadOnlySpan<string>)
                 ["uniform", "in", "out", "const", "if", "else", "for", "return", "discard", "void", "#define", "#include"])
        {
            list.Add(new ShaderSymbol(word, 'k', word, L("doc-keyword"), ""));
        }

        return [.. list];
    }

    private static ShaderSymbol[] BuildEngine()
    {
        const string game = "Vintage Story";
        return
        [
            new("applyFog", 'f', "vec4 applyFog(vec4 rgbaPixel, float fogWeight)", L("doc-applyfog"), FogInclude),
            new("getFogLevel", 'f', "float getFogLevel(vec4 worldPos, float fogMin, float fogDensity)",
                L("doc-getfoglevel"), FogInclude),
            new("rgbaFogIn", 'u', "uniform vec4 rgbaFogIn", L("doc-rgbafogin"), FogInclude),
            new("fogDensityIn", 'u', "uniform float fogDensityIn", L("doc-fogdensityin"), FogInclude),
            new("fogMinIn", 'u', "uniform float fogMinIn", L("doc-fogminin"), FogInclude),
            new("dayLight", 'u', "uniform float dayLight", L("doc-daylight"), game),
            new("sunPosition", 'u', "uniform vec3 sunPosition", L("doc-sunposition"), game),
            new("windWaveCounter", 'u', "uniform float windWaveCounter", L("doc-windwavecounter"), game)
        ];
    }

    private static ShaderSymbol[] BuildSnippets() =>
        [
            new(L("snip-fog"), 's',
                "float fogAmount = getFogLevel(worldPos, fogMinIn, fogDensityIn);\noutColor = applyFog(outColor, fogAmount);",
                L("snip-fog-doc"), "Snippet", FogInclude, null, ["nebel", "fog", "applyfog"]),
            new(L("snip-vignette"), 's', "float vd = length(uv - 0.5);\noutColor.rgb *= smoothstep(0.85, 0.25, vd);",
                L("snip-vignette-doc"), "Snippet", null, null, ["vignette"]),
            new(L("snip-tint"), 's', "outColor.rgb *= vec3(1.05, 1.0, 0.92);", L("snip-tint-doc"), "Snippet", null,
                null, ["farbton", "tint"]),
            new(L("snip-daylight"), 's', "outColor.rgb *= mix(0.35, 1.0, dayLight);", L("snip-daylight-doc"),
                "Snippet", null, "uniform float dayLight;", ["tageslicht", "daylight"])
        ];

    // The open file's uniforms, defines, functions and ins/outs
    public static List<ShaderSymbol> FileSymbols(string source)
    {
        List<ShaderSymbol> symbols = [];
        var file = L("source-file");
        foreach (var g in UniformRx.Matches(source).Select(m => m.Groups).Bounded(64))
        {
            symbols.Add(new ShaderSymbol(g[2].Value, 'u', "uniform " + g[1].Value + " " +
                                                                g[2].Value, L("doc-in-file"), file));
        }

        foreach (var g in DefineRx.Matches(source).Select(m => m.Groups).Bounded(64))
        {
            symbols.Add(new ShaderSymbol(g[1].Value, 'd', "#define " + g[1].Value + " " +
                                                                g[2].Value.Trim(), L("doc-file-constant"), file));
        }

        foreach (var g in FunctionRx.Matches(source).Select(m => m.Groups).Bounded(64))
        {
            if (g[2].Value != "main")
            {
                symbols.Add(new ShaderSymbol(g[2].Value, 'f', g[1].Value + " " + g[2].Value + "(" +
                                                                     g[3].Value + ")", L("doc-file-function"), file));
            }
        }

        foreach (var g in InOutRx.Matches(source).Select(m => m.Groups).Bounded(64))
        {
            symbols.Add(new ShaderSymbol(g[2].Value, 'v', g[1].Value + " " + g[2].Value,
                L("doc-in-out"), file));
        }

        return symbols;
    }

    private static readonly Regex UniformRx =
        new(@"uniform\s+(\w+)\s+(\w+)\s*;",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex DefineRx =
        new(@"#define\s+(\w+)\s+([^\n]*)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex FunctionRx =
        new(@"\b(float|vec[234]|int|void|bool)\s+(\w+)\s*\(([^)]*)\)\s*\{",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex InOutRx =
        new(@"^\s*(?:in|out)\s+(\w+)\s+(\w+)\s*;",
            RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
}
