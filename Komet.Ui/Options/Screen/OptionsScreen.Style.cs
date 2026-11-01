using Komet.Hud;
using Vintagestory.API.MathTools;

namespace Komet.Options;

// The look: the style in use (StyleLibrary), what lies under the screen (the style's shader or the blur), the camera a
// shader follows from page to page, and the change of style itself: the old picture moves toward the eye and fades, a
// flash and a scan line cross, the new one comes up from a little further away.
internal sealed partial class OptionsScreen
{
    private const double DollyOut = 0.32, DollyIn = 0.6, DollyGrow = 0.07, DollyShrink = 0.06, FlashPeak = 0.16;
    private const double DefaultOpacity = 0.75, MinCover = 0.85, CameraSpeed = 2.2;

    private StyleSheet _style = StyleSheet.Fallback();
    private double _clock; // seconds the screen has been drawn, for the shader's time and the transitions
    private double _dolly = -1; // seconds into a change of style; negative: none
    private bool _dollyWaits; // the new style is set, its first picture not yet on screen
    private (double X, double Y, double Zoom, double Page) _camera = (0, 0, 1, 0);
    private readonly float[] _panelRects = new float[SceneState.MaxPanels * 4];
    private readonly Vec4f _tint = new(1, 1, 1, 1);
    private readonly Vec4f _accent = new(); // the scene's accent, set anew each frame: no Vec4f a frame while a style shader shows
    private LoadedTexture _white;
    private readonly WorldGlobe _globe;
    private readonly WorldAtlas _atlas;
    private double _planet; // how far the planet has come in: it shows once the world map is there
    private long _prefetch; // the tick listener reading the world map before the first opening; 0: done
    private double _joined = double.NaN; // Monotonic when the prefetch first saw a player in a world
    private const double SettleSeconds = 60; // a join loads chunks meanwhile: the map read waits (opening reads at once)

    private static double Monotonic => Environment.TickCount64 / 1000.0;

    // In the world, before the first opening: the world map read once in the background, so the planet is there at once
    // as reading it took a second or two after the screen had opened, the planet popped in late. Stops once it is read;
    // a world without a map keeps asking, a property read every few seconds. Not while the world is still loading: the
    // read made a gigabyte of garbage beside the join's own.
    private void Prefetch(float _) // a tick listener: the interval is not needed
    {
        if (capi.World?.Player?.Entity is null)
        {
            _joined = double.NaN;
            return;
        }

        if (double.IsNaN(_joined)) _joined = Monotonic;
        if (Monotonic - _joined < SettleSeconds) return;
        if (_atlas.Texture != 0)
        {
            Komet.Core.Listeners.Unregister(capi.Event, _prefetch);
            _prefetch = 0;
            if (!IsOpened()) _planet = 1; // read while closed: shown whole from the first frame
            return;
        }

        if (!IsOpened()) _atlas.Update(Monotonic); // without a world or its map yet: nothing, asked again
    }

    // The motion setting with the style's say: 0 none, else a duration factor (calm moves slower)
    private double Pace => _settings.MenuMotion switch { 0 => 0, 1 => 1.6, _ => 1 };

    private bool Moves(string kind) => Pace > 0 && _style.On(kind);

    private Rgba Accent => _style["accent"];
    private Rgba Panel => Opaque(_style["panel"]);
    private Rgba HeaderBack => Opaque(_style["band"]);
    private Rgba Selected => _style["select"];
    private Rgba Title => _style["title"];
    private Rgba Faint => _style["faint"];
    private Rgba Soft => _style["soft"];
    private Rgba ChipBack => _style["chip"];
    private Rgba BubbleBack => _style["bubble"];

    // A ground's alpha as the opacity setting scales it, never below MinCover of the style's own: text stays readable
    private Rgba Opaque(Rgba color) =>
        color with { A = Math.Clamp(color.A * _settings.MenuOpacity / DefaultOpacity, color.A * MinCover, 1) };

    // The style the settings name, its fonts and colours taken; the picture is made anew
    private void Restyle()
    {
        var sheet = _styles.Sheet(_settings.MenuStyle);
        if (ReferenceEquals(sheet, _style)) return;
        _style = sheet;
        _fonts.Families(sheet.Font("text"), sheet.Font("heading"), sheet.Font("title"), sheet.Font("numbers"));
        _fonts.Colored(sheet["text"]);
        HudUi.Restyle(sheet); // the HUD, its windows and the overlay in the same look
        _widest.Clear();
        _dirty = true;
    }

    // The settings changed: a new style begins its change (at once when nothing moves or the screen is closed)
    private void StyleAsked()
    {
        if (_styles.Sheet(_settings.MenuStyle) is not { } next || ReferenceEquals(next, _style)) return;
        if (!IsOpened() || Pace <= 0 || !_canvas.Ready) Restyle();
        else if (_dolly < 0) (_dolly, _dollyWaits) = (0, false);
    }

    // Per frame: the change of style moves on; returns the picture's alpha and scale
    private (double Alpha, double Scale) Dolly(float deltaTime)
    {
        if (_dolly < 0 || !Finite(deltaTime)) return (1, 1);
        var (pace, before) = (Math.Max(1, Pace), _dolly);
        if (_dollyWaits)
        {
            if (!ReferenceEquals(OnScreen.Style, _style)) return (0, 1 - DollyShrink); // the new style's picture is painting
            _dollyWaits = false;
        }
        else _dolly += deltaTime / pace;

        if (before < DollyOut && _dolly >= DollyOut)
        {
            Restyle();
            (_dolly, _dollyWaits) = (DollyOut, true);
            return (0, 1 + DollyGrow);
        }

        if (_dolly < DollyOut)
        {
            var e = Smooth(_dolly / DollyOut);
            return (1 - e, 1 + DollyGrow * e * e);
        }

        var t = (_dolly - DollyOut) / DollyIn;
        if (t >= 1) _dolly = -1;
        var k = 1 - Math.Pow(1 - Math.Min(1, t), 3); // ease out
        return (k, 1 - DollyShrink * (1 - k));
    }

    // The flash and the scan line over everything while a style changes
    private void Flare(int width, int height)
    {
        if (_dolly < 0) return;
        var t = Math.Clamp(_dolly / (DollyOut + DollyIn), 0, 1);
        var flash = FlashPeak * Math.Sin(Math.PI * t);
        var a = _style["accent"];
        if (flash > 0.005) Solid(0, 0, width, height, a with { A = flash }, GlowDepth + 1);
        var line = Smooth(Math.Clamp((_dolly - DollyOut * 0.6) / (DollyIn * 0.9), 0, 1));
        if (line is > 0 and < 1)
            Solid(0, line * height, width, Math.Max(1, scaled(1)), _style["changed"] with { A = Math.Sin(Math.PI * line) }, GlowDepth + 1);
    }

    // Under the screen: the style's shader, else the blur, then the style's dim
    private void Underlay(double eased, float deltaTime)
    {
        if (!Finite(eased) || !Finite(deltaTime)) return;
        Follow(deltaTime);
        var shown = false;
        if (_style.Shader is { } shader)
        {
            var blurred = Backdrop.Enabled ? _backdrop.Blurred(1) : 0;
            var a = _style["accent"];
            var state = new SceneState((float)_clock, (float)_camera.Page, (float)_camera.X, (float)_camera.Y, (float)_camera.Zoom,
                _accent.Set((float)a.R, (float)a.G, (float)a.B, (float)a.A), _panelRects, Panels(), (float)(_style.Size("veil") * _lens.Alpha),
                _atlas.Texture, _globe.Tints, _globe.Place, (float)_planet, _globe.Weather, _atlas.Scale());
            _globe.Locate();
            var now = Monotonic; // _clock starts over with each opening: refreshes count on a clock that does not
            _globe.Sky(now);
            _atlas.Update(now); // the explored world map, read on a worker
            _planet = Ease(_planet, _atlas.Texture != 0 ? 1 : 0, 1 - Math.Exp(-deltaTime * 2.5)); // no map, no planet
            _globe.Begin(now);
            _globe.Step(); // a slice a frame: the world around the player for the shader
            shown = _scene.Draw(shader, DimDepth - 1, eased * _style.Size("scene"), blurred, state);
        }

        var blur = !shown && _style.Blur && _backdrop.Draw(DimDepth - 1, eased);
        var dim = shown || blur ? _style["backdrop"] : _style["backdropPlain"];
        Solid(0, 0, capi.Render.FrameWidth, capi.Render.FrameHeight, dim with { A = dim.A * eased }, DimDepth);
    }

    // The shown picture's panels (grounds with corner marks) as frame pixels for the veil; their count
    private int Panels()
    {
        var (count, backs) = (0, OnScreen.Backs);
        foreach (var b in backs.Bounded(MaxItems))
        {
            if (!b.Panel || count >= SceneState.MaxPanels) continue;
            (_panelRects[count * 4], _panelRects[count * 4 + 1], _panelRects[count * 4 + 2], _panelRects[count * 4 + 3]) =
                ((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
            count++;
        }

        return count;
    }

    // The camera eases toward where the page puts it: sections apart, pages a step within
    private void Follow(float deltaTime)
    {
        var (section, page) = Place();
        var pan = Moves("pan");
        var target = pan
            ? (X: section * 0.3 + page * 0.07, Y: (page % 3 - 1) * 0.05, Zoom: 1 + 0.04 * (page % 4) + 0.06 * (section % 2),
                Page: section * 10.0 + page)
            : (X: 0, Y: 0, Zoom: 1, Page: section * 10.0 + page);
        var k = pan ? 1 - Math.Exp(-deltaTime * CameraSpeed / Math.Max(1, Pace)) : 1;
        if (!Finite(k)) return;
        _camera = (Ease(_camera.X, target.X, k), Ease(_camera.Y, target.Y, k), Ease(_camera.Zoom, target.Zoom, k),
            Ease(_camera.Page, target.Page, k));
    }

    // The open page as (section index, index within its section)
    private (int Section, int Page) Place()
    {
        var (section, page) = (-1, 0);
        string? last = null;
        foreach (var p in _pages.Bounded(MaxPages))
        {
            if (p.Section != last) (section, page, last) = (section + 1, 0, p.Section);
            else page++;
            if (p.Id == _page) return (Math.Max(0, section), page);
        }

        return (0, 0);
    }

    private static double Smooth(double t) => Finite(t) ? Math.Clamp(t, 0, 1) * Math.Clamp(t, 0, 1) * (3 - 2 * Math.Clamp(t, 0, 1)) : 1;

    // A filled rectangle in frame pixels: a white texture tinted (the engine's RenderRectangle draws only an outline)
    private void Solid(double x, double y, double w, double h, Rgba color, float z)
    {
        if (!Finite(x + y + w + h) || w <= 0 || h <= 0 || !Finite(color.A) || color.A <= 0.002) return;
        if (_white.TextureId == 0)
        {
            using var surface = new Cairo.ImageSurface(Cairo.Format.Argb32, 4, 4);
            using (var ctx = new Cairo.Context(surface))
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Paint();
            }

            surface.Flush();
            capi.Gui.LoadOrUpdateCairoTexture(surface, false, ref _white);
        }

        _ = _tint.Set((float)color.R, (float)color.G, (float)color.B, (float)Math.Min(1, color.A));
        capi.Render.Render2DTexture(_white.TextureId, (float)x, (float)y, (float)w, (float)h, z, _tint);
    }
}
