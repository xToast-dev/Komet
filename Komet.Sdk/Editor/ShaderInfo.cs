using System.Globalization;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Sdk;

internal enum ShaderItemKind
{
    Uniform,
    Input,
    Output
}

// One line of THIS SHADER: a declaration of the open file (offset into its text; -1 for an engine define) with its type,
// its location (inputs/outputs) or value (defines)
internal sealed record ShaderItem(ShaderItemKind Kind, string Name, string Type, string Detail, int Offset);

// What the open file declares and what the engine sets around it. Declarations from the text (uniform, in/out with their
// layout location), defines from the live program's PrefixCode (the engine's exact prefix for this program, vertex and
// fragment), live values through glGetUniform on that program (on demand, only for the shown section).
internal static class ShaderInfo
{
    public const int MaxItems = 96;

    // No time limit, as before: both run over a whole expanded source and have nothing to backtrack into
    private static readonly Regex Declaration =
        new(
            @"^[ \t]*(?:layout\s*\(\s*location\s*=\s*(\d+)\s*\)\s*)?(?:flat\s+|smooth\s+|noperspective\s+)?(uniform|in|out|attribute|varying)\s+(?:(?:highp|mediump|lowp)\s+)?(\w+)\s+(\w+)(\s*\[[^\]]*\])?",
            RegexOptions.Multiline | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex DefineLine =
        new(@"#define\s+(\w+)(?:[ \t]+([^\r\n]*))?",
            RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    public static List<ShaderItem> Declared(string text, bool vertex)
    {
        var items = new List<ShaderItem>();
        foreach (var g in Declaration.Matches(text).Take(MaxItems).Select(m => m.Groups).Bounded(MaxItems))
        {
            var word = g[2].Value;
            var kind = word switch
            {
                "uniform" => ShaderItemKind.Uniform,
                "in" or "attribute" => ShaderItemKind.Input,
                "varying" => vertex ? ShaderItemKind.Output : ShaderItemKind.Input,
                _ => ShaderItemKind.Output
            };
            var location = g[1].Success ? "location " + g[1].Value : "";
            items.Add(new ShaderItem(kind, g[4].Value, g[3].Value + g[5].Value.Trim(), location,
                g[4].Index));
        }

        return items;
    }

    // The program the game runs for this stage file (its name is the file's stem), null before it was used
    public static ShaderProgramBase? ProgramOf(string path) =>
        DrawCounter.Program(Path.GetFileNameWithoutExtension(path));

    // The engine's defines for this file's stage: name -> value, in prefix order
    public static List<(string Name, string Value)> Defines(string path)
    {
        var list = new List<(string, string)>();
        if (ProgramOf(path) is not { } program) return list;

        var vertex = path.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase);
        var shader = vertex ? program.VertexShader : program.FragmentShader;
        foreach (var g in DefineLine.Matches(shader?.PrefixCode ?? "").Take(MaxItems).Select(m => m.Groups)
                     .Bounded(MaxItems))
        {
            list.Add((g[1].Value, g[2].Value.Trim()));
        }

        return list;
    }

    // A uniform's live value in the program (null: not active in it, optimised out or no program yet)
    public static float[]? Live(ShaderProgramBase program, string name, string type)
    {
        var at = GL.GetUniformLocation(program.ProgramId, name);
        if (at < 0) return null;

        var count = type switch
        {
            "vec2" or "ivec2" => 2,
            "vec3" or "ivec3" => 3,
            "vec4" or "ivec4" => 4,
            "mat3" => 9,
            "mat4" => 16,
            _ => 1
        };
        var values = new float[16];
        if (type is "int" or "ivec2" or "ivec3" or "ivec4" or "bool" ||
            type.StartsWith("sampler", StringComparison.Ordinal))
        {
            var ints = new int[16];
            GL.GetUniform(program.ProgramId, at, ints);
            for (var i = 0; i < 16; i++)
            {
                values[i] = ints[i];
            }
        }
        else
        {
            GL.GetUniform(program.ProgramId, at, values);
        }

        return values[..count];
    }

    public static string Text(float[] v, int max = 4)
    {
        var parts = new string[Math.Min(v.Length, max)];
        for (var i = 0; i < Math.Min(parts.Length, 16); i++)
        {
            parts[i] = v[i].ToString("0.###", CultureInfo.InvariantCulture);
        }

        return string.Join(" ", parts) + (v.Length > max ? " …" : "");
    }
}
