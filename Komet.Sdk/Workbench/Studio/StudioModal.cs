using System.Text.Json;
using System.Text.RegularExpressions;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The studio's dialogs over a dimmed screen: Als Mod exportieren (what goes into the zip, modinfo.json's name, mod id
// and version checked as typed) and a question before something cannot be undone
internal sealed partial class StudioModal(ShaderIde host)
{
    private readonly TextField[] _fields = [new(80), new(40), new(20)];
    private readonly List<(string Name, string Info, bool Bad)> _parts = [];
    private string _title = "", _sub = "", _text = "", _ok = "";
    private string? _project;
    private Action? _confirm, _cancel, _accept;
    private int _focus;

    public bool Open { get; private set; }

    public void Confirm(string title, string text, string ok, Action run) =>
        (_title, _sub, _text, _ok, _confirm, _project, Open) = (title, "", text, ok, run, null, true);

    public void Export(string project)
    {
        var folder = Path.Join(StudioLink.ProjectsFolder, project);
        if (!Directory.Exists(folder))
        {
            host.Toast(L("no-project"));
            return;
        }

        (_title, _sub, _text, _ok, _confirm, _project, Open, _focus) =
            (L("export-mod"), project, "", L("export"), null, project, true, 0);
        _fields[0].Set(project);
        _fields[1].Set(Slug(project));
        _fields[2].Set(Version(folder));
        Parts(folder);
    }

    private void Parts(string folder)
    {
        _parts.Clear();
        var manifest = File.Exists(Path.Join(folder, "komet-shader.json"));
        _parts.Add(("komet-shader.json", manifest ? L("project-word") : L("missing"), !manifest));
        foreach (var sub in (ReadOnlySpan<string>)["shaders", "passes", "includes"])
        {
            var count = Count(Path.Join(folder, sub));
            var info = count switch
            {
                0 => L("empty-folder"),
                1 => L("one-file", count),
                _ => L("n-files", count)
            };
            _parts.Add((sub + "/", info, false));
        }

        _parts.Add(("modinfo.json", L("generated"), false));
    }

    private static int Count(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", ShaderPaths.Recursive(true)).Take(999).Count() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static string Version(string folder)
    {
        try
        {
            if (StudioLink.Read(Path.Join(folder, "komet-shader.json"), out var json, out _))
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.TryGetProperty("version", out var version) &&
                    version.ValueKind == JsonValueKind.String)
                {
                    return version.GetString() ?? "1.0.0";
                }
            }
        }
        catch (JsonException)
        {
            return "1.0.0";
        }

        return "1.0.0";
    }

    // ä ö ü ß spelled out, everything but a-z and 0-9 dropped
    private static string Slug(string name)
    {
        var lower = name.ToLowerInvariant().Replace("ä", "ae", StringComparison.Ordinal)
            .Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);
        return NotSlug.Replace(lower, "");
    }

    // What is wrong with the fields, "" when they are good
    private string Check()
    {
        var (name, id, version) = (_fields[0].Text.Trim(), _fields[1].Text.Trim(), _fields[2].Text.Trim());
        if (name.Length == 0) return L("name-missing");

        if (!ModId.IsMatch(id)) return L("modid-rule");

        return SemVer.IsMatch(version) ? "" : L("version-rule");
    }

    private void Close() => (Open, _confirm, _project) = (false, null, null);

    private void Accept()
    {
        if (_project is not { } project)
        {
            var run = _confirm;
            Close();
            run?.Invoke();
            return;
        }

        if (Check().Length > 0) return;

        var (name, id, version) = (_fields[0].Text.Trim(), _fields[1].Text.Trim(), _fields[2].Text.Trim());
        var zip = StudioLink.Export(project, id, name, version, out var error);
        Close();
        if (zip is null)
        {
            host.AddLog(error, true);
            host.Toast(error);
            return;
        }

        host.AddLog(L("exported-log", StudioLink.Display(zip)));
        host.Toast(L("exported-toast", StudioLink.Name(zip)));
        host.Tree.Reveal(zip);
    }

    public bool Key(GlKeys key, bool ctrl)
    {
        switch (key)
        {
            case GlKeys.Escape: Close(); return true;
            case GlKeys.Enter or GlKeys.KeypadEnter: Accept(); return true;
            case GlKeys.Tab when _project is not null: _focus = (_focus + 1) % _fields.Length; return true;
        }

        if (_project is not null) _ = _fields[_focus].Key(key, ctrl, host.Api);

        return true;
    }

    public void Type(char c)
    {
        if (_project is not null) _fields[_focus].Type(c);
    }

    private static readonly Regex NotSlug =
        new("[^a-z0-9]",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex ModId =
        new("^[a-z0-9]+$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    private static readonly Regex SemVer =
        new(@"^\d+\.\d+\.\d+$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
}
