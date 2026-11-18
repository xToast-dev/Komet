using System.Globalization;
using System.Text.RegularExpressions;
using Komet.Rendering;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// Where a uniform's value comes from, for the editor's hover and the "not set" problem. The rule, first that holds:
// a Komet game value ("provided by Komet", with its live value), a setting of the active pack bound to it ("pack
// setting", with its value), the four inputs Komet gives every pass of a pack (scene, depth, resolution, time), a
// uniform another mod registers through ShaderHub, and "set by the game" when the vanilla version of the file or a
// game include declares it, or it is one of the engine's known uniforms. Anything else is "not set": it stays 0.
internal sealed partial class ShaderIde
{
    private static readonly string[] PassInputs = ["scene", "depth", "resolution", "time"];
    private string? _vanillaOf;
    private string _vanillaText = "";

    // The origin's words and, when Komet knows it, the live value
    public (string Origin, string? Value, bool Set) Origin(string path, string name)
    {
        if (GameValues.Find(name) is { } value) return (L("origin-komet"), Format(GameValues.Read(value)), true);

        if (Side.Pack?.Settings.Find(s => s.Uniform == name) is { } setting)
        {
            return (L("origin-pack"), Side.RowOf(setting.Key) is { } row ? row.ValueText(row.Get()) : null, true);
        }

        if (Array.IndexOf(PassInputs, name) >= 0 && path.Replace('\\', '/').Contains("/passes/", StringComparison.Ordinal))
        {
            return (L("origin-pass"), null, true);
        }

        if (ModShaders.UniformOwner(name) is { } mod && mod != GameValues.Mod)
        {
            return (L("origin-mod", mod), null, true);
        }

        return SetByGame(path, name) ? (L("origin-game"), null, true) : (L("origin-not-set"), null, false);
    }

    private bool SetByGame(string path, string name)
    {
        if (Array.Exists(ShaderDocs.Engine, s => s.Kind == 'u' && s.Name == name)) return true;

        var game = StudioLink.IsGameShader(path) ? path : StudioLink.GameFileOf(path);
        if (game is null) return false;

        if (_vanillaOf != game)
        {
            _vanillaOf = game;
            _vanillaText = StudioLink.Read(game, out var text, out _) ? text : "";
        }

        return Regex.IsMatch(_vanillaText, @"uniform\s+\w+\s+" + Regex.Escape(name) + @"\s*[;\[]", RegexOptions.None,
            TimeSpan.FromMilliseconds(50)) || Includes(name);
    }

    // A game include declares it (fogandlight, shadowcoords …): includes are read once
    private string[]? _includes;

    private bool Includes(string name)
    {
        _includes ??= [.. StudioLink.List(Path.Join(StudioLink.AssetsRoot, "game", "shaderincludes"))
            .Where(e => !e.Folder).Select(e => StudioLink.Read(e.Path, out var text, out _) ? text : "")];
        return Array.Exists(_includes, text => text.Contains(" " + name + ";", StringComparison.Ordinal) &&
                                               text.Contains("uniform", StringComparison.Ordinal));
    }

    public static string Format(float[] values) =>
        string.Join(", ", values.Select(v => v.ToString("0.00", CultureInfo.InvariantCulture)));

    // The editor's hover over a word: a declared uniform's type, live value and origin
    public string? Describe(string word)
    {
        if (Active is not { } document || word.Length == 0) return null;

        var declared = Regex.Match(document.Text, @"uniform\s+(\w+)\s+" + Regex.Escape(word) + @"\b", RegexOptions.None,
            TimeSpan.FromMilliseconds(50));
        var known = GameValues.Find(word);
        if (!declared.Success && known is null) return null;

        var type = declared.Success ? declared.Groups[1].Value : known!.Type;
        var (origin, value, _) = Origin(document.Path, word);
        return "uniform " + type + " " + word + "  ·  " + origin + (value is null ? "" : "  ·  " + value) +
               (known is null ? "" : "  ·  " + known.Doc);
    }

    // GAME VALUES' click: the declaration after the file's last uniform, else after #version and the includes
    public void Declare(GameValues.Value value)
    {
        if (_current is not { } document || Mode != EditorMode.Mine)
        {
            Toast(L("value-no-file"));
            return;
        }

        if (document.ReadOnly)
        {
            Toast(L("value-readonly"));
            return;
        }

        if (Declared(document.Text, value.Name)) return;

        var at = DeclarationAt(document.Text);
        _ = document.Replace(at, at, "uniform " + value.Type + " " + value.Name + ";\n");
        Changed(document.Path);
        Toast(L("value-inserted", value.Name));
    }

    public static bool Declared(string text, string name) =>
        Regex.IsMatch(text, @"uniform\s+\w+\s+" + Regex.Escape(name) + @"\b", RegexOptions.None,
            TimeSpan.FromMilliseconds(50));

    private static int DeclarationAt(string text)
    {
        var at = 0;
        foreach (Match line in Regex.Matches(text, @"^[ \t]*(uniform\s|#version|#include|precision\s).*$\n?",
                     RegexOptions.Multiline, TimeSpan.FromMilliseconds(100)).Bounded(512))
        {
            at = line.Index + line.Length;
        }

        return at;
    }
}
