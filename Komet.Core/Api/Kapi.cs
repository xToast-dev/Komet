using System.Globalization;
using Komet.Core;

namespace Komet.Api;

/// <summary>
///     KAPI, Komet's API for other mods. <see cref="Connect(ModSystem, string)" /> hands a mod its
///     <see cref="KapiMod" />, through which it reaches everything else.
/// </summary>
/// <remarks>
///     <para>
///         KAPI is versioned <c>major.minor</c>. A mod names the version it was written against; Komet connects it
///         when the major versions match and its own minor version is the same or newer. A new minor version only adds;
///         a new major version may change or remove.
///     </para>
///     <para>
///         Reference <c>Komet.Core.dll</c> without shipping it, name KAPI's types only in a class of your own, and
///         call that class only when <c>api.ModLoader.IsModEnabled("komet")</c>: the mod then loads without Komet too.
///     </para>
/// </remarks>
/// <example>
///     <code language="csharp">
/// // in a class of its own, called from StartClientSide when Komet is enabled
/// var komet = Kapi.Connect(this, "1.0");
/// if (komet is null) return; // a Komet too old for this mod: the log says so
/// komet.Shaders.Uniform("mymodWarmth", () =&gt; config.Warmth);
/// komet.Options.Page("My mod").Switch("Warm picture", () =&gt; config.Warm, v =&gt; config.Warm = v);
/// </code>
/// </example>
public static class Kapi
{
    /// <summary>KAPI's major version: a mod written against another major version is not connected.</summary>
    public const int Major = 1;

    /// <summary>KAPI's minor version: a mod written against this one or an older one is connected.</summary>
    public const int Minor = 0;

    /// <summary>How many mods may be connected at once.</summary>
    public const int MaxMods = 64;

    private static readonly List<KapiMod> Connected = [];
    private static readonly List<(string ModId, string Version, string Why)> RefusedMods = [];
    private static readonly HashSet<string> Silenced = new(StringComparer.Ordinal);

    // A connection lasts one world: its registrations end with it
    static Kapi() => WorldEnd.On(nameof(Kapi), DisconnectAll);

    /// <summary>KAPI's version, <c>major.minor</c>: <c>1.0</c>.</summary>
    public static string Version => Major.ToString(CultureInfo.InvariantCulture) + "." +
                                    Minor.ToString(CultureInfo.InvariantCulture);

    // The connected mods and the ones refused this world, for the Connected mods page
    internal static IReadOnlyList<KapiMod> Mods => [.. Connected.Bounded(MaxMods)];

    internal static IReadOnlyList<(string ModId, string Version, string Why)> Refusals =>
        [.. RefusedMods.Bounded(MaxMods)];

    /// <summary>Whether a mod written against <paramref name="version" /> can connect to this Komet.</summary>
    /// <param name="version"><c>major.minor</c>, for example <c>1.0</c>.</param>
    /// <returns>True when the major versions match and this Komet's minor version is the same or newer.</returns>
    public static bool Supports(string version) =>
        Parse(version) is { Major: Major, Minor: <= Minor };

    /// <summary>
    ///     Connects a mod: its id comes from the mod system's mod. Call in <c>StartClientSide</c>, on the main thread.
    /// </summary>
    /// <param name="system">The mod's mod system (<c>this</c>).</param>
    /// <param name="version">The KAPI version the mod was written against, <c>major.minor</c>.</param>
    /// <returns>The connection; null when the version is not supported or the mod has no id (the log says why).</returns>
    public static KapiMod? Connect(ModSystem system, string version) =>
        NotNull(system) ? Connect(system.Mod?.Info?.ModID ?? "", version) : null;

    /// <summary>Connects a mod by its id. Prefer <see cref="Connect(ModSystem, string)" />.</summary>
    /// <param name="modId">The mod's id, as in its <c>modinfo.json</c>.</param>
    /// <param name="version">The KAPI version the mod was written against, <c>major.minor</c>.</param>
    /// <returns>The connection; null when the version is not supported or the id is empty (the log says why).</returns>
    public static KapiMod? Connect(string? modId, string version)
    {
        if (string.IsNullOrWhiteSpace(modId) || modId.Contains(':', StringComparison.Ordinal))
            return Refused(modId ?? "", "a mod id without ':' is needed");
        if (!Supports(version))
            return Refused(modId, $"it was written for KAPI {version}, this Komet offers KAPI {Version}", version);
        if (Connected.Count >= MaxMods) return Refused(modId, $"more than {MaxMods} mods are connected");

        var mod = new KapiMod(modId, version);
        Connected.Add(mod);
        _ = RefusedMods.RemoveAll(r => r.ModId == modId);
        return mod;
    }

    // The player switched a mod's render steps off (Connected mods page); kept for the session, every world
    internal static bool IsSilenced(string modId) => Silenced.Contains(modId);

    internal static void Silence(string modId, bool off) => _ = off ? Silenced.Add(modId) : Silenced.Remove(modId);

    internal static void Disconnected(KapiMod mod) => _ = Connected.Remove(mod);

    private static void DisconnectAll()
    {
        foreach (var mod in (KapiMod[])[.. Connected.Bounded(MaxMods)]) mod.Dispose();
        Connected.Clear();
        RefusedMods.Clear();
    }

    private static KapiMod? Refused(string modId, string why, string version = "")
    {
        ModEvents.Logger?.Warning("Komet: KAPI did not connect {0}: {1}", modId, why);
        if (modId.Length > 0 && RefusedMods.Count < MaxMods && !RefusedMods.Exists(r => r.ModId == modId))
        {
            RefusedMods.Add((modId, version, why));
        }

        return null;
    }

    private static (int Major, int Minor)? Parse(string version)
    {
        if (string.IsNullOrEmpty(version) || version.Length > 16) return null;

        var parts = version.Split('.');
        return parts.Length == 2 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor)
            : null;
    }
}

/// <summary>
///     One mod's connection to Komet, from <see cref="Kapi.Connect(ModSystem, string)" />. Every registration made
///     through it carries the mod's id, and <see cref="Dispose" /> takes all of them away at once.
/// </summary>
/// <remarks>
///     A connection lasts until it is disposed or the world closes; connect again in the next
///     <c>StartClientSide</c>. Main thread only, except <see cref="KapiDebug.Measure" />.
/// </remarks>
public sealed class KapiMod : IDisposable
{
    /// <summary>How many registrations one connection keeps; more are refused.</summary>
    public const int MaxRegistrations = 1024;

    private readonly List<Kept> _undo = [];

    internal KapiMod(string modId, string version)
    {
        (ModId, Version) = (modId, version);
        (Shaders, Rendering, Options, Features, Debug, Styles) =
            (new KapiShaders(this), new KapiRendering(this), new KapiOptions(this), new KapiFeatures(this),
                new KapiDebug(this), new KapiStyles(this));
    }

    /// <summary>The mod's id: every page, pass, feature, timing and style of the connection carries it.</summary>
    public string ModId { get; }

    /// <summary>The KAPI version the mod asked for.</summary>
    public string Version { get; }

    /// <summary>False once disposed, or once the world closed.</summary>
    public bool IsConnected { get; private set; } = true;

    /// <summary>Defines, uniforms and source edits for the game's shaders, and fullscreen passes of the mod's own.</summary>
    public KapiShaders Shaders { get; }

    /// <summary>Render steps of the mod's own at named places in Komet's frame.</summary>
    public KapiRendering Rendering { get; }

    /// <summary>The mod's pages in Komet's options window.</summary>
    public KapiOptions Options { get; }

    /// <summary>The mod's own features, holds on Komet's, and every feature's state.</summary>
    public KapiFeatures Features { get; }

    /// <summary>Timings of the mod's code in Komet's <c>.komet debug</c> protocol.</summary>
    public KapiDebug Debug { get; }

    /// <summary>Styles for Komet's options window.</summary>
    public KapiStyles Styles { get; }

    // Features stay for the world: counted apart from what Dispose undoes
    internal int FeatureCount { get; set; }

    /// <summary>
    ///     Takes away everything the connection registered: passes, defines, uniforms, edits, pages, styles, holds and
    ///     event handlers. Features stay until the world closes (Harmony patches cannot all be taken back at once).
    ///     Later calls do nothing.
    /// </summary>
    public void Dispose()
    {
        if (!IsConnected) return;

        IsConnected = false;
        _undo.Reverse();
        foreach (var kept in _undo.Bounded(MaxRegistrations))
        {
            try
            {
                kept.Undo();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                ModEvents.Logger?.Error("Komet: KAPI could not take back a registration of {0}: {1}", ModId, e);
            }
        }

        _undo.Clear();
        Kapi.Disconnected(this);
    }

    // Live registrations of a kind, for the Connected mods page
    internal int Count(KapiKind kind) => _undo.Count(k => k.Kind == kind && !k.Done);

    // What Dispose undoes, last first; false when the connection is gone or full (the caller undoes at once)
    internal bool Keep(Action undo, KapiKind kind = KapiKind.Handler) => Track(undo, kind) is not null;

    // A refused registration takes no slot; one the connection cannot keep is undone at once and says why
    internal KapiRegistration Keep(IDisposable made, string? error = null, KapiKind kind = KapiKind.Shader)
    {
        if (made is Nothing) return new KapiRegistration(null, error);

        if (Track(static () => { }, kind) is not { } slot)
        {
            made.Dispose();
            return new KapiRegistration(null, IsConnected ? "too many registrations" : "the connection is closed");
        }

        var registration = new KapiRegistration(made, error) { Disposed = () => slot.Done = true };
        slot.Undo = registration.Dispose;
        return registration;
    }

    private Kept? Track(Action undo, KapiKind kind)
    {
        if (!NotNull(undo) || !IsConnected) return null;

        // registrations already disposed hold no slot (a step replaced again and again, one made and dropped)
        if (_undo.Count >= MaxRegistrations) _ = _undo.RemoveAll(static k => k.Done);
        if (_undo.Count >= MaxRegistrations) return null;

        var kept = new Kept(undo, kind);
        _undo.Add(kept);
        return kept;
    }

    private sealed class Kept(Action undo, KapiKind kind)
    {
        public Action Undo { get; set; } = undo;
        public KapiKind Kind { get; } = kind;
        public bool Done { get; set; }
    }
}

// What a connection registered, by kind (the Connected mods page counts them)
internal enum KapiKind
{
    Shader,
    Pass,
    Step,
    Page,
    Hold,
    Style,
    Handler
}

/// <summary>
///     A registration from <see cref="KapiShaders" />. <see cref="Active" /> tells whether Komet took it;
///     <see cref="Error" /> says why not. Disposing takes it away; its <see cref="KapiMod" /> does so at its own
///     <see cref="KapiMod.Dispose" />.
/// </summary>
public sealed class KapiRegistration : IDisposable
{
    private IDisposable? _made;

    internal KapiRegistration(IDisposable? made, string? error)
    {
        var refused = made is null or Nothing;
        (_made, Error) = (refused ? null : made,
            refused ? error ?? "refused: an invalid argument, a full table or no module to take it" : null);
    }

    /// <summary>Komet took the registration, and it has not been disposed.</summary>
    public bool Active => _made is not null;

    /// <summary>Why Komet refused it; null when it was taken.</summary>
    public string? Error { get; }

    // Told once the registration went, by either side
    internal Action? Disposed { get; init; }

    /// <summary>Takes the registration away. Later calls do nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _made, null) is not { } made) return;

        made.Dispose();
        Disposed?.Invoke();
    }
}
