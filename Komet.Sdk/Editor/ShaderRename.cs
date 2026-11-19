using System.Text;

namespace Komet.Sdk;

// Safe symbol rename: a valid new GLSL name, and whole-word replacement that leaves comments and string literals alone
internal static class ShaderRename
{
    private const int MaxName = 128;

    // GLSL keywords, reserved words and the built-in functions and types a rename must never produce
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "attribute", "const", "uniform", "varying", "layout", "centroid", "flat", "smooth", "noperspective", "patch",
        "sample", "break", "continue", "do", "for", "while", "switch", "case", "default", "if", "else", "subroutine",
        "in", "out", "inout", "float", "double", "int", "void", "bool", "true", "false", "invariant", "precise",
        "discard", "return", "lowp", "mediump", "highp", "precision", "struct", "uint", "buffer", "shared",
        "coherent", "volatile", "restrict", "readonly", "writeonly", "atomic_uint", "common", "partition", "active",
        "asm", "class", "union", "enum", "typedef", "template", "this", "goto", "inline", "noinline", "public",
        "static", "extern", "external", "interface", "long", "short", "half", "fixed", "unsigned", "superp", "input",
        "output", "filter", "sizeof", "cast", "namespace", "using", "define", "undef", "ifdef", "ifndef", "elif",
        "endif", "include", "version", "extension", "pragma", "line", "error",
        "vec2", "vec3", "vec4", "ivec2", "ivec3", "ivec4", "uvec2", "uvec3", "uvec4", "bvec2", "bvec3", "bvec4",
        "dvec2", "dvec3", "dvec4", "mat2", "mat3", "mat4", "mat2x2", "mat2x3", "mat2x4", "mat3x2", "mat3x3",
        "mat3x4", "mat4x2", "mat4x3", "mat4x4", "sampler1D", "sampler2D", "sampler3D", "samplerCube",
        "sampler2DShadow", "sampler2DArray", "sampler2DArrayShadow", "samplerCubeShadow", "isampler2D", "usampler2D",
        "sampler2DMS", "samplerBuffer", "image2D", "radians", "degrees", "sin", "cos", "tan", "asin", "acos", "atan",
        "sinh", "cosh", "tanh", "pow", "exp", "log", "exp2", "log2", "sqrt", "inversesqrt", "abs", "sign", "floor",
        "trunc", "round", "roundEven", "ceil", "fract", "mod", "modf", "min", "max", "clamp", "mix", "step",
        "smoothstep", "isnan", "isinf", "fma", "length", "distance", "dot", "cross", "normalize", "faceforward",
        "reflect", "refract", "matrixCompMult", "outerProduct", "transpose", "determinant", "inverse", "lessThan",
        "lessThanEqual", "greaterThan", "greaterThanEqual", "equal", "notEqual", "any", "all", "not", "texture",
        "texture2D", "textureLod", "textureSize", "texelFetch", "textureOffset", "textureGrad", "textureProj",
        "textureGather", "dFdx", "dFdy", "fwidth", "floatBitsToInt", "intBitsToFloat", "packUnorm4x8",
        "unpackUnorm4x8", "packHalf2x16", "unpackHalf2x16", "bitfieldExtract", "findLSB", "findMSB", "main"
    };

    // A GLSL identifier that is no keyword, built-in or gl_/__ name
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaxName || char.IsAsciiDigit(name[0])) return false;

        for (var i = 0; i < Math.Min(name.Length, MaxName); i++)
        {
            if (!ShaderDocument.IsWord(name[i])) return false;
        }

        return !Reserved.Contains(name) && !name.StartsWith("gl_", StringComparison.Ordinal) &&
               !name.Contains("__", StringComparison.Ordinal);
    }

    // The text with every whole-word "from" outside comments and string literals replaced by "to"
    public static string ReplaceWord(string text, string from, string to, out int count)
    {
        count = 0;
        var output = new StringBuilder(text.Length);
        var end = Math.Min(text.Length, ShaderPaths.MaxSource * 4);
        var i = 0;
        for (var steps = 0; steps < Math.Min(end, ShaderPaths.MaxSource * 4) && i < end; steps++)
        {
            var skip = Skip(text, i, end);
            if (skip > i)
            {
                _ = output.Append(text, i, skip - i);
                i = skip;
                continue;
            }

            if (!ShaderDocument.IsWord(text[i]))
            {
                _ = output.Append(text[i++]);
                continue;
            }

            var start = i;
            for (var w = 0; w < MaxName * 4 && i < end && ShaderDocument.IsWord(text[i]); w++) i++;

            var word = text.AsSpan(start, i - start);
            if (word.SequenceEqual(from))
            {
                _ = output.Append(to);
                count++;
            }
            else
            {
                _ = output.Append(word);
            }
        }

        return count == 0 ? text : output.Append(text, i, text.Length - i).ToString();
    }

    // The end of a comment or string literal starting at i; i when none starts there
    private static int Skip(string text, int i, int end)
    {
        if (text[i] == '"')
        {
            var close = text.IndexOfAny(['"', '\n'], i + 1);
            return close < 0 ? end : close + 1;
        }

        if (text[i] != '/' || i + 1 >= end) return i;

        if (text[i + 1] == '/')
        {
            var line = text.IndexOf('\n', i);
            return line < 0 ? end : line;
        }

        if (text[i + 1] != '*') return i;

        var stop = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return stop < 0 ? end : stop + 2;
    }
}
