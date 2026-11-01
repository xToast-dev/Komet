using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using OpenTK.Graphics.OpenGL;

namespace Komet.World;

// The cloud map (essentials' CloudRendererMap) uploads both of its tile textures whenever its thread has a new state,
// up to every 40 ms: at view distance 1536 two 200 x 200 RGBA16 images, converted for Vulkan row by row, 3 % of the main
// thread while a world loads. The second texture holds only the undulating mode, which nearly never changes, and the
// first often comes out the same. Here each upload is skipped when its buffer holds what was last uploaded from it. A
// buffer stands for its texture: InitCloudTiles makes both anew together.
internal static class CloudUploads
{
    private const string Map = "FluffyClouds.CloudRendererMap";
    private const int MaxInstructions = 512, Sites = 2;

    private static readonly ConditionalWeakTable<short[], Last> Sent = [];

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    // Uploads left out, a total (main thread)
    public static long Skipped { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        Sent.Clear();
        if (!NotNull(harmony) || Target() is not { } target) return;
        _ = harmony.Patch(target, transpiler: new HarmonyMethod(Rewrite));
    }

    internal static MethodInfo? Target() =>
        AccessTools.TypeByName(Map) is { } map ? AccessTools.DeclaredMethod(map, "WriteTexture", []) : null;

    // Both TexSubImage2D<short> calls go through Upload; any other shape keeps the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var upload = AccessTools.Method(typeof(CloudUploads), nameof(Upload));
        if (!Il.Take(instructions, MaxInstructions, out var code) || !NotNull(upload)) return code;
        var sites = new List<int>();
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].operand is MethodInfo { Name: nameof(GL.TexSubImage2D), IsGenericMethod: true } m &&
                m.DeclaringType == typeof(GL) && m.GetGenericArguments() is [var t] && t == typeof(short))
                sites.Add(i);
        if (sites.Count != Sites) return code;
        var done = 0;
        foreach (var at in sites.Bounded(Sites)) done += Il.Substitute(code, at, upload) ? 1 : 0; // same call: both or none
        Rewritten = done == Sites;
        return code;
    }

    internal static void Upload(TextureTarget target, int level, int x, int y, int width, int height, PixelFormat format,
        PixelType type, short[] pixels)
    {
        if (Enabled && pixels is not null)
        {
            var last = Sent.GetValue(pixels, static p => new Last(p.Length));
            if (last.Uploaded && pixels.AsSpan().SequenceEqual(last.Data))
            {
                Skipped++;
                return;
            }

            pixels.AsSpan().CopyTo(last.Data);
            last.Uploaded = true;
        }

        GL.TexSubImage2D(target, level, x, y, width, height, format, type, pixels);
    }

    // What a buffer last uploaded; nothing until its first upload (a new texture's contents are undefined)
    private sealed class Last(int length)
    {
        public readonly short[] Data = new short[length];
        public bool Uploaded;
    }
}
