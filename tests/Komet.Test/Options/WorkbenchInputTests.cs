using Komet.Core;
using Komet.Hud;
using Komet.Options;

namespace Komet.Test.Options;

public sealed class WorkbenchInputTests
{
    [Test]
    public void TheWorkbenchTakesRawMouseButtonsBeforeTheyBecomeGameHotkeys()
    {
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.KeyboardState = new bool[512];
        game.PreviousKeyboardState = new bool[512];
        game.MouseStateRaw = new MouseButtonState();
        game.InWorldMouseState = new MouseButtonState();
        var manager = (GuiManager)RuntimeHelpers.GetUninitializedObject(typeof(GuiManager));
        AccessTools.DeclaredField(typeof(ClientSystem), "game").SetValue(manager, game);
        game.clientSystems = [manager];

        // Wire up just the input state so the real client dispatch can run without Cairo or a window.
        var tool = new Tool();
        var screen = (OptionsScreen)RuntimeHelpers.GetUninitializedObject(typeof(OptionsScreen));
        AccessTools.DeclaredField(typeof(OptionsScreen), "_developer").SetValue(screen, true);
        AccessTools.DeclaredField(typeof(OptionsScreen), "_canvas")
            .SetValue(screen, RuntimeHelpers.GetUninitializedObject(typeof(HudCanvas)));
        AccessTools.DeclaredField(typeof(OptionsScreen), "_query").SetValue(screen, "");
        AccessTools.DeclaredField(typeof(OptionsScreen), "_page").SetValue(screen, "dev-ide");
        AccessTools.DeclaredField(typeof(OptionsScreen), "_pages")
            .SetValue(screen, new[] { new OptionPage("dev-ide", "Workbench", "Developer") { Tool = tool } });
        var isOpen = AccessTools.DeclaredField(typeof(GuiDialog), "opened");
        isOpen.SetValue(screen, true);
        AccessTools.DeclaredField(typeof(ClientMain), "LoadedGuis").SetValue(game, new List<GuiDialog> { screen });
        AccessTools.DeclaredField(typeof(ClientMain), "OpenedGuis").SetValue(game, new List<GuiDialog> { screen });

        var down = new MouseEvent(20, 30, EnumMouseButton.Left, 0);
        var up = new MouseEvent(20, 30, EnumMouseButton.Left, 0);
        game.OnMouseDownRaw(down);
        game.OnMouseUpRaw(up);
        screen.UnFocus(); // another dialog can request focus while the workbench is visible
        var keyUp = new KeyEvent { KeyCode = (int)GlKeys.W };
        manager.OnKeyUp(keyUp);

        Assert.Multiple(() =>
        {
            Assert.That(screen.DisableMouseGrab, Is.True);
            Assert.That(game.InWorldMouseState.Left, Is.False);
            Assert.That((tool.Presses, tool.Releases), Is.EqualTo((1, 1)));
            Assert.That(down.Handled && up.Handled, Is.True);
            Assert.That(keyUp.Handled, Is.True);
        });

        isOpen.SetValue(screen, false);
        Assert.That(manager.CaptureRawMouse(), Is.False);
    }

    private sealed class Tool : IDevTool
    {
        public string Id => "ide";
        public string Title => "Workbench";
        public int Presses { get; private set; }
        public int Releases { get; private set; }

        public void Draw(HudUi ui) { }

        public bool MouseDown(double x, double y, EnumMouseButton button)
        {
            Presses++;
            return true;
        }

        public void MouseUp() => Releases++;
    }
}
