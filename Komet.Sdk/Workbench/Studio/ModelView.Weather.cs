using System.Numerics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// Weather inputs in preview space. The volume uses the game's tile format and the same inverse projection
// as the opaque scene, expressed in world units instead of the small model's coordinates.
internal sealed partial class ModelView
{
    private const int WeatherSize = 48, AuroraBands = 15, AuroraSegments = 29;
    private const float WorldUnits = 50f, AuroraUnits = 125f;
    private const float AuroraAltitude = 300f, AuroraHeight = 200f;
    private const float CloudAltitude = 160f;
    private bool _companionPending;
    private (int First, int Count) _ribbon, _cloudTiles;
    private int _weatherInputs, _weatherWind, _mapFb, _mapTiles, _mapColors, _companion;
    private int _weatherMap, _weatherColor, _clearLiquidDepth;
    private (string Vertex, string Fragment)? _weatherSource;

    public void WeatherCompanion(string vertex, string fragment)
    {
        if (_weatherSource == (vertex, fragment)) return;

        _weatherSource = (vertex, fragment);
        _companionPending = true;
    }

    private void DrawCloudVolume(int program, Matrix4x4 view, Matrix4x4 projection, int height, bool customMap)
    {
        var worldView = Matrix4x4.CreateScale(1f / WorldUnits) * view;
        if (!Matrix4x4.Invert(worldView * projection, out var inverse)) return;

        CopySceneTargets(ClearBufferMask.DepthBufferBit, height);
        if (!BeginTransparency(height)) return;

        WeatherTextures(height);
        GL.Disable(EnableCap.DepthTest);
        GL.UseProgram(program);
        GL.BindVertexArray(customMap ? _builtinVao : _vao);
        BindPreviewTexture(program, "depthTex", 0, _sceneDepth);
        BindPreviewTexture(program, "liquidDepth", 1, _clearLiquidDepth);
        BindPreviewTexture(program, "cloudMap", 2, customMap ? _mapTiles : _weatherMap);
        BindPreviewTexture(program, "cloudCol", 3, customMap ? _mapColors : _weatherColor);
        Matrix(GL.GetUniformLocation(program, "iMvpMatrix"), inverse);
        GL.Uniform1(GL.GetUniformLocation(program, "cloudMapWidth"), (float)WeatherSize);
        GL.Uniform3(GL.GetUniformLocation(program, "cloudOffset"), 0f, CloudAltitude, 0f);
        GL.Uniform1(GL.GetUniformLocation(program, "FrameWidth"), Width);
        GL.Uniform1(GL.GetUniformLocation(program, "time"), (float)_clock.Elapsed.TotalSeconds);
        if (!customMap) Constants(true);

        GL.DrawArrays(PrimitiveType.Triangles, _ranges[Screen].First, _ranges[Screen].Count);
        if (!customMap) Constants(false);

        CompositeTransparency();
    }

    private bool DrawWeatherMap(Matrix4x4 view, int height)
    {
        if (_companionPending && _weatherSource is { } source)
        {
            _companionPending = false;
            if (_companion != 0) GL.DeleteProgram(_companion);

            _companion = PreviewProgram(source.Vertex, source.Fragment);
        }

        if (_companion == 0 || !WeatherMapReady()) return false;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mapFb);
        GL.Viewport(0, 0, WeatherSize, WeatherSize);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(IndexedEnableCap.Blend, 0);
        GL.Disable(IndexedEnableCap.Blend, 1);
        GL.ClearColor(0, 0, 0, 0);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.UseProgram(_program);
        GL.BindVertexArray(_vao);
        Textures(_blocks);
        BindPreviewTexture(_program, "mapData1", 0, _weatherInputs);
        BindPreviewTexture(_program, "mapData2", 1, _weatherWind);
        Matrix(GL.GetUniformLocation(_program, "viewMatrix"), Matrix4x4.CreateScale(1f / WorldUnits) * view);
        GL.Uniform1(GL.GetUniformLocation(_program, "width"), (float)WeatherSize);
        GL.Uniform1(GL.GetUniformLocation(_program, "alpha"), 1f);
        GL.Uniform1(GL.GetUniformLocation(_program, "time"), (float)_clock.Elapsed.TotalSeconds);
        SceneUniforms();
        GL.DrawArrays(PrimitiveType.Triangles, _ranges[Screen].First, _ranges[Screen].Count);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
        GL.Viewport(0, 0, Width, height);
        return true;
    }

    private bool WeatherMapReady()
    {
        if (_mapFb != 0) return true;

        if (_weatherMap == 0) BuildWeatherMap();
        _mapTiles = Texture<float>(WeatherSize, WeatherSize, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, null);
        _mapColors = Texture<float>(WeatherSize, WeatherSize, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, null);
        _mapFb = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _mapFb);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, _mapTiles, 0);
        GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1,
            TextureTarget.Texture2D, _mapColors, 0);
        GL.DrawBuffers(2, OitOutputs);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete)
        {
            return true;
        }

        ReleaseWeatherMapTargets();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
        return false;
    }

    private void WeatherTextures(int height)
    {
        if (_weatherMap == 0) BuildWeatherMap();
        if (_clearLiquidDepth != 0) return;

        var width = (Width + 3) / 4;
        var rows = (height + 3) / 4;
        var farDepth = new float[width * rows];
        Array.Fill(farDepth, 1f);
        _clearLiquidDepth = Texture(width, rows, PixelInternalFormat.DepthComponent24, PixelFormat.DepthComponent,
            farDepth);
    }

    private void BuildWeatherMap()
    {
        var tiles = new float[WeatherSize * WeatherSize * 4];
        var colors = new float[tiles.Length];
        var input = new float[tiles.Length];
        var wind = new float[tiles.Length];
        for (var i = 0; i < WeatherSize * WeatherSize; i++)
        {
            var x = i % WeatherSize - WeatherSize * 0.5f;
            var row = i / WeatherSize;
            var z = row - WeatherSize * 0.5f;
            var shape = MathF.Sin(x * 0.48f + MathF.Sin(z * 0.28f)) + MathF.Cos(z * 0.53f - x * 0.12f);
            var density = Math.Clamp((shape + 0.4f) * 0.72f, 0f, 1.4f);
            var bottom = -0.3f + 0.12f * MathF.Sin(x + z * 0.3f);
            var light = 0.80f + 0.16f * Math.Clamp(density, 0f, 1f);
            (tiles[4 * i], tiles[4 * i + 2], tiles[4 * i + 3]) =
                (density, bottom, bottom + 0.42f + density * 1.2f);
            (colors[4 * i], colors[4 * i + 1], colors[4 * i + 2], colors[4 * i + 3]) =
                (light, light * 0.99f, light * 0.97f, 1f);
            (input[4 * i], input[4 * i + 1], input[4 * i + 2], input[4 * i + 3]) =
                (0.05f, 0.16f + density * 0.27f, density * 0.6f, 1f);
            wind[4 * i] = 0.025f * density;
        }

        _weatherMap = Texture(WeatherSize, WeatherSize, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, tiles);
        _weatherColor = Texture(WeatherSize, WeatherSize, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, colors);
        _weatherInputs = Texture(WeatherSize, WeatherSize, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, input);
        _weatherWind = Texture(WeatherSize, WeatherSize, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, wind);
    }

    private void MakeAuroraRibbon(List<float> vertices)
    {
        var first = vertices.Count / 8;
        uint seed = 0x6178E917;
        for (var i = 0; i < AuroraBands; i++)
        {
            var direction = new Vector3(20f * AuroraRandom(ref seed) - 10f, 5f * AuroraRandom(ref seed) - 3f,
                20f * AuroraRandom(ref seed) - 10f);
            if (direction.LengthSquared() < 0.01f) direction = Vector3.UnitX;

            direction = Vector3.Normalize(direction) / 1.5f;
            var point = new Vector3(1200f * AuroraRandom(ref seed) - 600f, 120f * AuroraRandom(ref seed) - 60f,
                1200f * AuroraRandom(ref seed) - 600f);
            for (var step = 0; step < AuroraSegments; step++)
            {
                var next = point + direction * new Vector3(20f + 5f * AuroraRandom(ref seed),
                    4f + 4f * AuroraRandom(ref seed), 20f + 5f * AuroraRandom(ref seed));
                foreach (var (s, t) in Corners.Bounded(6))
                {
                    var p = Vector3.Lerp(point, next, s);
                    var column = step + (int)s;
                    // Aurora packs its along-band attribute into the fixture's unused normal X slot.
                    Vertex(vertices, p.X, p.Y + AuroraHeight * t, p.Z, column / (float)AuroraSegments, 0, 0,
                        column % 2, t);
                }

                point = next;
            }
        }

        _ribbon = (first, vertices.Count / 8 - first);
    }

    private static float AuroraRandom(ref uint seed)
    {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        return (seed >> 8) / 16777216f;
    }

    private void MakeCloudTiles(List<float> vertices)
    {
        var first = vertices.Count / 8;
        for (var tile = 0; tile < 24; tile++)
        {
            var angle = tile / 24f * MathF.Tau;
            var distance = 350f + tile % 4 * 68f;
            var x = MathF.Sin(angle) * distance;
            var z = MathF.Cos(angle) * distance;
            var y = CloudAltitude - 30f + tile % 4 * 9f;
            for (var side = 0; side < 6; side++)
            {
                foreach (var (s, t) in Corners.Bounded(6))
                {
                    var (u, v, w) = side switch
                    {
                        0 or 1 => (side * 110f, t * 20f, s * 85f),
                        2 or 3 => (s * 110f, (side - 2) * 20f, t * 85f),
                        _ => (s * 110f, t * 20f, (side - 4) * 85f)
                    };
                    Vertex(vertices, x + u, y + v, z + w, 0, 1, 0, s, t);
                }
            }
        }

        _cloudTiles = (first, vertices.Count / 8 - first);
    }

    private void ReleaseWeatherDepth()
    {
        if (_clearLiquidDepth != 0) GL.DeleteTexture(_clearLiquidDepth);

        _clearLiquidDepth = 0;
    }

    private void DisposeWeather()
    {
        if (_weatherMap != 0) GL.DeleteTexture(_weatherMap);

        if (_weatherColor != 0) GL.DeleteTexture(_weatherColor);

        if (_weatherInputs != 0) GL.DeleteTexture(_weatherInputs);

        if (_weatherWind != 0) GL.DeleteTexture(_weatherWind);

        if (_companion != 0) GL.DeleteProgram(_companion);

        (_weatherMap, _weatherColor) = (0, 0);
        (_weatherInputs, _weatherWind, _companion, _weatherSource, _companionPending) = (0, 0, 0, null, false);
        ReleaseWeatherMapTargets();
    }

    private void ReleaseWeatherMapTargets()
    {
        if (_mapFb != 0) GL.DeleteFramebuffer(_mapFb);

        if (_mapTiles != 0) GL.DeleteTexture(_mapTiles);

        if (_mapColors != 0) GL.DeleteTexture(_mapColors);

        (_mapFb, _mapTiles, _mapColors) = (0, 0, 0);
    }
}
