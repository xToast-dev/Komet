using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace Komet.Options;

// The game's settings replaced by Komet's options screen, as Sodium replaces Minecraft's video settings. The escape menu's Settings
// button and Graphics tab both run GuiCompositeSettings.OnGraphicsOptions; in game the prefix loads an empty composer named like the
// graphics tab into the escape menu (so nothing shows behind the screen) and opens the screen over it; its buttons open the game's own
// screens (original graphics tab, macro editor) through their handlers. The escape menu raises "leftGraphicsDlg" when its composer
// stops being the graphics one (another tab, or closed); the screen goes with it. Stands down (the game's tab shows) when the knob is
// off, the bodies are not 1.22.7's, or another mod patches OnGraphicsOptions (its additions would miss the controls they expect).
internal static class GraphicsMenu
{
    internal const ulong Fingerprint = 0x4DF9587AB32B1D45UL; // the six rebuilt tabs, Vintage Story 1.22.7
    private const string DialogName = "gamesettings-graphics";

    // The tabs the screen rebuilds: a game update that changes one leaves the whole menu to the game
    private static readonly string[] Tabs =
        ["OnGraphicsOptions", "OnMouseOptions", "OnAccessibilityOptions", "OnSoundOptions", "OnInterfaceOptions", "OnDeveloperOptions"];

    private static OptionsScreen? _dialog;
    private static MethodInfo? _target;
    private static AccessTools.FieldRef<GuiCompositeSettings, IGameSettingsHandler>? _handler;
    private static AccessTools.FieldRef<GuiCompositeSettings, GuiComposer>? _composer;
    private static string? _owner;
    private static bool _original, _foreign; // _original: the game's own tab is asked for, once
    private static ILogger? _logger;

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }
    public static bool StoodDown => _foreign; // another mod patches OnGraphicsOptions: the game's tab shows

    internal static MethodBase?[] Shaped()
    {
        var methods = Array.ConvertAll(Tabs, tab => (MethodBase?)AccessTools.Method(typeof(GuiCompositeSettings), tab, [typeof(bool)]));
        _ = Assert(methods.Length == Tabs.Length) && NotNull(methods[0]);
        return methods;
    }

    public static void Install(Harmony harmony, ICoreClientAPI capi, OptionsScreen dialog, ILogger? logger)
    {
        (Installed, _dialog, _logger, _foreign) = (false, dialog, logger, false);
        Quieten(harmony);
        var shaped = Shaped();
        if (!NotNull(harmony) || !NotNull(capi) || !NotNull(dialog) || shaped[0] is not MethodInfo target ||
            !EngineShape.Matches(shaped, Fingerprint, nameof(GraphicsMenu), logger)) return;
        (_target, _owner) = (target, harmony.Id);
        _handler = AccessTools.FieldRefAccess<GuiCompositeSettings, IGameSettingsHandler>("handler");
        _composer = AccessTools.FieldRefAccess<GuiCompositeSettings, GuiComposer>("composer");
        _ = NotNull(harmony.Patch(target, new HarmonyMethod(Replace)));
        capi.Event.RegisterEventBusListener(Left, 0.5, "leftGraphicsDlg");
        Installed = true;
    }

    // The game's frame graph and FPS line (HudDebugScreen) wait while the screen is open: the escape menu shows them on its own for
    // as long as a composer named like the graphics tab is loaded (enteredGraphicsDlg), and Alt+F3 may have them on anyway. A prefix
    // that only skips the drawing, whatever the body does, so it needs no fingerprint; the graph keeps recording under it.
    private static void Quieten(Harmony harmony)
    {
        var render = AccessTools.Method(typeof(HudDebugScreen), nameof(HudDebugScreen.OnRenderGUI), [typeof(float)]);
        if (NotNull(harmony) && render is not null) _ = NotNull(harmony.Patch(render, new HarmonyMethod(Quiet)));
    }

    private static bool Quiet() => _dialog is null || !_dialog.IsOpened();

    public static void Clear()
    {
        _dialog?.Leave();
        (_dialog, _target, _handler, _composer, _logger, Installed) = (null, null, null, null, null, false);
    }

    // Harmony binds the instance and the argument by name
    private static bool Replace(GuiCompositeSettings __instance, bool on)
    {
        if (!Enabled || _original || !on || _dialog is null || _handler is null || _composer is null ||
            !NotNull(__instance) || Foreign()) return true;
        var handler = _handler(__instance);
        if (!NotNull(handler) || !handler.IsIngame) return true; // the main menu's settings: Komet is not loaded there anyway
        var empty = handler.GuiComposers?.Create(DialogName + "ingame", ElementBounds.Fixed(0, 0, 1, 1));
        if (!NotNull(empty)) return true;
        _ = empty.Compose();
        _composer(__instance) = empty;
        handler.LoadComposer(empty);
        _dialog.Embed(handler, name => Open(__instance, name));
        return false;
    }

    // One of the game's own screens by handler name: a tab's takes on (OnGraphicsOptions: the original tab), others nothing (OnMacroEditor)
    private static void Open(GuiCompositeSettings settings, string handler)
    {
        var tab = AccessTools.Method(typeof(GuiCompositeSettings), handler, [typeof(bool)]);
        var method = tab ?? AccessTools.Method(typeof(GuiCompositeSettings), handler, Type.EmptyTypes);
        if (!NotNull(settings) || !NotNull(method)) return;
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

    private static void Left(string eventName, ref EnumHandling handling, IAttribute data)
    {
        if (NotNull(eventName) && Assert(eventName == "leftGraphicsDlg")) _dialog?.Leave();
    }

    private static bool Foreign()
    {
        if (!NotNull(_target)) return true;
        var foreign = EngineShape.Foreign([_target], EngineShape.Kinds.All, _owner, typeof(GraphicsMenu));
        _foreign = EngineShape.Report(_logger, nameof(GraphicsMenu), _foreign, foreign);
        return foreign;
    }
}
