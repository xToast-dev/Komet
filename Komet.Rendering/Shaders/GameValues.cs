using Komet.Diagnostics;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// Game state as ready uniforms for the player's own shaders, all named "komet…": a shader gets a value by declaring it
// ("uniform vec3 kometSunPosition;"). They go through ModShaders' uniform registry, which binds a name only in programs
// that declare it (a cached location per program, -1 costs nothing more) and reads each value once a frame. The table is
// the one source for the registration, the Studio's GAME VALUES list, the editor's hover and its completion.
// Registered lazily (Ensure): when the Studio opens, a shader pack registers or a project's copy runs, so a game without
// any of them keeps the registry's empty early out. Main thread.
internal static class GameValues
{
    public const string Mod = "komet-game";

    // One value: GLSL name, type, what it holds, and how it is read into a reused array (no allocation per frame)
    internal sealed record Value(string Name, string Type, string Doc, Action<ICoreClientAPI, float[]> Reader, bool Color)
    {
        public float[] Last { get; } = new float[Size(Type)];

        private static int Size(string type) =>
            type switch
            {
                "vec2" => 2,
                "vec3" => 3,
                "vec4" => 4,
                _ => 1
            };
    }

    public static readonly Value[] All =
    [
        new("kometSunPosition", "vec3", "Direction to the sun (normalised), as the game's sky uses it.", Sun, false),
        new("kometMoonPosition", "vec3", "Direction to the moon from the camera.", Moon, false),
        new("kometDayLight", "float", "Daylight at the player, 0 at night to 1 at noon.", DayLight, false),
        new("kometTimeOfDay", "float", "Time of day, 0 at midnight to 1 at the next midnight.", TimeOfDay, false),
        new("kometMoonPhase", "float", "Moon phase, 0 to 8 (0 new moon, 4 full moon).", (a, v) => v[0] = (float)a.World.Calendar.MoonPhaseExact, false),
        new("kometSeason", "float", "Season of the year, 0 to 1 (0 is early spring).", (a, v) => v[0] = a.Render.ShaderUniforms.SeasonRel, false),
        new("kometFogColor", "vec4", "The scene's blended fog colour.", FogColor, true),
        new("kometFogDensity", "float", "The scene's blended fog density.", (a, v) => v[0] = a.Ambient.BlendedFogDensity, false),
        new("kometFogMin", "float", "The minimum fog everywhere.", (a, v) => v[0] = a.Ambient.BlendedFogMin, false),
        new("kometAmbientColor", "vec3", "The scene's blended ambient light colour.", Ambient, true),
        new("kometSceneBrightness", "float", "The scene's blended brightness.", (a, v) => v[0] = a.Ambient.BlendedSceneBrightness, false),
        new("kometCameraPosition", "vec3", "The camera's world position, relative to the player's reference position.", Camera, false),
        new("kometWindSpeed", "float", "Wind speed at the player.", (a, v) => v[0] = a.Render.ShaderUniforms.WindSpeed, false),
        new("kometWindWave", "float", "The wind's wave counter (grows with time and wind).", (a, v) => v[0] = a.Render.ShaderUniforms.WindWaveCounter, false),
        new("kometRain", "float", "Rainfall at the player, 0 to 1.", Rain, false),
        new("kometTime", "float", "Seconds counter that the game's shaders animate with.", (a, v) => v[0] = a.Render.ShaderUniforms.TimeCounter, false)
    ];

    private static readonly List<IDisposable> Registered = [];
    private static ICoreClientAPI? _api;
    private static long _rainFrame = -1;
    private static float _rain;

    public static Value? Find(string name) => Array.Find(All, v => v.Name == name);

    // Registers every value once (until Clear); later calls do nothing
    public static void Ensure(ICoreClientAPI api)
    {
        _api = api;
        if (Registered.Count > 0) return;

        foreach (var value in All.Bounded(64))
        {
            var each = value;
            Registered.Add(ModShaders.Uniform(Mod, each.Name, () => Read(each)));
        }
    }

    // World end (ModShaders.Clear): the next world registers again when needed
    public static void Clear()
    {
        Registered.Clear(); // ModShaders.Clear dropped the entries themselves
        _api = null;
    }

    // The value now, for the uniform and the Studio's list; the last good one when the game cannot say yet
    public static float[] Read(Value value)
    {
        if (_api is not { World.Player.Entity: not null } api) return value.Last;

        try
        {
            value.Reader(api, value.Last);
        }
        catch (Exception e) when (e is NullReferenceException or InvalidOperationException)
        {
            // a world still loading: the last value stays
        }

        return value.Last;
    }

    private static void Set(float[] v, Vec3f? from)
    {
        if (from is not null) (v[0], v[1], v[2]) = (from.X, from.Y, from.Z);
    }

    private static void Sun(ICoreClientAPI api, float[] v)
    {
        Set(v, api.Render.ShaderUniforms.SunPosition3D);
        var length = MathF.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);
        if (length > 1e-6f) (v[0], v[1], v[2]) = (v[0] / length, v[1] / length, v[2] / length);
    }

    private static void Moon(ICoreClientAPI api, float[] v) =>
        Set(v, api.World.Calendar.GetMoonPosition(api.World.Player.Entity.CameraPos, api.World.Calendar.TotalDays));

    private static void DayLight(ICoreClientAPI api, float[] v)
    {
        var pos = api.World.Player.Entity.Pos;
        v[0] = api.World.Calendar.GetDayLightStrength(pos.X, pos.Z);
    }

    private static void TimeOfDay(ICoreClientAPI api, float[] v)
    {
        var calendar = api.World.Calendar;
        v[0] = calendar.HoursPerDay > 0 ? calendar.HourOfDay / calendar.HoursPerDay : 0;
    }

    private static void FogColor(ICoreClientAPI api, float[] v)
    {
        var fog = api.Ambient.BlendedFogColor;
        (v[0], v[1], v[2], v[3]) = (fog.R, fog.G, fog.B, fog.A);
    }

    private static void Ambient(ICoreClientAPI api, float[] v) => Set(v, api.Ambient.BlendedAmbientColor);

    private static void Camera(ICoreClientAPI api, float[] v)
    {
        var camera = api.World.Player.Entity.CameraPos;
        var origin = api.Render.ShaderUniforms.playerReferencePos;
        (v[0], v[1], v[2]) = origin is null
            ? ((float)camera.X, (float)camera.Y, (float)camera.Z)
            : ((float)(camera.X - origin.X), (float)(camera.Y - origin.Y), (float)(camera.Z - origin.Z));
    }

    // The climate lookup is the dearest: once a frame at most
    private static void Rain(ICoreClientAPI api, float[] v)
    {
        var frame = FrameClock.Frames;
        if (frame == 0 || frame != _rainFrame)
        {
            _rainFrame = frame;
            var climate = api.World.BlockAccessor.GetClimateAt(api.World.Player.Entity.Pos.AsBlockPos);
            _rain = climate?.Rainfall ?? 0;
        }

        v[0] = _rain;
    }
}
