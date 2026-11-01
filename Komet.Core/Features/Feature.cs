using System.Reflection;
using HarmonyLib;
using Komet.Api;
using static Komet.Api.FeatureState;

namespace Komet.Core;

// Main: ModTimes to HangWatch; Last: once the HUD renders and every patch is in (no built-in feature uses it now)
internal enum FeatureStage
{
    Main,
    Last
}

// Engine bodies a feature reproduces, skips or replays, and their fingerprint in the version written against
internal readonly record struct EngineBodies(Func<MethodBase?[]> Methods, ulong Expected);

// Overlay: set by the Hud feature for those after it (GraphicsMenu, the renderers).
internal sealed class FeatureContext(Harmony harmony, ICoreClientAPI api, ILogger logger, bool localServer)
{
    public Harmony Harmony { get; } = harmony;
    public ICoreClientAPI Api { get; } = api;
    public ILogger Logger { get; } = logger;
    public bool LocalServer { get; } = localServer;
    public IRenderer? Overlay { get; set; }
}

// Install null: the knob is the feature. Stop: before UnpatchAll, reverse order; Unpatched: after. Recheck: once every
// mod has patched (LevelFinalize).
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

    // Engine methods the feature skips (a prefix returning false) or rewrites (a transpiler), for features without a
    // fingerprinted SeamGuard. Another mod's patch of Rivals kinds on one of them (Replacing for a skip: its prefix or
    // transpiler would be lost; Body for a rewrite: two rewrites of one body) stands the feature down, its switch held at
    // the engine's value, until that mod is gone. Methods not found (null) are left out: nothing was patched there.
    public Func<MethodBase?[]>? Replaces { get; init; }
    public PatchKinds Rivals { get; init; } = PatchKinds.Replacing;
}

// What the modules' feature lists build their entries with (using static)
internal static class FeatureKit
{
    // A probe's answer from a guard's Matched and Blocked
    public static FeatureState Found(bool matched, bool blocked, FeatureState blockedAs = StoodDown) =>
        (matched, blocked) switch
        {
            (false, _) => EngineChanged,
            (_, true) => blockedAs,
            _ => Active
        };

    // A 0/1 knob over a bool static
    public static Knob Switch(string key, string? page, string? group, Func<bool> get, Action<bool> set,
        string? json = null) =>
        Assert(key.Length > 0) && NotNull(get) && NotNull(set)
            ? new Knob(key, page, group, 0, 1, 0, () => get() ? 1 : 0, value => set(value != 0), json)
            : throw new ArgumentException("knob without a name", nameof(key));
}

// A probe's reflection answer, asked again only after Epoch moved (each world's install)
internal sealed class ProbeTarget(Func<object?> find)
{
    public static int Epoch { get; set; }
    private object? _value;
    private int _at = -1;

    public bool Missing
    {
        get
        {
            if (_at != Epoch) (_value, _at) = (find(), Epoch);
            return _value is null;
        }
    }
}
