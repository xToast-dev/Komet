using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Where the Komet SDK (mod kometsdk, the Shader Workbench) joins Komet's shader loading. Komet calls these when set and
// does nothing extra when the kit is absent, so a player's Komet carries no workbench code. The kit sets them in its
// Start, before any StartClientSide, and clears them when its world ends. Main thread.
internal static class StudioHooks
{
    // Before the registry reads its files: the active projects' copies go in
    public static Action<ICoreClientAPI>? Loading { get; set; }

    // After a reload: true when a broken copy gave way and the registry must load once more
    public static System.Func<ShaderMods.Diagnostic[], bool>? Validate { get; set; }

    // Every ShaderProgramBase.Use: the kit's per-frame program use count
    public static Action<ShaderProgramBase>? Used { get; set; }
}
