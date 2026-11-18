using Komet.Rendering;

namespace Komet.Sdk;

// A stage's text as the driver would get it, built without the game: the includes inlined from the engine's loaded
// table (each once, as the engine does) and the program's PrefixCode after #version. The 3D view compiles this, so it
// shows the editor's text whether or not the game runs the shader.
internal static partial class ShaderBuild
{
    internal static string Draft(ICoreClientAPI api, string program, string ext, string text,
        System.Func<string, string?>? draftInclude = null)
    {
        var p = ShaderMods.ProgramByName(api, program) ?? DrawCounter.Program(program);
        var prefix = (ext == ".vsh" ? p?.VertexShader : p?.FragmentShader)?.PrefixCode ?? "";
        // The test scene supplies ordinary vertex attributes and its own light. It has no game SSBOs or shadow maps.
        prefix += "\n#undef USESSBO\n#define USESSBO 0\n#undef SHADOWQUALITY\n#define SHADOWQUALITY 0\n" +
                  "#undef DYNLIGHTS\n#define DYNLIGHTS 0\n" +
                  "#ifndef USEOIT\n#define USEOIT 1\n#endif\n";
        List<(string Text, string File, int Line)> lines = [];
        Expand(text, "", lines, draftInclude);
        var builder = new System.Text.StringBuilder(text.Length + prefix.Length + 1024);
        var prefixed = false;
        for (var i = 0; i < Math.Min(lines.Count, MaxLines); i++)
        {
            _ = builder.Append(lines[i].Text).Append('\n');
            if (!prefixed && lines[i].Text.StartsWith("#version", StringComparison.Ordinal))
            {
                _ = builder.Append(prefix).Append('\n');
                prefixed = true;
            }
        }

        return prefixed ? builder.ToString() : "#version 330 core\n" + prefix + "\n" + builder;
    }
}
