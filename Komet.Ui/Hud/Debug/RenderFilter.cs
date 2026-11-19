namespace Komet.Hud;

internal sealed class RenderFilter
{
    internal string Query { get; set; } = "";
    internal string Mod { get; set; } = "";
    internal string Stage { get; set; } = "";
    internal bool ErrorsOnly { get; set; }

    internal bool Matches(string mod, string stage, bool error, string text) =>
        (!ErrorsOnly || error) &&
        (Mod.Length == 0 || mod.Split('\n').Contains(Mod, StringComparer.OrdinalIgnoreCase)) &&
        (Stage.Length == 0 || Stage.Equals(stage, StringComparison.OrdinalIgnoreCase)) &&
        (Query.Length == 0 || text.Contains(Query, StringComparison.OrdinalIgnoreCase));

    internal void Clear() => (Query, Mod, Stage, ErrorsOnly) = ("", "", "", false);
}
