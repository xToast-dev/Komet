namespace Komet.Hud;

// One view of the dockable window (K): a tab of its bar or a page behind the gear and the badges. It draws
// into the window's scrolling body; the window composes only on change (input, new numbers, Live).
internal interface IHudTab
{
    // The saved tab (HudSettings.WindowTab) and the title's lang key, "hud-tab-" + Id
    string Id { get; }

    // Protocol and inspector: the window grows for long lines
    bool Wide => false;

    // Collectors that run only while this tab shows
    HudNeeds Needs => HudNeeds.None;

    void Draw(HudUi ui, HudBody body);

    // The window opened on it or switched to it
    void Shown()
    {
    }

    // Per frame while shown: true when the tab must redraw now (a running capture, the live inspector)
    bool Live() => false;

    // Keys and typing (the inspector's search); true: taken
    bool Key(KeyEvent args, HudBody body) => false;

    bool Char(char c, HudBody body) => false;
}

// The window's scrolling body as the shown tab sees it: its scroll offset, which jumps and filters move
internal sealed class HudBody(Func<double> get, Action<double> set)
{
    public double Scroll
    {
        get => get();
        set => set(value);
    }
}
