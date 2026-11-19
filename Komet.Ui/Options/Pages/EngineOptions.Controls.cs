using HarmonyLib;
using Komet.Core;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Options.OptionsText;
using KeyMap = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;

namespace Komet.Options;

// OnControlOptions and the mouse tab (1.22.7) as one page. Keys apply at once; the locked-modifiers switch waits for
// Apply.
internal static partial class EngineOptions
{
    private const int MaxKeys = OptionPage.MaxRows - 16; // the headings, the switch and the buttons besides

    // An unbound key: no key or mouse button has this code, yet the engine indexes its 512-entry key states with it
    // (SystemPlayerControl, UpdateFreeMouse, IsHotKeyPressed), where -1 would throw
    internal const int NoKey = 511;

    private static readonly (HotkeyType Type, string Title)[] KeyKinds =
    [
        (HotkeyType.MovementControls, "Movement controls"), (HotkeyType.MouseModifiers, "Mouse click modifiers"),
        (HotkeyType.CharacterControls, "Actions"), (HotkeyType.HelpAndOverlays, "In-game Help and Overlays"),
        (HotkeyType.GUIOrOtherControls, "User interface & More"), (HotkeyType.InventoryHotkeys, "Inventory hotkeys"),
        (HotkeyType.CreativeOrSpectatorTool, "Creative mode"), (HotkeyType.CreativeTool, ""),
        (HotkeyType.DevTool, "Debug and Macros"),
        (HotkeyType.MouseControls, "mouseactions")
    ];

    private static readonly string[] Announced = ["shift", "ctrl", "primarymouse", "secondarymouse", "toolmodeselect"];

    private static Dictionary<string, string>?
        _clashes; // code -> the name of another key bound the same; rebuilt after each change

    private static bool _armed; // the reset asked once and waits for its second click

    private static KeyMap? HotKeys => ScreenManager.hotkeyManager?.HotKeys; // null without a running game (tests)

    private static OptionPage? Controls(string section, Action<string>? tab)
    {
        if (HotKeys is not { } keys) return null;

        (_clashes, _armed) = (null, false);
        var page = new OptionPage("vs-controls", Lang.Get("setting-controls-header"), section)
                { Capacity = OptionPage.MaxRows }
            .Setting("noseparatectrlkeys", () => !ClientSettings.SeparateCtrl, Locked);
        var hint = T("key-hint");
        foreach (var (type, title) in KeyKinds.Bounded(KeyKinds.Length))
        {
            var modifiers = type == HotkeyType.MouseModifiers;
            var kind = keys.Values.Where(k => k.KeyCombinationType == type).Take(MaxKeys).ToArray();
            if (kind.Length == 0) continue;

            if (title.Length > 0) _ = page.Group(Lang.Get(title));

            foreach (var key in kind.Bounded(MaxKeys))
            {
                var code = key.Code;
                _ = page.Key(key.Name ?? code, code, () => Mapping(code), () => Clash(code).Length > 0, hint);
                if (modifiers)
                {
                    _ = page.EnabledWhen(() => ClientSettings.SeparateCtrl); // locked to sneak and sprint otherwise
                }
            }
        }

        _ = page.Group(T("group-more"))
            .Button(Lang.Get("setting-name-setdefault"), () => _armed ? Lang.Get("Confirm") : T("reset"), ResetKeys,
                Lang.Get("Really reset key controls to default settings?") + "\n\n" + T("reset-hint"));
        return tab is null
            ? page
            : page.Button(Lang.Get("setting-name-macroeditor"), T("open"), () => tab("OnMacroEditor"));
    }

    private static string Mapping(string code)
    {
        if (!NotNull(code) || HotKeys is not { } keys || !keys.TryGetValue(code, out var key)) return "";

        if (key.CurrentMapping?.KeyCode == NoKey) return T("key-none");

        var (clash, shown) = (Clash(code), key.CurrentMapping?.ToString() ?? "?");
        return clash.Length > 0 ? shown + Lang.Get("keybind-conflict-sameas", clash) : shown;
    }

    internal static string Clash(string code)
    {
        _clashes ??= Clashes();
        if (!NotNull(code)) return "";

        return _clashes.GetValueOrDefault(code, "");
    }

    // The game's clash check (a mouse modifier beside sneak/sprint is by design), comparing parts, not shown text.
    private static Dictionary<string, string> Clashes()
    {
        Dictionary<string, string> clashes = [];
        if (HotKeys is not { } keys) return clashes;

        Dictionary<(int, int?, bool, bool, bool), HotKey> seen = [];
        foreach (var key in keys.Values.Take(OptionPage.MaxRows).Bounded(OptionPage.MaxRows))
        {
            var type = key.KeyCombinationType;
            if (type == HotkeyType.MouseControls ||
                (type == HotkeyType.MouseModifiers && !ClientSettings.SeparateCtrl) ||
                key.CurrentMapping?.KeyCode == NoKey)
            {
                continue; // unbound keys clash with nothing
            }

            var shown = key.CurrentMapping is { } m
                ? (m.KeyCode, m.SecondKeyCode, m.Ctrl, m.Alt, m.Shift)
                : (-1, null, false, false, false);
            if (seen.TryGetValue(shown, out var other) && !Paired(key, other))
            {
                clashes[key.Code] = other.Name;
                _ = clashes.TryAdd(other.Code, key.Name);
            }

            seen[shown] = key;
        }

        return clashes;
    }

    private static bool Paired(HotKey a, HotKey b)
    {
        var (modifier, other) = a.KeyCombinationType == HotkeyType.MouseModifiers ? (a, b) : (b, a);
        return modifier.KeyCombinationType == HotkeyType.MouseModifiers && other.Code is "sneak" or "sprint";
    }

    // The game's CompletedCapture: shift and ctrl follow sneak and sprint while locked
    internal static void Bind(string code, KeyCombination? combination)
    {
        if (HotKeys is not { } keys || combination is null || !NotNull(code) ||
            !keys.TryGetValue(code, out var key))
        {
            return;
        }

        var bound = key.Clone();
        bound.CurrentMapping = combination;
        keys[code] = bound;
        ClientSettings.Inst.SetKeyMapping(code, combination);
        var follower = ClientSettings.SeparateCtrl
            ? null
            : code switch { "sneak" => "shift", "sprint" => "ctrl", _ => null };
        if (follower is not null && keys.TryGetValue(follower, out var follows)) follows.CurrentMapping = combination;

        if (follower is not null || Array.IndexOf(Announced, code) >= 0)
        {
            (_capi?.Event as ClientEventAPI)?.TriggerHotkeysChanged();
        }

        _clashes = null;
    }

    // No key at all: the key does nothing until bound again
    internal static void Unbind(string code)
    {
        if (NotNull(code) && Assert(code.Length > 0)) Bind(code, new KeyCombination { KeyCode = NoKey });
    }

    internal static bool Unbound(string code) =>
        NotNull(code) && Assert(code.Length > 0) && HotKeys is { } keys && keys.TryGetValue(code, out var key) &&
        key.CurrentMapping?.KeyCode == NoKey;

    // The game's own texts (interaction help, its controls tab) name an unbound key as such, not as key 511
    internal static void NameUnbound(Harmony harmony)
    {
        var name = AccessTools.DeclaredMethod(typeof(KeyCombination), nameof(KeyCombination.ToString), []);
        if (NotNull(name))
        {
            _ = harmony.Patch(name, new HarmonyMethod(AccessTools.Method(typeof(EngineOptions), nameof(NamedUnbound))));
        }
    }

    // Prefix: the game's own naming runs for every bound key
    private static bool NamedUnbound(KeyCombination __instance, ref string __result)
    {
        if (!NotNull(__instance) || __instance.KeyCode != NoKey) return true;

        __result = T("key-none");
        return false;
    }

    // The game's default binding back
    internal static void Defaulted(string code)
    {
        if (!Assert(code is { Length: > 0 })) return;

        if (HotKeys is { } keys && keys.TryGetValue(code, out var key) && key.DefaultMapping is { } fallback)
        {
            Bind(code, fallback.Clone());
        }
    }

    // The game's defaults as the rows read them: a fresh ClientSettings filled by LoadDefaultValues stands in for the
    // player's while the getters run (its static setters write to Inst, so it must be Inst while it fills); the player's
    // is back before anything else reads it. NaN where a getter fails.
    internal static double[] Defaults(IReadOnlyList<OptionRow> rows)
    {
        var values = new double[Math.Min(rows.Count, OptionPage.MaxRows)];
        Array.Fill(values, double.NaN);
        var live = ClientSettings.Inst;
        if (!NotNull(live) ||
            Activator.CreateInstance(typeof(ClientSettings), true) is not ClientSettings fresh)
        {
            return values;
        }

        try
        {
            ClientSettings.Inst = fresh;
            fresh.LoadDefaultValues();
            for (var i = 0; i < Math.Min(values.Length, OptionPage.MaxRows); i++)
            {
                values[i] = Read(rows[i]);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _capi?.Logger?.Warning("Komet options: the game's defaults could not be read: {0}", e.Message);
        }
        finally
        {
            ClientSettings.Inst = live;
        }

        return values;
    }

    private static double Read(OptionRow row)
    {
        if (!NotNull(row) || row.Kind is OptionKind.Group or OptionKind.Button or OptionKind.Key) return double.NaN;

        try
        {
            return row.Get();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return double.NaN;
        }
    }

    private static void ResetKeys()
    {
        var keys = HotKeys;
        if (!_armed || keys is null)
        {
            _armed = keys is not null;
            return;
        }

        ClientSettings.KeyMapping.Clear();
        foreach (var key in keys.Values.Take(OptionPage.MaxRows).Bounded(OptionPage.MaxRows))
        {
            key.CurrentMapping = key.DefaultMapping?.Clone();
            ClientSettings.Inst.SetKeyMapping(key.Code, key.CurrentMapping);
        }

        (_armed, _clashes) = (false, null);
        (_capi?.Event as ClientEventAPI)?.TriggerHotkeysChanged();
    }

    private static void Locked(bool locked)
    {
        if (HotKeys is not { } keys) return;

        ClientSettings.SeparateCtrl = !locked;
        foreach (var (modifier, leader, own) in new[] { ("shift", "sneak", 1), ("ctrl", "sprint", 3) }.Bounded(2))
        {
            if (!keys.TryGetValue(modifier, out var key)) continue;

            key.CurrentMapping = locked && keys.TryGetValue(leader, out var lead)
                ? lead.CurrentMapping
                : new KeyCombination { KeyCode = own };
            ClientSettings.Inst.SetKeyMapping(modifier, key.CurrentMapping);
        }

        _clashes = null;
    }
}
