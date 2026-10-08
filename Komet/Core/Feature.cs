using System.Reflection;
using HarmonyLib;

namespace Komet.Core;

// Main: from ModTimes to FrameClock; Tail: the bench, after them; Last: PreJit, once the HUD renders and every patch is in place
internal enum FeatureStage
{
    Main,
    Tail,
    Last
}

// Engine bodies a feature reproduces, skips or replays, and their fingerprint in the game version it was written against
internal readonly record struct EngineBodies(Func<MethodBase?[]> Methods, ulong Expected);

// Overlay: the HUD's, set by the Hud feature for those after it (GraphicsMenu, the renderers).
internal sealed class FeatureContext(Harmony harmony, ICoreClientAPI api, ILogger logger, bool localServer)
{
    public Harmony Harmony { get; } = harmony;
    public ICoreClientAPI Api { get; } = api;
    public ILogger Logger { get; } = logger;
    public bool LocalServer { get; } = localServer;
    public HudOverlay? Overlay { get; set; }
}

// Install null: nothing to patch (the knob alone is the feature). Stop runs before UnpatchAll in reverse install order, Unpatched
// after it. Recheck: LevelFinalize, when every mod has patched. Probe: why the feature is not doing its job (Active when it is).
// Requires is declarative: a test checks each one comes earlier in the table.
internal sealed record Feature(string Id)
{
    public Action<FeatureContext>? Install { get; init; }
    public FeatureStage Stage { get; init; }
    public bool ServerOnly { get; init; } // installed only when the server runs in this process
    public string[] Requires { get; init; } = [];
    public Knob[] Knobs { get; init; } = [];
    public Action? Recheck { get; init; }
    public Action<FeatureContext?>? Stop { get; init; }
    public Action? Unpatched { get; init; }
    public Func<FeatureState>? Probe { get; init; }
    public EngineBodies[] Shapes { get; init; } = [];
}
