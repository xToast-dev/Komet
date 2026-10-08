using System.Runtime.CompilerServices;
using Cairo;
using HarmonyLib;

namespace Komet.Hud;

internal sealed partial class HudOverlay
{
    private const int MaxPrepared = 1024, MaxNested = 64, MaxWarmed = 32;

    // Every character the overlay and the windows print, so the glyphs are in cairo's cache too
    private const string Glyphs =
        "0123456789 abcdefghijklmnopqrstuvwxyzäöüß ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÜ .,:;%()[]/+-–×·Ø~";

    private static readonly Type[] Warmed =
    [
        typeof(HudOverlay), typeof(HudUi), typeof(HudUiFonts), typeof(HudCanvas), typeof(HudMotion), typeof(HudLive),
        typeof(FrameStats), typeof(RenderPassStats), typeof(SpikeLedger), typeof(GpuStats), typeof(ModTimes),
        typeof(HudSettings), typeof(FrameClock), typeof(OptionsScreen), typeof(HudPanel), typeof(HudLine),
        typeof(HudText), typeof(HudWindow), typeof(DebugWindow), typeof(Growth), typeof(Features)
    ];

    private static void Warm(ILogger logger, HudUiFonts fonts)
    {
        if (!NotNull(logger) || !NotNull(fonts)) return;
        _ = Task.Run(() => Prepare(fonts)).ContinueWith(failed => logger.Warning("Komet HUD: warm-up failed ({0})",
                failed.Exception?.GetBaseException().Message), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    // First-call JIT of the HUD's code and loading its fonts cost the frame after F7 60-80 ms; done here first, the cold first interval
    // frame drops from ~46 ms to ~12 ms (fresh process). PrepareMethod compiles on a pool thread: tier 0, no static constructor runs,
    // tiering continues as usual. The nested types are the closures holding the rows' and the dialogs' lambdas.
    private static void Prepare(HudUiFonts fonts)
    {
        WarmFonts(fonts);
        var prepared = 0;
        foreach (var type in Warmed.Bounded(MaxWarmed))
        {
            prepared += PrepareType(type);
            foreach (var nested in AccessTools.InnerTypes(type).Bounded(MaxNested)) prepared += PrepareType(nested);
        }

        _ = Assert(prepared > 0);
    }

    // cairo's font-face and scaled-font caches are process-wide and behind its own locks, and cairo-sharp tracks objects in a concurrent
    // dictionary: a private surface and context on a pool thread load the faces the HUD draws with. The fonts are only read here;
    // CairoFont.SetupContext, which writes the font's options, stays on the main thread.
    private static void WarmFonts(HudUiFonts fonts)
    {
        if (!NotNull(fonts) || !Assert(fonts.Body.UnscaledFontsize > 0)) return;
        using var surface = new ImageSurface(Format.Argb32, 64, 64);
        using var context = new Context(surface);
        using var options = new FontOptions();
        options.Antialias = Antialias.Subpixel; // what SetupContext sets, part of the scaled font's cache key
        context.FontOptions = options;
        foreach (var font in (ReadOnlySpan<CairoFont>)[fonts.Small, fonts.Body, fonts.Strong, fonts.Stat, fonts.Big, fonts.Huge, fonts.Mono])
        {
            context.SelectFontFace(font.Fontname, font.Slant, font.FontWeight);
            context.SetFontSize(scaled(font.UnscaledFontsize));
            _ = context.TextExtents(Glyphs);
            context.MoveTo(0, 32);
            context.ShowText(Glyphs);
        }
    }

    private static int PrepareType(Type type)
    {
        if (!NotNull(type) || type.ContainsGenericParameters) return 0;
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

        return Assert(prepared >= 0) ? prepared : 0;
    }
}
