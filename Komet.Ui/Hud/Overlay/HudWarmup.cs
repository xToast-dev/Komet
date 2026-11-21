using System.Runtime.CompilerServices;
using HarmonyLib;
using Komet.Core;
using Komet.Diagnostics;
using Komet.Options;
using Komet.Ui.Hud;
using SkiaSharp;

namespace Komet.Hud;

// The HUD's code and fonts warmed on a pool thread at start: the first frame after F7 waits no JIT or font load
internal static class HudWarmup
{
    private const int MaxPrepared = 1024, MaxNested = 64, MaxWarmed = 32;

    private const string Glyphs =
        "0123456789 abcdefghijklmnopqrstuvwxyzäöüß ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÜ .,:;%()[]/+-–×·Ø~";

    private static readonly Type[] Warmed =
    [
        typeof(HudOverlay), typeof(HudSampler), typeof(HudPanels), typeof(OverlayCard), typeof(HudWindow),
        typeof(HudUi), typeof(HudUiFonts), typeof(HudCanvas), typeof(HudMotion), typeof(HudLive), typeof(HudParts),
        typeof(FrameStats), typeof(RenderPassStats), typeof(SpikeLedger), typeof(GpuStats), typeof(ModTimes),
        typeof(HudSettings), typeof(FrameClock), typeof(OptionsScreen), typeof(HudPanel), typeof(HudLine),
        typeof(KometText), typeof(OverviewTab), typeof(ProtocolTab), typeof(Growth), typeof(Features)
    ];

    public static void Warm(ILogger logger, HudUiFonts fonts) =>
        _ = Task.Run(() => Prepare(fonts)).ContinueWith(failed => logger.Warning("Komet HUD: warm-up failed ({0})",
                failed.Exception?.GetBaseException().Message), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    // First-call JIT and font loading cost the frame after F7 60-80 ms; warmed, the cold first frame drops ~46 -> ~12
    // ms.
    private static void Prepare(HudUiFonts fonts)
    {
        WarmFonts(fonts);
        foreach (var type in Warmed.Bounded(MaxWarmed))
        {
            _ = PrepareType(type);
            foreach (var nested in AccessTools.InnerTypes(type).Bounded(MaxNested))
            {
                _ = PrepareType(nested);
            }
        }
    }

    // Skia's font caches are process-wide: a private canvas on a pool thread loads the faces and shapes the glyphs
    // before the first HUD frame asks for them.
    private static void WarmFonts(HudUiFonts fonts)
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        using var font = new SKFont { Edging = SKFontEdging.SubpixelAntialias, Subpixel = true };
        foreach (var face in (ReadOnlySpan<CairoFont>)
                 [fonts.Small, fonts.Body, fonts.Strong, fonts.Stat, fonts.Big, fonts.Huge, fonts.Mono])
        {
            SkiaFonts.Setup(font, face, scaled(face.UnscaledFontsize));
            _ = font.MeasureText(Glyphs);
            canvas.DrawText(Glyphs, 0, 32, font, paint);
        }
    }

    private static int PrepareType(Type type)
    {
        if (type.ContainsGenericParameters) return 0;

        var prepared = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(type).Bounded(MaxPrepared))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() is null) continue;

            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                prepared++;
            }
            catch (Exception e) when (e is ArgumentException or TypeLoadException or BadImageFormatException
                                          or MissingMemberException)
            {
                // the first call compiles it as before
            }
        }

        return prepared;
    }
}
