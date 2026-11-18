using Komet.Hud;

namespace Komet.Options;

// One tool of the developer screen: a flat entry in its tool list, drawn over the list and detail columns with the
// HUD's widgets in the menu style. The screen recomposes it every RefreshMs while it shows (0: only on input).
internal interface IDevTool
{
    string Id { get; }
    string Title { get; }
    double RefreshMs => 400;

    void Draw(HudUi ui);

    // The tool came on screen or went: collectors it needs run only while it shows
    void Shown(bool shown)
    {
    }

    bool Key(KeyEvent args) => false;

    bool Char(char c) => false;

    // Mouse in canvas pixels, before the screen's click map; true: taken
    bool MouseDown(double x, double y, EnumMouseButton button) => false;

    bool MouseMove(double x, double y) => false;

    void MouseUp()
    {
    }

    bool Wheel(double notches, double x) => false;

    // Per frame; true: the tool changed and redraws
    bool Poll() => false;

    // False: the tool lays out to the view's bottom itself and the screen does not scroll it
    bool Scrolls => true;

    // True: the tool takes the whole screen, without the menu's header, title and footer
    bool FullScreen => false;

    // True while the tool hides the screen to show the game (held)
    bool Peeking => false;

    // A rectangle (canvas pixels) where the screen draws the live game picture over the tool; null: none
    (double X, double Y, double W, double H)? Live => null;

    // The texture drawn in Live: the frame so far (sharp, upright) unless the tool shows something of its own (a render
    // target); called once per frame while Live is set, in the GUI stage with the GL state saved by the caller
    int Picture(int sharp) => sharp;

    // The menu's control painter in the current style, before each draw: the tool's rows look as the menu's
    void Skin(MenuControls controls)
    {
    }
}

// A tool from a draw callback (the HUD's views)
internal sealed class DevTool(string id, string title, Action<HudUi> draw, Action<bool>? onShown = null) : IDevTool
{
    public string Id => id;
    public string Title => title;

    public void Draw(HudUi ui) => draw(ui);

    public void Shown(bool shown) => onShown?.Invoke(shown);
}

// The Shader Studio's way in (Komet.Sdk, a module above this one): it sets the workbench in its Start; if it never
// started, the developer screen says so
internal static class SdkLink
{
    public static System.Func<ICoreClientAPI, Action, IDevTool>? Workbench { get; set; }
}
