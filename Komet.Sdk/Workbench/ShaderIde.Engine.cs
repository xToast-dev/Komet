using System.Text.RegularExpressions;
using static Komet.Sdk.IdeText;

namespace Komet.Sdk;

// The Engine view: read-only C# the game runs for the shown shader's program, decompiled on a worker from the player's
// own installation (EngineSource). While it runs, or when nothing is found, the view says why instead of staying empty.
// A Ctrl+click on a uniform's declaration opens the view at the line that sets it.
internal sealed partial class ShaderIde
{
    private ShaderDocument? _engine;
    private string? _engineFor, _engineJump;
    private bool _engineDone;

    private void ShowEngine()
    {
        EngineSource.Start();
        var program = Program(_current);
        if (program is null)
        {
            SetEngine("none", L("engine-not-shader"), true);
            return;
        }

        if (_engineFor != _current!.Path)
        {
            (_engineFor, _engineDone) = (_current.Path, false);
            SetEngine(program, L("engine-loading"), false);
        }

        _ = EnginePoll();
    }

    // Per frame while the view waits: the finished text replaces the notice once
    private bool EnginePoll()
    {
        if (Mode != EditorMode.Engine || _engineDone || _current is null || Program(_current) is not { } program)
        {
            return false;
        }

        var uniforms = Regex.Matches(_current.Text, @"uniform\s+\w+\s+(\w+)", RegexOptions.None, TimeSpan.FromMilliseconds(100))
            .Select(m => m.Groups[1].Value).Distinct().Take(32).ToArray();
        if (EngineSource.Query(program, uniforms) is not { } text) return false;

        var empty = EngineSource.Failed ? L("engine-failed", EngineSource.Error) : L("engine-none", program);
        var shown = text.Length > 0 ? text : empty;
        SetEngine(program, shown, true);
        if (_engineJump is { } uniform)
        {
            _engineJump = null;
            Jump(uniform);
        }

        return true;
    }

    private void SetEngine(string program, string text, bool done)
    {
        _engine = new ShaderDocument(Path.Join(StudioLink.DataRoot, "engine", program + ".cs"), text, text, true);
        _engineDone = done;
    }

    // The game program a file belongs to: its own name for a vanilla shader or a copy of one
    private static string? Program(ShaderDocument? document)
    {
        if (document is null) return null;

        var game = StudioLink.IsGameShader(document.Path) ? document.Path : StudioLink.GameFileOf(document.Path);
        return game is not null && ShaderPaths.IsStage(game) ? Path.GetFileNameWithoutExtension(game) : null;
    }

    public bool JumpToEngine(string uniform)
    {
        if (Program(_current) is null || uniform.Length == 0) return false;

        SetMode(EditorMode.Engine);
        if (_engineDone)
        {
            Jump(uniform);
        }
        else
        {
            _engineJump = uniform;
        }

        return true;
    }

    private void Jump(string uniform)
    {
        if (_engine is not { } engine) return;

        var line = EngineSource.LineOf(engine.Text, uniform);
        if (line < 0)
        {
            Toast(L("engine-uniform-not-found", uniform));
            return;
        }

        engine.Move(engine.LineOffset(line), false);
        engine.FirstLine = Math.Max(0, line - 3);
        Redraw();
    }
}
