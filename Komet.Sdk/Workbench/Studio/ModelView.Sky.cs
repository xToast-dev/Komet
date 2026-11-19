using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

internal sealed partial class ModelView
{
    // Godrays radiate from the glow parts (the sun the game drew into its glow target): the view has none, so a soft
    // disk stands where the sun uniforms below say it is
    private const float SunU = 0.72f, SunV = 0.78f;
    private (int First, int Count) _celestial;

    private (int First, int Count) _effect;
    private bool _godrays;
    private Vector3? _object; // while the movable object draws: its pivot
    private int _sunDisk;
    private int _sunParts;

    private void MakeCelestial(List<float> vertices)
    {
        var first = vertices.Count / 8;
        foreach (var (s, t) in Corners.Bounded(6))
        {
            var across = (s - 0.5f) * 1.35f;
            Vertex(vertices, 9f + across, 3.8f + (t - 0.5f) * 1.9f, 14f - across, -0.7f, 0, -0.7f, s, t);
        }

        _celestial = (first, vertices.Count / 8 - first);
    }

    // The portal an effect shader (rift, galaxy) draws on: one upright quad on the hill, facing the camera's start
    private void MakeEffect(List<float> vertices)
    {
        var first = vertices.Count / 8;
        foreach (var (s, t) in Corners.Bounded(6))
        {
            var across = (s - 0.5f) * 1.6f;
            Vertex(vertices, across * 0.7f, Block + t * 2.4f, -across * 0.7f, -0.7f, 0, -0.7f, s,
                t); // faces the start view
        }

        _effect = (first, vertices.Count / 8 - first);
    }

    private static int SunParts()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var row = i / size;
            var (u, v) = ((i % size + 0.5f) / size - SunU, (row + 0.5f) / size - SunV);
            // as faint as the game's sun in its glow target: a full 1 saturates the rays into a black hole
            var glow = Math.Clamp(1.4f - MathF.Sqrt(u * u + v * v) * 14f, 0, 1) * 0.3f;
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (0, (byte)(glow * 255), 0, 255);
        }

        var texture = Texture(size, size, PixelInternalFormat.Rgba8, PixelFormat.Rgba, pixels);
        ClampPreviewTexture();
        return texture;
    }

    // The godrays' sun: on the glow disk, the camera looking at it; true when the uniform was one of them
    private static bool SunUniform(string name, int location)
    {
        switch (name)
        {
            case "sunPosScreenIn":
                GL.Uniform3(location, SunU * 2 - 1, SunV * 2 - 1, 0f);
                return true;
            case "sunPos3dIn" or "playerViewVector":
                GL.Uniform3(location, 0.42f, 0.38f, 0.82f);
                return true;
            case "dusk":
                GL.Uniform1(location, 0);
                return true;
            default: return false;
        }
    }

    // The colour grade at the player's neutral settings (the values copied from the game left its picture black), no fog
    private static bool GradeUniform(string name, int location)
    {
        switch (name)
        {
            case "gammaLevel" or "brightnessLevel" or "maxlight" or "maxsat":
                GL.Uniform1(location, 1f);
                return true;
            case "sepiaLevel" or "damageVignetting" or "minlight" or "minsat":
                GL.Uniform1(location, 0f);
                return true;
            // the game's fog is set for its view distance: over the few units of the test scene it whitens everything
            case "fogDensityIn" or "fogMinIn" or "flatFogDensity":
                GL.Uniform1(location, 0f);
                return true;
            default: return false;
        }
    }

    private void SkyTextures()
    {
        const int size = 32;
        var sky = new byte[size * size * 4];
        var glow = new byte[sky.Length];
        for (var i = 0; i < size * size; i++)
        {
            var row = i / size;
            var height = row / (size - 1f);
            (sky[i * 4], sky[i * 4 + 1], sky[i * 4 + 2], sky[i * 4 + 3]) =
                ((byte)(105 + height * 101), (byte)(157 + height * 69), (byte)(237 + height * 12), 255);
            (glow[i * 4], glow[i * 4 + 1], glow[i * 4 + 2], glow[i * 4 + 3]) =
                (255, 211, 169, (byte)(12 * (1 - height)));
        }

        _skyGradient = Texture(size, size, PixelInternalFormat.Rgba8, PixelFormat.Rgba, sky);
        ClampPreviewTexture();
        _sunGlow = Texture(size, size, PixelInternalFormat.Rgba8, PixelFormat.Rgba, glow);
        ClampPreviewTexture();
        _stars = StarTexture();
        _sunDisk = SunTexture();
    }

    private static int SunTexture()
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            var x = (i % size + 0.5f) / size * 2f - 1f;
            var row = i / size;
            var y = (row + 0.5f) / size * 2f - 1f;
            var alpha = Math.Clamp((0.96f - MathF.Sqrt(x * x + y * y)) * 9f, 0f, 1f);
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) =
                (255, 248, 211, (byte)(alpha * 255f));
        }

        var texture = Texture(size, size, PixelInternalFormat.Rgba8, PixelFormat.Rgba, pixels);
        ClampPreviewTexture();
        return texture;
    }

    private static void ClampPreviewTexture()
    {
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
    }

    private static int StarTexture()
    {
        const int size = 128;
        var texture = GL.GenTexture();
        GL.ActiveTexture(TextureUnit.Texture0 + StarUnit);
        GL.BindTexture(TextureTarget.TextureCubeMap, texture);
        var pixels = new byte[size * size * 4];
        var seed = 0x8A2EDB73u;
        for (var face = 0; face < 6; face++)
        {
            for (var i = 0; i < size * size; i++)
            {
                seed ^= seed << 13;
                seed ^= seed >> 17;
                seed ^= seed << 5;
                var bright = (int)(seed % 160);
                var star = bright < 3 ? 92 - bright * 19 : 0;
                (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) =
                    ((byte)(10 + star), (byte)(13 + star), (byte)(24 + star), 255);
            }

            GL.TexImage2D(TextureTarget.TextureCubeMapPositiveX + face, 0, PixelInternalFormat.Rgba8, size, size, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        }

        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT,
            (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR,
            (int)TextureWrapMode.ClampToEdge);
        return texture;
    }
}
