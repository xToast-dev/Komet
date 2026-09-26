using HarmonyLib;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

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

        (___nowQuerying, _issued, _rest) =
            (false, true, RestFrames); // the engine begins and ends a query around this frame's quad
    }
}
