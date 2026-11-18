using System.Reflection;
using Komet.Core;

namespace Komet.Hud;

// This build and its update check: version and source from modinfo.json, channel, commit and edition (from the
// configuration and modinfo.json's version suffix) from the assembly's metadata
internal sealed class HudBuild : IDisposable
{
    // Assertions do not inline without the optimizer: every Komet timing is inflated several times
    public static readonly bool DebugBuild =
        Self.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration == "Debug";

    private static string? _editionStamp; // the overlay asks on every composition
    private static string? _builtStamp; // the version page asks on every recompose
    private readonly ILogger _logger;

    public HudBuild(ICoreClientAPI capi)
    {
        _logger = capi.Logger;
        var mod = capi.ModLoader.GetMod(KometMod.Id);
        if (!NotNull(mod) || !Assert(mod.Info.Version.Length > 0)) return;

        (Version, Preview, Commit, SourcePath) = (mod.Info.Version, Metadata(Self, "Channel") == "preview",
            Metadata(Self, "Commit"), mod.SourcePath ?? "");
    }

    private static Assembly Self => KometMod.Assembly; // the mod's, stamped by the build; set before the HUD exists

    public string Version { get; } = "";
    private bool Preview { get; }
    public string Commit { get; } = "";
    public string SourcePath { get; } = "";

    // Null until the player agreed to checks or asked for one
    public UpdateCheck? Update { get; private set; }

    // "3.0.0 release abc1234", as the protocol heads it
    public string Label => Version + " " + Edition() + (Commit.Length > 0 ? " " + Commit : "");

    public void Dispose() => Update?.Dispose();

    public static string BuiltStamp() => _builtStamp ??= Metadata(Self, "Built");

    private static string Metadata(Assembly assembly, string key) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Take(64).FirstOrDefault(a => a.Key == key)
            ?.Value ?? "";

    // Komet.csproj stamps every build from its configuration and modinfo.json's version suffix
    public static string Edition()
    {
        var stamped = _editionStamp ??= Metadata(Self, "Edition");
        return Assert(stamped.Length > 0) && Assert(stamped is "release" or "pre" or "dev") ? stamped : "dev";
    }

    // A click on "check" is consent for that one check.
    public void RunUpdateCheck()
    {
        if (!Assert(Version.Length > 0)) return;

        if (Update is null)
        {
            Update = new UpdateCheck(_logger, Version, Preview, Commit, SourcePath);
        }
        else
        {
            Update.Start();
        }
    }

    public (string Text, Rgba Fill, Rgba Color) EditionBadge()
    {
        var edition = Edition();
        var text = HudParts.T("hud-v-edition-" + edition, Version);
        var (fill, color) = edition switch
        {
            "release" => (HudUi.Ok with { A = 0.25 }, HudUi.Ok),
            "pre" => (HudUi.Warn with { A = 0.22 }, HudUi.Warn),
            _ => (HudUi.Accent, Rgba.White(1))
        };
        return (text, fill, color);
    }

    // The window title's badges: the edition, then the check's state; each opens the version page
    public (string Text, Rgba Fill, Rgba Color, Action? Click)[] Badges(Action open)
    {
        var (text, fill, color) = EditionBadge();
        return CheckBadge() is { } check
            ? [(text, fill, color, open), (check.Text, check.Fill, check.Color, open)]
            : [(text, fill, color, open)];
    }

    // The menu footer's chips: the short edition, then a newer version or a file that differs, by name; "checked" quietly
    public List<(string Text, Rgba Color)> VersionState()
    {
        var (_, _, color) = EditionBadge();
        List<(string, Rgba)> chips = [(HudParts.T("hud-v-edition-short-" + Edition(), Version), color)];
        if (Update?.Report is not { } r) return chips;

        if (r.State == UpdateState.Outdated)
        {
            chips.Add((HudParts.T("hud-v-check-newer", r.Newest), HudUi.Warn));
        }
        else if (r.State == UpdateState.Mismatch)
        {
            chips.Add((HudParts.T("hud-v-check-mismatch"), HudUi.Err));
        }
        else if (r.State == UpdateState.Verified)
        {
            chips.Add((HudParts.T("hud-v-check-verified"), HudUi.Ok));
        }

        return chips;
    }

    private (string Text, Rgba Fill, Rgba Color)? CheckBadge()
    {
        if (Update is not { } check) return null;

        var r = check.Report;
        (string Key, Rgba Color) b = r.State switch
        {
            UpdateState.Verified => ("hud-v-check-verified", HudUi.Ok),
            UpdateState.Mismatch => ("hud-v-check-mismatch", HudUi.Err),
            UpdateState.Outdated => ("hud-v-check-outdated", HudUi.Warn),
            UpdateState.Checking => ("hud-v-check-running", HudUi.Dim),
            UpdateState.Unverified or UpdateState.NoRelease => ("hud-v-check-none", HudUi.Dim),
            _ => ("hud-v-check-failed", HudUi.Dim)
        };
        return (HudParts.T(b.Key, r.Newest), b.Color with { A = 0.22 }, b.Color);
    }
}
