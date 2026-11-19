using System.Text.RegularExpressions;
using Komet.Rendering;

namespace Komet.Test.Rendering;

// The distant cascade's shader additions on the game's own includes, and the geometry of its map: a centre on the texel grid, the
// matrix that takes it to the middle of the map, and when a map is due again. Its drawing needs a GL context: that is checked in game.
public sealed class DistantShadowsTests
{
    // ShaderProgram.collectUniformNames: the uniforms the engine finds and gives locations and texture units
    private static readonly Regex EngineUniforms = new(
        @"(\s|\r\n)uniform\s*(?<type>float|int|ivec2|ivec3|ivec4|vec2|vec3|vec4|sampler2DShadow|sampler2D|" +
        @"samplerCube|mat3|mat4x3|mat4)\s*(\[[\d\w]+\])?\s*(?<var>[\d\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(2));

    private static string Include(string name)
    {
        GameInstall.RequireAssets();
        return File.ReadAllText(Path.Combine(GameInstall.Assets, "game", "shaderincludes", name));
    }

    private static List<string> Uniforms(string code) =>
        [.. EngineUniforms.Matches(code).Select(m => m.Groups["var"].Value)];

    // Added once each, after the far cascade; the engine finds no new uniform, so no sampler moves to another texture unit
    [TestCase(DistantShadows.VertexFile)]
    [TestCase(DistantShadows.FragmentFile)]
    public void TheIncludesGetTheCascadeOnceAndTheEngineSeesNoNewUniform(string file)
    {
        var source = Include(file);
        var added = file == DistantShadows.VertexFile ? DistantShadows.Vertex(source) : DistantShadows.Fragment(source);
        Assert.That(added, Is.Not.Null, "the anchors are not in the game's include");
        var again = file == DistantShadows.VertexFile ? DistantShadows.Vertex(added) : DistantShadows.Fragment(added);
        Assert.Multiple(() =>
        {
            Assert.That(added, Does.Contain("shadowCoordsDistant").And.Contain(DistantShadows.Marker));
            Assert.That(added!.IndexOf("shadowCoordsDistant", StringComparison.Ordinal),
                Is.GreaterThan(added.IndexOf("shadowCoordsFar", StringComparison.Ordinal)), "after the far cascade");
            Assert.That(again, Is.Null, "added twice");
            Assert.That(Uniforms(added), Is.EqualTo(Uniforms(source)), "a new uniform the engine would bind");
        });
    }

    // The centre moves off the camera only across the light, onto a texel corner in the light's plane, wherever the camera is
    [TestCase(512568.3, 200.7, 512874.9, 0.3, 0.8, 0.52)]
    [TestCase(1000.5, 110.2, -2345.25, -0.6, 0.45, 0.66)]
    public void AMapIsCentredOnTheTexelGrid(double x, double y, double z, double lx, double ly, double lz)
    {
        double[] light = Unit([lx, ly, lz]), camera = [x, y, z];
        var map = DistantShadows.Plan(light, camera, 576, 4096);
        var texel = 2 * 576.0 / 4096;
        double[] delta = [map.Center[0] - x, map.Center[1] - y, map.Center[2] - z];
        Assert.Multiple(() =>
        {
            for (var row = 0; row < 2; row++)
            {
                var along = map.View[row] * map.Center[0] + map.View[4 + row] * map.Center[1] +
                            map.View[8 + row] * map.Center[2];
                Assert.That(Math.Abs(along / texel - Math.Round(along / texel)), Is.LessThan(1e-6),
                    $"row {row} off the grid");
            }

            Assert.That(delta[0] * light[0] + delta[1] * light[1] + delta[2] * light[2], Is.EqualTo(0).Within(1e-6));
            Assert.That(Math.Sqrt(delta.Sum(d => d * d)), Is.LessThanOrEqualTo(texel), "more than a texel away");
        });
    }

    private static double[] Unit(double[] v)
    {
        var length = Math.Sqrt(v.Sum(c => c * c));
        return [.. v.Select(c => c / length)];
    }
}
