using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// OpenTK answers NativeWindow.ClientSize with a GLFW call, on X11 a round trip to the display server, and the engine asks a dozen
// times per frame. The size changes only through the setter or NativeWindow.OnResize (GameWindow and GameWindowNative do not override
// it), so the answer is kept until one of those runs.
internal static class WindowSizeCache
{
    private static Vector2i _size;
    private static bool _valid;
    public static bool Enabled { get; set; } = true;

    public static void Install(Harmony harmony)
    {
        _valid = false; // nothing watched the window between the sessions
        var get = AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize));
        var set = AccessTools.PropertySetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize));
        var resize = AccessTools.Method(typeof(NativeWindow), "OnResize", [typeof(ResizeEventArgs)]);
        if (!NotNull(get) || !NotNull(set) || !NotNull(resize)) return;
        if (!NotNull(harmony.Patch(get, new HarmonyMethod(Cached), new HarmonyMethod(Remember)))) return;
        _ = NotNull(harmony.Patch(set, postfix: new HarmonyMethod(Invalidate)));
        _ = NotNull(harmony.Patch(resize, postfix: new HarmonyMethod(Invalidate)));
    }

    private static bool Cached(ref Vector2i __result)
    {
        if (!Enabled || !_valid) return true;
        __result = _size;
        return !(Assert(_size.X >= 0) && Assert(_size.Y >= 0)); // a bad cache hands the call back to OpenTK
    }

    private static void Remember(Vector2i __result)
    {
        if (!Enabled || !Assert(__result.X >= 0) || !Assert(__result.Y >= 0)) return;
        (_size, _valid) = (__result, true);
    }

    private static void Invalidate()
    {
        _valid = false;
    }
}

// SystemRenderSunMoon.OnRenderFrame3DPost polls last frame's sun occlusion query every frame, and each glGet* is a Mesa glthread sync
// (~10 µs). Here the query rests and is read once long complete, two glGets every fourth frame; the sun highlight then updates every
// fourth frame instead of every second, which the engine's smoothing hides.
internal static class SunOcclusion
{
    private const int RestFrames = 3, MaxSamples = 1500;
    private static int _rest;
    private static bool _issued;
    public static bool Enabled { get; set; } = true;

    public static void Install(Harmony harmony)
    {
        (_rest, _issued) = (0, false); // the new world's query name is only reserved until the engine begins it
        var post = AccessTools.Method(typeof(SystemRenderSunMoon), "OnRenderFrame3DPost");
        var prefix = new HarmonyMethod(Prefix);
        if (!NotNull(post) || !Assert(post.GetParameters().Length == 1) ||
            !Assert(Il.Binds(post, prefix.method))) return;
        _ = NotNull(harmony.Patch(post, prefix));
    }

    private static void Prefix(ref bool ___firstTickDone, ref bool ___nowQuerying, ref float ___targetSunSpec,
        int ___occlQueryId)
    {
        if (!Enabled || !Assert(_rest >= 0)) return;
        ___firstTickDone = false; // the engine never polls
        if (_rest > 0) // no new query while the last one matures
        {
            _rest--;
            ___nowQuerying = true;
            return;
        }

        if (_issued && Assert(___occlQueryId != 0))
        {
            GL.GetQueryObject(___occlQueryId, GetQueryObjectParam.QueryResultAvailable, out int ready);
            if (!Assert(ready is 0 or 1)) return;
            if (ready == 0) // reading it now would wait for the GPU, and a new query would run over it
            {
                (___nowQuerying, _rest) = (true, 1);
                return;
            }

            GL.GetQueryObject(___occlQueryId, GetQueryObjectParam.QueryResult, out int samples);
            ___targetSunSpec = GameMath.Clamp(samples / (float)MaxSamples, 0f, 1f);
        }

        // the engine begins and ends a query around this frame's quad
        (___nowQuerying, _issued, _rest) = (false, true, RestFrames);
    }
}
