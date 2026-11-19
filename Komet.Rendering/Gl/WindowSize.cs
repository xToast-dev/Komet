using System.Reflection;
using HarmonyLib;
using Komet.Core;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.MathTools;
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

    // 1.23 keeps the framebuffer size in the platform and reads that cache in the frame methods
    private static readonly AccessTools.FieldRef<ClientPlatformWindows, Size2i>? CachedFramebuffer =
        AccessTools.Field(typeof(ClientPlatformWindows), "framebuffersize")?.FieldType == typeof(Size2i)
            ? AccessTools.FieldRefAccess<ClientPlatformWindows, Size2i>("framebuffersize")
            : null;

    // A bit per Frames entry, not a count: another mod's later patch on one of them runs Rewrite again
    private static int _rewritten;

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten => _rewritten == (1 << Frames.Length) - 1;

    public static void Install(Harmony harmony)
    {
        _rewritten = 0;

        for (var i = 0; i < Frames.Length; i++)
        {
            if (AccessTools.DeclaredMethod(typeof(ClientPlatformWindows), Frames[i].Name, Frames[i].Parameters) is
                { } method)
            {
                _ = harmony.Patch(method, transpiler: new HarmonyMethod(Rewrite));
            }
        }
    }

    // Every size read becomes the engine's cached size; a method without one keeps the engine's IL
    private static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions, MethodBase? original)
    {
        var (client, framebuffer, size, frameSize) = (
            AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.ClientSize)),
            AccessTools.PropertyGetter(typeof(NativeWindow), nameof(NativeWindow.FramebufferSize)),
            AccessTools.Method(typeof(WindowSize), nameof(Size)),
            AccessTools.Method(typeof(WindowSize), nameof(FrameSize)));
        if (!Il.Take(instructions, MaxInstructions, out var code) || !NotNull(size) || !NotNull(frameSize)) return code;

        var sites = 0;
        for (var i = 0; i < Math.Min(code.Count, MaxInstructions); i++)
        {
            if (NotNull(client) && code[i].Calls(client) && Il.Substitute(code, i, size)) sites++;
            else if (NotNull(framebuffer) && code[i].Calls(framebuffer) && Il.Substitute(code, i, frameSize)) sites++;
        }

        var frame = Array.FindIndex(Frames, f => f.Name == original?.Name);
        if (frame < 0) return code;

        _rewritten &= ~(1 << frame);
        if (sites > 0) _rewritten |= 1 << frame;

        return code;
    }

    internal static Vector2i Size(NativeWindow window)
    {
        if (Enabled && ScreenManager.Platform is ClientPlatformWindows platform &&
            ReferenceEquals(platform.window, window) &&
            window.WindowState != WindowState.Minimized && platform.WindowSize is { Width: > 0, Height: > 0 } kept)
        {
            return new Vector2i(kept.Width, kept.Height);
        }

        return window.ClientSize; // a null window throws as the engine's callvirt did
    }

    internal static Vector2i FrameSize(NativeWindow window)
    {
        if (Enabled && CachedFramebuffer is not null && ScreenManager.Platform is ClientPlatformWindows platform &&
            ReferenceEquals(platform.window, window) && CachedFramebuffer(platform) is { Width: > 0, Height: > 0 } kept)
        {
            return new Vector2i(kept.Width, kept.Height);
        }

        return window.FramebufferSize;
    }
}
