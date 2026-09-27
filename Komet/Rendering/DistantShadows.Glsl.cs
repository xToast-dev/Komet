using System.Text.RegularExpressions;


namespace Komet.Rendering;

// The shader side of the distant cascade, added to the engine's two shadow includes when they are loaded. shadowcoords.vsh computes
// the vertex's coordinates in the distant map and its weight: what the near and far cascades leave, faded out at the map's edge.
// fogandlight.fsh darkens by that weight like the far cascade, with four hardware-filtered taps. The fragment part compiles only with
// KOMET_DISTANT, defined for a program whose vertex shader includes shadowcoords.vsh: a fragment input no vertex shader writes fails
// to link. The new uniforms carry a precision qualifier, which keeps them out of the engine's uniform scan: a sampler it found would
// take a texture unit and move the units of every sampler declared after it.
internal static partial class DistantShadows
{
    internal const string VertexFile = "shadowcoords.vsh", FragmentFile = "fogandlight.fsh", Marker = "kometDistant";
    internal const string Definition = "#define KOMET_DISTANT 1\r\n";

    private const string VertexDeclarations = """
        #if SHADOWQUALITY > 0
        uniform highp mat4 kometDistantMatrix;
        uniform highp float kometDistantOn;
        out vec4 shadowCoordsDistant;
        #endif

        """;

    // After the far cascade: shadowCoordsFar.w already has the near weight taken off, so the three weights add up to 1 at most
    private const string VertexBody = """

        #if SHADOWQUALITY > 0
        	shadowCoordsDistant = kometDistantMatrix * worldPos;
        	float kometEdge = clamp((max(0.0, 0.02 - shadowCoordsDistant.x) + max(0.0, shadowCoordsDistant.x - 0.98) +
        		max(0.0, 0.02 - shadowCoordsDistant.y) + max(0.0, shadowCoordsDistant.y - 0.98)) * 50.0, 0.0, 1.0);
        	shadowCoordsDistant.w = kometDistantOn * max(0.0, 1.0 - kometEdge - shadowCoordsFar.w - nearSub);
        	if (shadowCoordsDistant.z >= 0.999) shadowCoordsDistant.w = 0.0;
        #endif

        """;

    private const string FragmentDeclarations = """

        #if SHADOWQUALITY > 0 && defined(KOMET_DISTANT)
        in vec4 shadowCoordsDistant;
        uniform highp sampler2DShadow kometDistantMap;
        uniform highp vec2 kometDistantTexel;
        #endif

        """;

    // kometDistantTexel: x the size of a texel, y the depth bias
    private const string FragmentBody = """

        	#if defined(KOMET_DISTANT)
        	if (shadowCoordsDistant.w > 0) {
        		float kometLit = 0.0;
        		for (int x = 0; x < 2; x++) {
        			for (int y = 0; y < 2; y++) {
        				kometLit += texture(kometDistantMap, vec3(shadowCoordsDistant.xy + (vec2(x, y) - 0.5) * kometDistantTexel.x,
        					shadowCoordsDistant.z - kometDistantTexel.y));
        			}
        		}
        		b -= shadowIntensity * (1.0 - kometLit / 4.0) * shadowCoordsDistant.w * 0.5;
        	}
        	#endif

        """;

    // Where the additions go: behind the far cascade's declarations, and behind the statement that sets its weight or brightness
    private static readonly Regex VertexDeclared = Pattern(@"out vec4 shadowCoordsFar;\s*#endif[^\n]*\n");
    private static readonly Regex VertexWeighted =
        Pattern(@"shadowCoordsFar\.w = max\(0\.0, clamp\(1\.0 - distanceFar, 0\.0, 1\.0\) - nearSub\);[^\n]*\n" +
                @"\s*if \(shadowCoordsFar\.z >= 0\.999\) shadowCoordsFar\.w = 0\.0;[^\n]*\n\s*#endif[^\n]*\n");
    private static readonly Regex FragmentDeclared = Pattern(@"uniform float shadowMapHeightInv;\s*#endif[^\n]*\n");
    private static readonly Regex FragmentShaded =
        Pattern(@"float b = 1\.0 - shadowIntensity \* totalFar \* shadowCoordsFar\.w \* 0\.5;[^\n]*\n");

    private static Regex Pattern(string pattern) =>
        NotNull(pattern)
            ? new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(2))
            : throw new ArgumentNullException(nameof(pattern));

    // The include with the distant cascade added, or null when it is not the text the additions were written for (another mod's
    // shader pack, a new engine version): the engine's shaders then stay as they are
    internal static string? Vertex(string? source) =>
        Add(source, VertexDeclared, VertexDeclarations, VertexWeighted, VertexBody);

    internal static string? Fragment(string? source) =>
        Add(source, FragmentDeclared, FragmentDeclarations, FragmentShaded, FragmentBody);

    private static string? Add(string? source, Regex declared, string declarations, Regex anchor, string body)
    {
        if (source is null || source.Contains(Marker, StringComparison.Ordinal)) return null;
        var (first, second) = (declared.Matches(source), anchor.Matches(source));
        if (first.Count != 1 || second.Count != 1 || !Assert(first[0].Index < second[0].Index)) return null;
        var (at, to) = (first[0].Index + first[0].Length, second[0].Index + second[0].Length);
        return Assert(at <= to) ? source[..at] + declarations + source[at..to] + body + source[to..] : null;
    }
}
