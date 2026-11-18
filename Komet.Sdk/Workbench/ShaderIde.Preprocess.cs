using Komet.Hud;

namespace Komet.Sdk;

// The editor's greyed #if branches: the open file's lines the preprocessor drops with the engine's defines of its
// program (read at most twice a second; a render setting that reloads the shaders changes them, the grey follows) and
// the file's own #define/#undef. Before the game used the program its defines are unknown and nothing is greyed.
internal sealed partial class ShaderIde
{
    private const long DefinesEveryMs = 500;

    private bool[] _inactive = [];
    private string? _inactiveText;
    private string _inactiveDefines = "";
    private string _definesKey = "";
    private string? _definesPath;
    private List<(string Name, string Value)> _definesNow = [];
    private long _definesAt;

    public Rgba InactiveTint => Ground with { A = 0.62 };

    public bool Inactive(ShaderDocument document, int line)
    {
        if (!ShaderPaths.IsStage(document.Path)) return false;

        Defines(document.Path);
        if (!ReferenceEquals(_inactiveText, document.Text) || !ReferenceEquals(_inactiveDefines, _definesKey))
        {
            (_inactiveText, _inactiveDefines) = (document.Text, _definesKey);
            _inactive = _definesNow.Count == 0 ? [] : ShaderPreprocessor.Inactive(document.Lines, _definesNow);
        }

        return line >= 0 && line < _inactive.Length && _inactive[line];
    }

    private void Defines(string path)
    {
        var now = Environment.TickCount64;
        if (now - _definesAt < DefinesEveryMs && _definesPath == path) return;

        (_definesAt, _definesPath) = (now, path);
        var defines = ShaderInfo.Defines(path);
        var key = path + "|" + string.Join(";", defines.Select(d => d.Name + "=" + d.Value));
        if (key != _definesKey)
        {
            (_definesNow, _definesKey) = (defines, key); // a new key object only when the defines changed
        }
    }
}
