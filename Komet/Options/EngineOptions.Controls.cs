using KeyMap = Vintagestory.API.Datastructures.OrderedDictionary<string, Vintagestory.API.Client.HotKey>;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using static Komet.Options.KometPages;

namespace Komet.Options;

// The game's controls tab (GuiCompositeSettings.OnControlOptions and the mouse tab's list of mouse actions, Vintage Story 1.22.7) as
// one page: every hotkey under the heading of its kind, in the game's order, bound like another one in the warning colour with the
// game's "(same as ...)". The screen captures the key (HotkeyCapturer, as the game's tab); Bind stores it as the game's CompletedCapture
// does. Keys take effect at once, as in the game; the switch of the locked modifiers waits for Apply like every other.
internal static partial class EngineOptions
{
    private const int MaxKeys = OptionPage.MaxRows - 16; // the headings, the switch and the buttons besides

    // The game's sortOrder and titles; the second creative kind shares the first's heading
    private static readonly (HotkeyType Type, string Title)[] KeyKinds =
    [
        (HotkeyType.MovementControls, "Movement controls"), (HotkeyType.MouseModifiers, "Mouse click modifiers"),
        (HotkeyType.CharacterControls, "Actions"), (HotkeyType.HelpAndOverlays, "In-game Help and Overlays"),
        (HotkeyType.GUIOrOtherControls, "User interface & More"), (HotkeyType.InventoryHotkeys, "Inventory hotkeys"),
        (HotkeyType.CreativeOrSpectatorTool, "Creative mode"), (HotkeyType.CreativeTool, ""), (HotkeyType.DevTool, "Debug and Macros"),
        (HotkeyType.MouseControls, "mouseactions")
    ];

    // The hotkeys a change of which the game announces (its ShiftOrCtrlChanged)
    private static readonly string[] Announced = ["shift", "ctrl", "primarymouse", "secondarymouse", "toolmodeselect"];

    // The game's hotkeys; none without a game running (the tests)
    private static KeyMap? HotKeys => ScreenManager.hotkeyManager?.HotKeys;

    private static Dictionary<string, string>? _clashes; // code -> the name of another key bound the same; rebuilt after each change
    private static bool _armed; // the reset asked once and waits for its second click

    private static OptionPage? Controls(string section, Action<string>? tab)
    {
        if (HotKeys is not { } keys || !NotNull(section) || !Assert(section.Length > 0)) return null;
        (_clashes, _armed) = (null, false);
        var page = new OptionPage("vs-controls", Lang.Get("setting-controls-header"), section) { Capacity = OptionPage.MaxRows }
            .Switch(Name("noseparatectrlkeys"), () => !ClientSettings.SeparateCtrl, Locked, Hover("noseparatectrlkeys"));
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
                if (modifiers) _ = page.EnabledWhen(() => ClientSettings.SeparateCtrl); // locked to sneak and sprint otherwise
            }
        }

        _ = page.Group(T("group-more"))
            .Button(Lang.Get("setting-name-setdefault"), () => _armed ? Lang.Get("Confirm") : T("reset"), ResetKeys,
                Lang.Get("Really reset key controls to default settings?") + "\n\n" + T("reset-hint"));
        return tab is null ? page : page.Button(Lang.Get("setting-name-macroeditor"), T("open"), () => tab("OnMacroEditor"));
    }

    // What a key is bound to, with the other one bound the same
    private static string Mapping(string code)
    {
        if (!NotNull(code) || HotKeys is not { } keys || !keys.TryGetValue(code, out var key)) return "";
        var clash = Clash(code);
        var shown = key.CurrentMapping?.ToString() ?? "?";
        return clash.Length > 0 ? shown + Lang.Get("keybind-conflict-sameas", clash) : shown;
    }

    internal static string Clash(string code)
    {
        _clashes ??= Clashes();
        if (!NotNull(code) || !Assert(_clashes.Count <= OptionPage.MaxRows)) return "";
        return _clashes.TryGetValue(code, out var other) ? other : "";
    }

    // The game's check: keys of the listed kinds bound alike, except a mouse modifier beside sneak or sprint (the same key by design).
    // The game compares the shown text; the parts it is made of say the same without naming every key
    private static Dictionary<string, string> Clashes()
    {
        Dictionary<string, string> clashes = [];
        if (HotKeys is not { } keys) return clashes;
        Dictionary<(int, int?, bool, bool, bool), HotKey> seen = [];
        foreach (var key in keys.Values.Take(OptionPage.MaxRows).Bounded(OptionPage.MaxRows))
        {
            var type = key.KeyCombinationType;
            if (type == HotkeyType.MouseControls || (type == HotkeyType.MouseModifiers && !ClientSettings.SeparateCtrl)) continue;
            var shown = key.CurrentMapping is { } m ? (m.KeyCode, m.SecondKeyCode, m.Ctrl, m.Alt, m.Shift) : (-1, null, false, false, false);
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
        if (!NotNull(a) || !NotNull(b)) return false;
        var (modifier, other) = a.KeyCombinationType == HotkeyType.MouseModifiers ? (a, b) : (b, a);
        return modifier.KeyCombinationType == HotkeyType.MouseModifiers && other.Code is "sneak" or "sprint";
    }

    // CompletedCapture: the key bound and saved, shift and ctrl following sneak and sprint while the modifiers are locked to them
    internal static void Bind(string code, KeyCombination? combination)
    {
        if (HotKeys is not { } keys || combination is null || !NotNull(code) || !keys.TryGetValue(code, out var key)) return;
        var bound = key.Clone();
        bound.CurrentMapping = combination;
        _ = Assert(bound.Code == code);
        keys[code] = bound;
        ClientSettings.Inst.SetKeyMapping(code, combination);
        var follower = ClientSettings.SeparateCtrl ? null : code switch { "sneak" => "shift", "sprint" => "ctrl", _ => null };
        if (follower is not null && keys.TryGetValue(follower, out var follows)) follows.CurrentMapping = combination;
        if (follower is not null || Array.IndexOf(Announced, code) >= 0) (_capi?.Event as ClientEventAPI)?.TriggerHotkeysChanged();
        _clashes = null;
    }

    // A right click on a key: the game's default for it
    internal static void Unbind(string code)
    {
        if (HotKeys is { } keys && NotNull(code) && keys.TryGetValue(code, out var key) && key.DefaultMapping is { } fallback)
            Bind(code, fallback.Clone());
    }

    // OnConfirmReset, on the second click: every key back to the game's default
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

    // onSeparateCtrl: locked, shift and ctrl take sneak's and sprint's keys; unlocked, their own again
    private static void Locked(bool locked)
    {
        if (HotKeys is not { } keys) return;
        ClientSettings.SeparateCtrl = !locked;
        foreach (var (modifier, leader, own) in new[] { ("shift", "sneak", 1), ("ctrl", "sprint", 3) }.Bounded(2))
        {
            if (!keys.TryGetValue(modifier, out var key)) continue;
            key.CurrentMapping = locked && keys.TryGetValue(leader, out var lead) ? lead.CurrentMapping : new KeyCombination { KeyCode = own };
            ClientSettings.Inst.SetKeyMapping(modifier, key.CurrentMapping);
        }

        _clashes = null;
    }
}
