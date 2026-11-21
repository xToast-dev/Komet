using System.Reflection;
using HarmonyLib;
using Komet.Api;
using Komet.Core;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace Komet.Options;

// Escape menu Settings and Graphics tab run OnGraphicsOptions; the prefix loads an empty composer named like the
// graphics tab, so "leftGraphicsDlg" fires once it changes. Stands down when another mod patches OnGraphicsOptions.
internal static class GraphicsMenu
{
    internal const ulong Fingerprint = 0x4DF9587AB32B1D45UL; // the six rebuilt tabs, Vintage Story 1.22.7
    internal const ulong Fingerprint123 = 0x218720CF8CB78240UL; // 1.23.0-pre.1
    private const string DialogName = "gamesettings-graphics";

    // A game update changing one leaves the whole menu to the game
    private static readonly string[] Tabs =
    [
        "OnGraphicsOptions", "OnMouseOptions", "OnAccessibilityOptions", "OnSoundOptions", "OnInterfaceOptions",
        "OnDeveloperOptions"
    ];

    private static OptionsScreen? _dialog;
    private static MethodInfo? _target;
    private static AccessTools.FieldRef<GuiCompositeSettings, IGameSettingsHandler>? _handler;
    private static AccessTools.FieldRef<GuiCompositeSettings, GuiComposer>? _composer;
    private static string? _owner;
    private static bool _original; // _original: the game's own tab is asked for, once
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static bool StoodDown { get; private set; }

    internal static MethodBase?[] Shaped() =>
        Array.ConvertAll(Tabs,
            tab => (MethodBase?)AccessTools.Method(typeof(GuiCompositeSettings), tab, [typeof(bool)]));

    // The patch only swaps a composer: a 1.23 point release rebuilding a tab body must not hand the whole menu back
    // to the game. Recognize that engine by the DPI scale its interface tab uses, never by a version string, and
    // insist on every seam and field we use before falling back.
    internal static bool Structured(MethodBase?[] shaped) =>
        shaped.Length == Tabs.Length && Array.TrueForAll(shaped, static tab => tab is not null) &&
        AccessTools.Field(typeof(GuiCompositeSettings), "handler") is not null &&
        AccessTools.Field(typeof(GuiCompositeSettings), "composer") is not null &&
        AccessTools.Method(typeof(GuiCompositeSettings), "OpenSettingsMenu") is not null;

    internal static bool PointRelease123(MethodBase?[] shaped) =>
        AccessTools.Field(typeof(RuntimeEnv), "DPIHScale") is not null && Structured(shaped);

    public static void Install(Harmony harmony, ICoreClientAPI capi, OptionsScreen dialog, ILogger? logger)
    {
        (Installed, _dialog, _logger, StoodDown) = (false, dialog, logger, false);
        Quieten(harmony);
        EngineOptions.NameUnbound(harmony);
        var shaped = Shaped();
        if (!NotNull(dialog) || shaped[0] is not MethodInfo target ||
            (!EngineShape.Matches(shaped, Fingerprint, Fingerprint123, nameof(GraphicsMenu), logger) &&
             !PointRelease123(shaped)))
        {
            return;
        }

        (_target, _owner) = (target, harmony.Id);
        _handler = AccessTools.FieldRefAccess<GuiCompositeSettings, IGameSettingsHandler>("handler");
        _composer = AccessTools.FieldRefAccess<GuiCompositeSettings, GuiComposer>("composer");
        _ = harmony.Patch(target, new HarmonyMethod(Replace));
        capi.Event.RegisterEventBusListener(Left, 0.5, "leftGraphicsDlg");
        Installed = true;
    }

    // HudDebugScreen's graph and FPS line hide while open (the menu shows them for the graphics composer). Drawing
    // only: no fingerprint needed; the graph keeps recording.
    private static void Quieten(Harmony harmony)
    {
        var render = AccessTools.Method(typeof(HudDebugScreen), nameof(HudDebugScreen.OnRenderGUI), [typeof(float)]);
        if (render is not null) _ = harmony.Patch(render, new HarmonyMethod(Quiet));
    }

    private static bool Quiet() => _dialog is null || !_dialog.IsOpened();

    public static void Clear()
    {
        _dialog?.Leave();
        (_dialog, _target, _handler, _composer, _logger, Installed) = (null, null, null, null, null, false);
        EngineOptions.Forget();
    }

    private static bool Replace(GuiCompositeSettings __instance, bool on)
    {
        if (!Enabled || _original || !on || _dialog is null || _handler is null || _composer is null ||
            !NotNull(__instance) || Foreign())
        {
            return true;
        }

        var handler = _handler(__instance);
        if (!NotNull(handler) || !handler.IsIngame)
        {
            return true; // the main menu's settings: Komet is not loaded there anyway
        }

        var empty = handler.GuiComposers?.Create(DialogName + "ingame", ElementBounds.Fixed(0, 0, 1, 1));
        if (!NotNull(empty)) return true;

        _ = empty.Compose();
        _composer(__instance) = empty;
        handler.LoadComposer(empty);
        _dialog.Embed(handler, name => Open(__instance, name));
        return false;
    }

    private static void Open(GuiCompositeSettings settings, string handler)
    {
        var tab = AccessTools.Method(typeof(GuiCompositeSettings), handler, [typeof(bool)]);
        var method = tab ?? AccessTools.Method(typeof(GuiCompositeSettings), handler, Type.EmptyTypes);
        if (!NotNull(method)) return;

        _original = true;
        try
        {
            _ = method.Invoke(settings, tab is null ? [] : [true]);
        }
        finally
        {
            _original = false;
        }
    }

    private static void Left(string eventName, ref EnumHandling handling, IAttribute data) => _dialog?.Leave();

    private static bool Foreign()
    {
        if (!NotNull(_target)) return true;

        var foreign = EngineShape.Foreign([_target], KapiPatchKinds.All, _owner, typeof(GraphicsMenu));
        StoodDown = EngineShape.Report(_logger, nameof(GraphicsMenu), StoodDown, foreign, [_target], KapiPatchKinds.All,
            _owner,
            [typeof(GraphicsMenu)]);
        return foreign;
    }
}
