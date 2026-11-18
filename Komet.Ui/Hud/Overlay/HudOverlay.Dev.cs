using Komet.Options;

namespace Komet.Hud;

// The developer screen (Ctrl+F8, ".komet dev"): the SDK's shader workbench alone, full screen in the menu style, or a
// note where to get it. Every other tool lives in the dockable window (Ctrl+F7).
internal sealed partial class HudOverlay
{
    private IDevTool[]? _devTools;

    private IReadOnlyList<IDevTool> DevTools() =>
        _devTools ??= [SdkLink.Workbench?.Invoke(_capi, PreviewGame) ?? Missing()];

    // Without the kit: what the workbench is and where it comes from
    private static DevTool Missing() =>
        new("ide", HudParts.T("settings-workbench-title"), ui =>
        {
            var y = ui.Y + ui.Px(40);
            foreach (var key in (ReadOnlySpan<string>)["settings-sdk-missing", "settings-sdk-missing-hint"])
            {
                var text = HudParts.T(key);
                ui.Text(ui.X + ui.Px(24), y, ui.F.BodyRow, ui.F.Body, ui.Fit(ui.F.Body, text, ui.W - ui.Px(48)), HudUi.Soft);
                y += ui.F.BodyRow + ui.Px(8);
            }
        });

    // The game behind the IDE: the screen closes, Ctrl+F8 brings it back as it was
    private void PreviewGame()
    {
        _ = !Options.IsOpened() || Options.TryClose();
        _capi.ShowChatMessage(HudParts.T("settings-workbench-preview-return"));
    }

    private void ToggleDeveloper() => _ = Options.Developer ? Options.TryClose() : Options.OpenDeveloper();
}
