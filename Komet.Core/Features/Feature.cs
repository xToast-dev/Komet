using System.Reflection;
using HarmonyLib;
using Komet.Api;
using static Komet.Api.KapiFeatureState;

namespace Komet.Core;

// Engine bodies a feature reproduces, skips or replays, and their fingerprint in the version written against
internal readonly record struct EngineBodies(Func<MethodBase?[]> Methods, ulong Expected);

// What a feature's install gets: Komet's Harmony, the client, its logger and whether the server runs in this process.
// Services between modules are not here: they are typed (Services), features of one module share their own statics.
internal sealed class FeatureContext(Harmony harmony, ICoreClientAPI api, ILogger logger, bool localServer)
{
    public Harmony Harmony { get; } = harmony;
    public ICoreClientAPI Api { get; } = api;
    public ILogger Logger { get; } = logger;
    public bool LocalServer { get; } = localServer;
}

// When a feature runs. Install null: the knob is the feature. Recheck: once every mod has patched (LevelFinalize), and
// when a shader mod starts or stops late. Stop: before UnpatchAll, in reverse install order (null context: Komet stood
// down before it had one). Unpatched: after UnpatchAll, in install order.
internal readonly record struct FeatureLife(
    Action<FeatureContext>? Install,
    Action? Recheck,
    Action<FeatureContext?>? Stop,
    Action? Unpatched);

// Where a feature installs (FeatureOrder): after those it Requires (which must exist) and After, before those Before
// names; among the features free to go next the lowest Order, then the id. Order is also the knobs' order in komet.json and
// on the settings pages, so it is unique among Komet's features. ServerOnly: installed only when the server runs in this
// process.
internal sealed record FeaturePlace(int Order, string[] Requires, string[] After, string[] Before, bool ServerOnly)
{
    public static readonly FeaturePlace None = new(0, [], [], [], false);
}

// How a feature knows it works and when it keeps out of another mod's way. Probe: its own state (Active when fine).
// Shapes: engine bodies it was written against. Replaces: engine methods it skips (a prefix returning false) or
// rewrites (a transpiler), for features without a fingerprinted SeamGuard; another mod's patch of Rivals kinds on one
// of them (Replacing for a skip: its prefix or transpiler would be lost; Body for a rewrite: two rewrites of one body)
// stands the feature down, its switch held at the engine's value, until that mod is gone. Missing methods (null) also
// make the guard stand down.
internal sealed record FeatureGuard(
    Func<KapiFeatureState>? Probe,
    EngineBodies[] Shapes,
    Func<MethodBase?[]>? Replaces,
    KapiPatchKinds Rivals)
{
    public static readonly FeatureGuard None = new(null, [], null, KapiPatchKinds.Replacing);
}

// One feature: its lifecycle, its place in the order, its guard and its knobs. The init properties below are the
// modules' authoring syntax; each sets its part.
internal sealed record Feature(string Id)
{
    public FeatureLife Life { get; private init; }
    public FeaturePlace Place { get; private init; } = FeaturePlace.None;
    public FeatureGuard Guard { get; private init; } = FeatureGuard.None;

    // ReSharper disable once TypeWithSuspiciousEqualityIsUsedInRecord.Global features are never compared by value
    public Knob[] Knobs { get; init; } = [];

    public Action<FeatureContext>? Install
    {
        // ReSharper disable once UnusedMember.Global set by the authoring syntax; a getter keeps it readable
        get => Life.Install;
        init => Life = Life with { Install = value };
    }

    public Action? Recheck
    {
        // ReSharper disable once UnusedMember.Global set by the authoring syntax; a getter keeps it readable
        get => Life.Recheck;
        init => Life = Life with { Recheck = value };
    }

    public Action<FeatureContext?>? Stop
    {
        get => Life.Stop;
        init => Life = Life with { Stop = value };
    }

    public Action? Unpatched
    {
        get => Life.Unpatched;
        init => Life = Life with { Unpatched = value };
    }

    public int Order
    {
        get => Place.Order;
        init => Place = Place with { Order = value };
    }

    public string[] Requires
    {
        get => Place.Requires;
        init => Place = Place with { Requires = value };
    }

    public string[] After
    {
        // ReSharper disable once UnusedMember.Global set by the authoring syntax; a getter keeps it readable
        get => Place.After;
        init => Place = Place with { After = value };
    }

    public string[] Before
    {
        // ReSharper disable once UnusedMember.Global set by the authoring syntax; a getter keeps it readable
        get => Place.Before;
        init => Place = Place with { Before = value };
    }

    public bool ServerOnly
    {
        get => Place.ServerOnly;
        init => Place = Place with { ServerOnly = value };
    }

    public Func<KapiFeatureState>? Probe
    {
        // ReSharper disable once UnusedMember.Global set by the authoring syntax; a getter keeps it readable
        get => Guard.Probe;
        init => Guard = Guard with { Probe = value };
    }

    public EngineBodies[] Shapes
    {
        get => Guard.Shapes;
        init => Guard = Guard with { Shapes = value };
    }

    public Func<MethodBase?[]>? Replaces
    {
        get => Guard.Replaces;
        init => Guard = Guard with { Replaces = value };
    }

    public KapiPatchKinds Rivals
    {
        // ReSharper disable once UnusedMember.Global set by the authoring syntax; a getter keeps it readable
        get => Guard.Rivals;
        init => Guard = Guard with { Rivals = value };
    }
}

// What the modules' feature lists build their entries with (using static)
internal static class FeatureKit
{
    // A probe's answer from a guard's Matched and Blocked
    public static KapiFeatureState Found(bool matched, bool blocked, KapiFeatureState blockedAs = StoodDown) =>
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
    private int _at = -1;
    private object? _value;
    public static int Epoch { get; set; }

    public bool Missing
    {
        get
        {
            if (_at != Epoch) (_value, _at) = (find(), Epoch);

            return _value is null;
        }
    }
}
