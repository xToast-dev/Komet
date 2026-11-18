using OpenTK.Graphics.OpenGL;
using GlBuffer = OpenTK.Graphics.OpenGL.BufferTarget;

namespace Komet.Sdk;

// The game's climate and season tint in the test scene: the blocks' texture is a small atlas like the game's (the block
// on the left half, a climate map and a season map on the right), the hill's grass tops carry the game's colormapData
// (climate map 1, season map 2, frostable, the temperature and rainfall), and colorMapRects, seasonRel, seaLevel,
// atlasHeight and seasonTemperature point the shader at them. Temperature, rainfall and season are the developer's to
// set under the picture, so a shader's tint can be looked at from a cold dry winter to a warm wet summer.
internal sealed partial class ModelView
{
    private const int AttrColormap = 10, AtlasW = 32, AtlasH = 16;
    private int _colormap;
    private bool _climateDirty = true;
    private float _temperature = 0.65f, _rainfall = 0.6f, _season = 0.3f;
    private readonly float[] _rects = [0.5f, 0f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f];

    // 0 cold .. 1 hot, as the game's tempRel
    public float Temperature { get => _temperature; set => (_temperature, _climateDirty) = (Math.Clamp(value, 0f, 1f), true); }

    // 0 dry .. 1 wet, as the game's rainfallRel
    public float Rainfall { get => _rainfall; set => (_rainfall, _climateDirty) = (Math.Clamp(value, 0f, 1f), true); }

    // 0 .. 1 through the year (the game's seasonRel; 0.5 midsummer)
    public float Season { get => _season; set => _season = Math.Clamp(value, 0f, 1f); }

    // Whether the open program reads the climate: its colormapData or a season uniform
    public bool UsesClimate { get; private set; }

    public void ResetClimate() => (Temperature, Rainfall, Season) = (0.65f, 0.6f, 0.3f);

    // The block on the left half (UVs 0..0.5), the climate map (temperature across, rainfall down) and the season map
    // (the year across) on the right
    private static int AtlasTexture()
    {
        var pixels = new byte[AtlasW * AtlasH * 4];
        var seed = 0x2545F491u;
        for (var i = 0; i < AtlasW * AtlasH; i++)
        {
            var (x, y) = (i % AtlasW, i / AtlasW);
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            var g = (byte)(150 + seed % 90);
            var (r, gr, b) = x < AtlasW / 2 ? (g, g, g) : MapColor((x - AtlasW / 2f) / (AtlasW / 2f - 1f), y);
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = (r, gr, b, 255);
        }

        var texture = Texture(AtlasW, AtlasH, PixelInternalFormat.Rgba8, PixelFormat.Rgba, pixels);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return texture;
    }

    // Rows 0..7: the climate (cold dry olive-brown to warm wet deep green); rows 8..15: the season (spring green,
    // summer, autumn orange, winter brown)
    private static (byte, byte, byte) MapColor(float across, int row)
    {
        if (row < AtlasH / 2)
        {
            var wet = row / (AtlasH / 2f - 1f);
            var (r, g, b) = (0.62f - 0.35f * wet + 0.1f * across, 0.6f + 0.15f * wet * across, 0.3f - 0.1f * wet);
            return (Byte(r), Byte(g), Byte(b));
        }

        ReadOnlySpan<(float R, float G, float B)> year = [(0.5f, 0.78f, 0.3f), (0.35f, 0.7f, 0.25f), (0.85f, 0.55f, 0.15f),
            (0.55f, 0.42f, 0.3f), (0.5f, 0.78f, 0.3f)];
        var at = across * 4f;
        var k = Math.Min((int)at, 3);
        var f = at - k;
        var (a, c) = (year[k], year[k + 1]);
        return (Byte(a.R + (c.R - a.R) * f), Byte(a.G + (c.G - a.G) * f), Byte(a.B + (c.B - a.B) * f));
    }

    private static byte Byte(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);

    // Per vertex: the grass tops of the hill tinted by both maps and frostable, every other face untinted
    private void ClimateBuffer(float[] vertices)
    {
        var count = vertices.Length / 8;
        var data = new int[count];
        var (first, blocks) = _parts[PartBlocks];
        var climate = 2 | (1 << 8) | (1 << 12) | ((int)(_temperature * 255) << 16) | ((int)(_rainfall * 255) << 24);
        for (var i = 0; i < Math.Min(count, 64 * 1024); i++)
        {
            data[i] = i >= first && i < first + blocks && vertices[i * 8 + 4] > 0.5f ? climate : 0;
        }

        if (_colormap == 0) _colormap = GL.GenBuffer();

        GL.BindBuffer(GlBuffer.ArrayBuffer, _colormap);
        GL.BufferData(GlBuffer.ArrayBuffer, count * sizeof(int), data, BufferUsageHint.StaticDraw);
        GL.BindBuffer(GlBuffer.ArrayBuffer, _vbo);
        _climateDirty = false;
    }

    private float[]? _vertices;

    // Before a draw: the per-vertex climate again when the developer moved temperature or rainfall
    private void RefreshClimate()
    {
        if (_climateDirty && _vertices is { } vertices) ClimateBuffer(vertices);
    }

    // The climate's uniforms; true when the name was one of them
    private bool ClimateUniform(string name, int location)
    {
        switch (name)
        {
            case "colorMapRects[0]": GL.Uniform4(location, 2, _rects); return true;
            case "seasonRel": GL.Uniform1(location, _season); return true;
            case "seaLevel": GL.Uniform1(location, 1000f); return true; // the scene under it: no height warming
            case "atlasHeight": GL.Uniform1(location, (float)AtlasH); return true;
            case "seasonTemperature": GL.Uniform1(location, 0f); return true;
            default: return false;
        }
    }
}
