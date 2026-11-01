using System.Reflection;
using HarmonyLib;
using Komet.Core;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// OpenTK's NativeWindow.ClientSize asks GLFW every read, and on X11 glfwGetWindowSize is a round trip to the X server.
// The engine reads it a dozen times a frame while switching frame buffers and post-processing (5 % of the main thread in
// a 2560x1440 trace). Those frame methods now take the size the engine keeps itself (WindowSize, set in Start and in its
// resize handler, the only places the window changes size). Minimized, the engine keeps its last size (Window_Resize
// never stores 0 x 0), so that window and any other ask GLFW: 0 x 0 viewports rasterize nothing, as in vanilla.
// WindowState is OpenTK's cached field (set by its iconify callback), no round trip. The resize handler, screenshots
// and the mouse code still ask GLFW.
internal static class WindowSize
{
    private const int MaxInstructions = 2048;

    private static readonly (string Name, Type[] Parameters)[] Frames =
    [
        (nameof(ClientPlatformWindows.LoadFrameBuffer), [typeof(EnumFrameBuffer)]),
        (nameof(ClientPlatformWindows.UnloadFrameBuffer), [typeof(EnumFrameBuffer)]),
        (nameof(ClientPlatformWindows.RenderPostprocessingEffects), [typeof(float[])]),
        (nameof(ClientPlatformWindows.RenderFinalComposition), []),
        (nameof(ClientPlatformWindows.BlitPrimaryToDefault), [])
    ];

    // A bit per Frames entry, not a count: another mod's later patch on one of them runs Rewrite again
    private static int _rewritten;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == (1 << Frames.Length) - 1;

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;
        if (!NotNull(harmony)) return;
        for (var i = 0; i < Frames.Length; i++)
            if (AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), Frames[i].Name, Frames[i].Parameters) is { } method)
                _ = harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite));
    }

    // Every ClientSize read becomes Size; a method without one keeps the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var (getter, size) = (AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize)),
            AccessTools.Method(typeof(WindowSize), nameof(Size)));
        if (!Il.Take(instructions, MaxInstructions, out var code) || !NotNull(getter) || !NotNull(size)) return code;
        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
            if (code[i].Calls(getter) && Il.Substitute(code, i, size)) sites++;
        var frame = Array.FindIndex(Frames, f => f.Name == original?.Name);
        if (frame < 0) return code;
        _rewritten &= ~(1 << frame);
        if (sites > 0) _rewritten |= 1 << frame;
        return code;
    }

    internal static Vector2i Size(NativeWindow window)
    {
        if (Enabled && ScreenManager.Platform is ClientPlatformWindows platform && ReferenceEquals(platform.window, window) &&
            window.WindowState != WindowState.Minimized && platform.WindowSize is { Width: > 0, Height: > 0 } kept)
            return new Vector2i(kept.Width, kept.Height);
        return window.ClientSize; // a null window throws as the engine's callvirt did
    }
}
