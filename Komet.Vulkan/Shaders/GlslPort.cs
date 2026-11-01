using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace Komet.Vulkan;

// Runs after the preprocessor. Storage buffers sit at their OpenGL binding + 40, uniform blocks + 56 (OpenGL has a
// namespace per kind, Vulkan one per set). Vertex outputs get locations in order, fragment inputs by name, as OpenGL's
// linker matches. Line count is kept so compiler messages point into the preprocessed source.
internal static partial class GlslPort
{
    public const int VertexBlock = 0, FragmentBlock = 1, FirstSampler = 8, MaxSamplers = 32, FirstStorage = 40,
        FirstUniformBlock = 56, MaxBinding = 16;
    private const int MaxStatements = 1 << 14, MaxNames = 64, MaxLength = 1 << 21, MaxLocations = 64;
    private const string HotMarker = "/*KOMET_HOT*/";

    public sealed record Uniform(string Name, string Type, int Count, float[]? Default, int Offset = -1,
        int Stride = 0);
    public sealed record Sampler(string Name, string Type, int Binding, int Count);
    public readonly record struct Attribute(string Name, string Type, int Location);
    public readonly record struct BlockBinding(string Name, bool Storage, int GlBinding, int Binding);
    private sealed record Declarator(string Name, string? Size, int Count, string? Init);

    private sealed class Context(IReadOnlyDictionary<string, int>? attributes)
    {
        // Loose uniforms put into the push constants both stages share (KometHot)
        public IReadOnlySet<string> Hot { get; init; } = new HashSet<string>(StringComparer.Ordinal);
        public Dictionary<string, Sampler> Samplers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Locations { get; } = new(StringComparer.Ordinal);
        public List<Attribute> Attributes { get; } = [];
        public List<BlockBinding> Blocks { get; } = [];
        public List<int> Outputs { get; } = [];
        public IReadOnlyDictionary<string, int>? Bound { get; } = attributes;
        public int NextLocation { get; set; }
        public string Error { get; set; } = "";
    }

    private static string? Stage(string text, bool vertex, Context context, List<Uniform> uniforms)
    {
        if (!Assert(text.Length < MaxLength)) return null;
        var output = new StringBuilder(text.Length + 4096);
        var (copied, blockAt) = (0, -1);
        foreach (var (start, end, brace) in Statements(text).Bounded(MaxStatements))
        {
            var statement = text[start..end];
            var loose = !brace && Loose(statement);
            var replaced = brace ? BlockHead(statement, context) : Declaration(statement, vertex, context, uniforms);
            if (replaced is null || context.Error.Length > 0) continue;
            _ = output.Append(text, copied, start - copied);
            if (loose && blockAt < 0) blockAt = output.Length;
            _ = output.Append(replaced);
            copied = end;
        }

        _ = output.Append(text, copied, text.Length - copied);
        if (context.Error.Length > 0) return null;
        if (blockAt >= 0) _ = output.Insert(blockAt, LooseBlocks(vertex, uniforms, context.Hot));
        var ported = Version.Replace(output.ToString(), Versioned, 1);
        if (!vertex) return ported;
        ported = VertexId.Replace(ported, m => m.Value == "gl_VertexID" ? "gl_VertexIndex" : "gl_InstanceIndex");
        return Sized(ported);
    }

    private static string LooseBlocks(bool vertex, List<Uniform> uniforms, IReadOnlySet<string> hot)
    {
        var cold = uniforms.Where(u => !hot.Contains(u.Name)).ToList();
        var block = new StringBuilder();
        if (cold.Count > 0)
        {
            _ = block.Append(CultureInfo.InvariantCulture,
                $"layout(std140, set = 0, binding = {(vertex ? VertexBlock : FragmentBlock)}) uniform ");
            _ = block.Append(vertex ? "KometVertex { " : "KometFragment { ");
            foreach (var u in cold.Bounded(MaxStatements))
                _ = block.Append(CultureInfo.InvariantCulture, $"{u.Type} {u.Name}{(u.Count > 1 ? $"[{u.Count}]" : "")}; ");
            _ = block.Append("}; ");
        }

        return (uniforms.Count > cold.Count ? block.Append(HotMarker) : block).ToString();
    }

    private static List<(int Start, int End, bool Brace)> Statements(string text)
    {
        var found = new List<(int, int, bool)>();
        var (depth, start, lineStart, directive) = (0, 0, true, false);
        for (var i = 0; i < Math.Min(text.Length, MaxLength); i++)
        {
            var c = text[i];
            if (directive)
            {
                if (c == '\n') (directive, start, lineStart) = (false, i + 1, true);
                continue;
            }

            if (c == '#' && lineStart && depth == 0)
            {
                directive = true;
                continue;
            }

            lineStart = c == '\n' || (lineStart && char.IsWhiteSpace(c));
            if (c == '{')
            {
                if (depth == 0) found.Add((start, i, true));
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0) start = i + 1;
            }
            else if (c == ';' && depth == 0)
            {
                found.Add((start, i + 1, false));
                start = i + 1;
            }
        }

        return found;
    }

    private static bool Loose(string statement)
    {
        var match = DeclarationPattern.Match(statement);
        return match.Success && Words(match).Contains("uniform") && !Opaque(match.Groups["type"].Value);
    }

    private static HashSet<string> Words(Match declaration)
    {
        var qualifiers = declaration.Groups["qualifiers"].Value;
        return [.. qualifiers.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Bounded(MaxNames)];
    }

    private static bool Opaque(string type) =>
        type.Contains("sampler", StringComparison.Ordinal) || type.Contains("image", StringComparison.Ordinal) ||
        type == "atomic_uint";

    // Replacement with as many lines, or null to keep it
    private static string? Declaration(string statement, bool vertex, Context context, List<Uniform> uniforms)
    {
        var match = DeclarationPattern.Match(statement);
        if (!match.Success) return null;
        var (words, type, layout) = (Words(match), match.Groups["type"].Value, match.Groups["layout"].Value);
        var names = Declarators(match.Groups["names"].Value, context);
        if (match.Groups["size"].Success) names = SizedByType(names, match.Groups["size"].Value, context);
        var lines = new string('\n', statement.Count(c => c == '\n'));
        var leading = match.Groups["space"].Value.Count(c => c == '\n');
        var after = new string('\n', statement.Count(c => c == '\n') - leading);
        if (words.Contains("uniform") && !Opaque(type))
        {
            foreach (var d in names.Bounded(MaxNames))
                uniforms.Add(new Uniform(d.Name, type, d.Count, Initial(d.Init, type, d.Name, context)));
            return lines;
        }

        var qualifiers = Spaces.Replace(match.Groups["qualifiers"].Value.Trim(), " ");
        var ported = new StringBuilder(match.Groups["space"].Value);
        foreach (var d in names.Bounded(MaxNames))
        {
            var binding = words.Contains("uniform") ? SamplerBinding(d, type, context) : LocationOf(d, type, layout,
                (vertex, words.Contains("out")), context);
            var array = d.Size is null ? "" : $"[{d.Count}]";
            var qualified = words.Contains("uniform") ? "uniform" : qualifiers;
            _ = ported.Append(CultureInfo.InvariantCulture,
                $"layout({Layout(layout, binding)}) {qualified} {type} {d.Name}{array}; ");
        }

        return ported.Append(after).ToString();
    }

    // float[4] a, b: the size on the type, for every name
    private static List<Declarator> SizedByType(List<Declarator> names, string size, Context context)
    {
        var count = ArraySize(size);
        if (count < 1) context.Error = $"the array size {size} is no constant";
        return [.. names.Select(d => d with { Size = size, Count = Math.Max(count, 1) })];
    }

    private static List<Declarator> Declarators(string names, Context context)
    {
        var found = new List<Declarator>();
        var (depth, start) = (0, 0);
        for (var i = 0; i <= Math.Min(names.Length, MaxLength); i++)
        {
            var c = i < names.Length ? names[i] : ',';
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            if (c != ',' || depth != 0) continue;
            var part = DeclaratorPattern.Match(names[start..Math.Min(i, names.Length)]);
            start = i + 1;
            if (!Assert(found.Count < MaxNames) || !part.Success)
            {
                context.Error = $"no port for the declaration of {names.Trim()}";
                return found;
            }

            var size = part.Groups["size"].Success ? part.Groups["size"].Value : null;
            var count = size is null ? 1 : ArraySize(size);
            if (count < 1) context.Error = $"the array size of {part.Groups["name"].Value} is no constant: {size}";
            var init = part.Groups["init"].Success ? part.Groups["init"].Value.Trim() : null;
            found.Add(new Declarator(part.Groups["name"].Value, size, Math.Max(count, 1), init));
        }

        return found;
    }

    // Sums and products of integers (what the engine's macros leave); -1 otherwise
    private static int ArraySize(string size)
    {
        var tokens = SizeToken.Matches(size);
        var spelled = size.Count(c => !char.IsWhiteSpace(c));
        if (tokens.Sum(t => t.Length) != spelled) return -1;
        var (sum, term, add) = (0L, 1L, true);
        for (var i = 0; i < Math.Min(tokens.Count, MaxNames); i++)
        {
            var token = tokens[i].Value;
            if (i % 2 == 0 && long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                term *= value;
            else if (i % 2 == 1 && token is "+" or "*") (sum, term) = token == "+" ? (sum + term, 1L) : (sum, term);
            else add = false;
        }

        return add && tokens.Count % 2 == 1 && sum + term is > 0 and < MaxLength ? (int)(sum + term) : -1;
    }
}

internal static partial class GlslPort
{
    public sealed record Block(int Binding, int Size, Uniform[] Members);

    public const int HotBlock = -1, MaxHotBytes = 256;

    // SPIR-V words of both stages, cached as base64 (ShaderCache)
    public sealed record Ported(string Name, [property: JsonConverter(typeof(SpirvWords))] uint[] Vertex,
        [property: JsonConverter(typeof(SpirvWords))] uint[] Fragment)
    {
        public required Block VertexUniforms { get; init; }
        public required Block FragmentUniforms { get; init; }

        public Block HotUniforms { get; init; } = new(HotBlock, 0, []);
        public required Sampler[] Samplers { get; init; }
        public required BlockBinding[] Blocks { get; init; }
        public required Attribute[] Attributes { get; init; }
        public required int[] Outputs { get; init; }
    }

    // attributes: glBindAttribLocation locations; hot: loose uniforms changing nearly every draw, pushed
    public static Ported? Build(string name, string vertex, string fragment,
        IReadOnlyDictionary<string, int>? attributes, out string error, IReadOnlySet<string>? hot = null) =>
        Build(name, (vertex, fragment), attributes, out error, out _, hot);

    // translated: the GLSL both stages were compiled from (tests read it; a port keeps only its SPIR-V)
    public static Ported? Build(string name, (string Vertex, string Fragment) stages,
        IReadOnlyDictionary<string, int>? attributes, out string error, out (string Vertex, string Fragment) translated,
        IReadOnlySet<string>? hot = null)
    {
        (error, translated) = ("", ("", ""));
        var (vertex, fragment) = stages;
        if (!NotNull(vertex) || !NotNull(fragment) || !Assert(name.Length > 0)) return null;
        var context = new Context(attributes) { Hot = hot ?? new HashSet<string>(StringComparer.Ordinal) };
        var (vertexUniforms, fragmentUniforms) = (new List<Uniform>(), new List<Uniform>());
        var vertexText = Translate(vertex, true, name, context, vertexUniforms);
        var fragmentText = vertexText is null ? null : Translate(fragment, false, name, context, fragmentUniforms);
        error = context.Error;
        if (vertexText is null || fragmentText is null) return null;
        var pushed = Pushed(vertexUniforms, fragmentUniforms, context.Hot);
        (vertexText, fragmentText) = (vertexText.Replace(HotMarker, HotDeclaration(pushed), StringComparison.Ordinal),
            fragmentText.Replace(HotMarker, HotDeclaration(pushed), StringComparison.Ordinal));
        var v = Shaderc.Compile(vertexText, Shaderc.Vertex, name + ".vsh", out error, relaxed: false);
        var f = v is null
            ? null
            : Shaderc.Compile(fragmentText, Shaderc.Fragment, name + ".fsh", out error, relaxed: false);
        if (v is null || f is null) return null;
        translated = (vertexText, fragmentText);
        var declaring = Spirv.Members(v, "KometHot").Length > 0 ? v : f; // a stage that declares the push constants
        var hotBlock = pushed.Count > 0 ? Laid(declaring, "KometHot", HotBlock, pushed) : new Block(HotBlock, 0, []);
        if (hotBlock.Size > MaxHotBytes)
        {
            error = $"the hot uniforms take {hotBlock.Size} bytes, more than push constants hold";
            return null;
        }

        return new Ported(name, v, f)
        {
            VertexUniforms = Laid(v, "KometVertex", VertexBlock,
                [.. vertexUniforms.Where(u => !context.Hot.Contains(u.Name))]),
            FragmentUniforms = Laid(f, "KometFragment", FragmentBlock,
                [.. fragmentUniforms.Where(u => !context.Hot.Contains(u.Name))]),
            HotUniforms = hotBlock,
            Samplers = [.. context.Samplers.Values.OrderBy(s => s.Binding)], Blocks = [.. context.Blocks],
            Attributes = [.. context.Attributes], Outputs = [.. context.Outputs.Order()]
        };
    }

    private static List<Uniform> Pushed(List<Uniform> vertex, List<Uniform> fragment, IReadOnlySet<string> hot)
    {
        var pushed = new List<Uniform>();
        if (hot.Count == 0) return pushed;
        foreach (var u in vertex.Concat(fragment).ToArray().Bounded(MaxStatements))
            if (hot.Contains(u.Name) && !pushed.Exists(p => p.Name == u.Name))
                pushed.Add(u);
        return pushed;
    }

    // One line, identical in both stages (members a stage leaves unread too)
    private static string HotDeclaration(List<Uniform> pushed)
    {
        if (pushed.Count == 0) return "";
        var members = string.Join(" ", pushed.Select(u => $"{u.Type} {u.Name}{(u.Count > 1 ? $"[{u.Count}]" : "")};"));
        return $"layout(push_constant) uniform KometHot {{ {members} }}; "; // std430, as push constants are laid out
    }

    private static string? Translate(string source, bool vertex, string name, Context context, List<Uniform> uniforms)
    {
        var (kind, file) = vertex ? (Shaderc.Vertex, name + ".vsh") : (Shaderc.Fragment, name + ".fsh");
        var text = Shaderc.Preprocess(source, kind, file, out var error);
        if (text is not null) return Stage(text, vertex, context, uniforms);
        context.Error = $"{file}: {error}";
        return null;
    }

    private static Block Laid(uint[] words, string block, int binding, List<Uniform> uniforms)
    {
        var members = Spirv.Members(words, block);
        var laid = new Uniform[uniforms.Count];
        var size = 0;
        for (var i = 0; i < Math.Min(laid.Length, MaxStatements); i++)
        {
            var u = uniforms[i];
            var m = i < members.Length && members[i].Name == u.Name ? members[i] : new Spirv.Member(u.Name, -1, 0);
            laid[i] = u with { Offset = m.Offset, Stride = m.Stride };
            size = Math.Max(size, m.Offset + (u.Count > 1 ? m.Stride * u.Count : Bytes(u.Type)));
        }

        _ = Assert(laid.All(u => u.Offset >= 0));
        return new Block(binding, (size + 15) / 16 * 16, laid);
    }

    // SPIR-V words as base64 of their bytes: a third of the decimal JSON array's size
    private sealed class SpirvWords : JsonConverter<uint[]>
    {
        public override uint[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var bytes = reader.GetBytesFromBase64();
            if (bytes.Length % sizeof(uint) != 0)
                throw new JsonException("SPIR-V bytes not whole words");
            return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
        }

        public override void Write(Utf8JsonWriter writer, uint[] value, JsonSerializerOptions options)
        {
            writer.WriteBase64StringValue(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }
}

internal static partial class GlslPort
{
    private static readonly Regex DeclarationPattern = new(@"^(?<space>\s*)(?:layout\s*\((?<layout>[^)]*)\)\s*)?" +
                    @"(?<qualifiers>(?:(?:uniform|in|out|flat|smooth|noperspective|centroid|invariant|" +
                    @"highp|mediump|lowp)\s+)+)" +
                    @"(?<type>\w+)\s*(?:\[(?<size>[^\]]*)\])?\s+(?<names>[^;]+);$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex DeclaratorPattern = new(@"^\s*(?<name>\w+)\s*(?:\[(?<size>[^\]]*)\])?\s*(?:=(?<init>.+))?$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex BlockPattern = new(@"^(?<space>\s*)(?:layout\s*\((?<layout>[^)]*)\)\s*)?" +
                    @"(?<qualifiers>(?:(?:readonly|writeonly|restrict|coherent|volatile)\s+)*)" +
                    @"(?<kind>buffer|uniform)\s+" +
                    @"(?<name>\w+)\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex InterfaceBlockPattern = new(@"^\s*(?:layout\s*\([^)]*\)\s*)?(?:\w+\s+)*(?:in|out)\s+\w+\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex Spaces = new(@"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex SizeToken = new(@"\d+|[+*]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex Unshared = new(@"\b(?:shared|packed)\b\s*,?",
        RegexOptions.None | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private static readonly Regex Assigned = new(@"\b(?:binding|location|set)\s*=\s*\d+\s*,?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex Given = new(@"\b(?<key>binding|location)\s*=\s*(?<value>\d+)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex Version = new(@"^#version[ \t]+(?<number>\d+)(?:[ \t]+(?:core|compatibility))?[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex VertexId = new(@"\bgl_(?:VertexID|InstanceID)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    private static readonly Regex MainPattern = new(@"\bvoid\s+main\s*\(\s*(?:void\s*)?\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, Regex.InfiniteMatchTimeout);

    // Vertex stages without gl_PointSize write OpenGL's default 1 (undefined in Vulkan): main renamed and called from a
    // one-line main appended at the end, so line numbers stay
    private static string Sized(string ported)
    {
        if (ported.Contains("gl_PointSize", StringComparison.Ordinal)) return ported;
        var renamed = MainPattern.Replace(ported, "void komet_main()", 1);
        return !ReferenceEquals(renamed, ported)
            ? renamed + "\nvoid main() { komet_main(); gl_PointSize = 1.0; }\n"
            : ported;
    }

    // Version kept (newer reserve words like sample); below 4.20 two extensions allow set/binding and stage locations
    private static string Versioned(Match version)
    {
        var number = int.Parse(version.Groups["number"].Value, CultureInfo.InvariantCulture);
        if (number >= 420) return version.Value;
        return $"#version {Math.Max(number, 140)}{(number >= 150 ? " core" : "")}\n" +
               "#extension GL_ARB_shading_language_420pack : enable\n" +
               "#extension GL_ARB_separate_shader_objects : enable\n#line 2";
    }

    private static string? BlockHead(string head, Context context)
    {
        if (InterfaceBlockPattern.IsMatch(head)) context.Error = $"no port for the interface block {head.Trim()}";
        var match = BlockPattern.Match(head);
        if (!match.Success || !Assert(context.Blocks.Count < MaxBinding * 2)) return null;
        var (layout, storage) = (match.Groups["layout"].Value, match.Groups["kind"].Value == "buffer");
        var gl = GivenValue(layout, "binding");
        if (!Index(Math.Max(gl, 0), MaxBinding))
        {
            context.Error = $"the binding of {match.Groups["name"].Value} is out of range";
            return null;
        }

        var binding = (storage ? FirstStorage : FirstUniformBlock) + Math.Max(gl, 0);
        context.Blocks.Add(new BlockBinding(match.Groups["name"].Value, storage, Math.Max(gl, 0), binding));
        var packed = layout.Contains("std140", StringComparison.Ordinal) ||
                     layout.Contains("std430", StringComparison.Ordinal);
        var packing = storage ? "std430, " : "std140, ";
        if (packed) packing = "";
        var unshared = Unshared.Replace(layout, "");
        var args = Layout(unshared, $"{packing}set = 0, binding = {binding}");
        var (space, qualifiers) = (match.Groups["space"].Value, match.Groups["qualifiers"].Value);
        return $"{space}layout({args}) {qualifiers}{match.Groups["kind"].Value} {match.Groups["name"].Value} ";
    }

    private static int GivenValue(string layout, string key)
    {
        var given = Given.Matches(layout).FirstOrDefault(m => m.Groups["key"].Value == key);
        return given is null ? -1 : int.Parse(given.Groups["value"].Value, CultureInfo.InvariantCulture);
    }

    private static string Layout(string original, string assigned)
    {
        var rest = Assigned.Replace(original, "").Trim().TrimEnd(',').Trim();
        return rest.Length == 0 ? assigned : $"{rest}, {assigned}";
    }

    private static string SamplerBinding(Declarator d, string type, Context context)
    {
        if (!context.Samplers.TryGetValue(d.Name, out var sampler))
        {
            var next = context.Samplers.Values.Sum(s => s.Count);
            if (next + d.Count > MaxSamplers) context.Error = "more samplers than the port has bindings for";
            sampler = new Sampler(d.Name, type, FirstSampler + next, d.Count);
            context.Samplers[d.Name] = sampler;
        }

        if (sampler.Type != type || sampler.Count != d.Count) context.Error = $"{d.Name} differs between the stages";
        return $"set = 0, binding = {sampler.Binding}";
    }

    private static string LocationOf(Declarator d, string type, string layout, (bool Vertex, bool Out) side,
        Context context)
    {
        var size = Shape(type).Columns * d.Count;
        if (size < 1) context.Error = $"no port for the type of {d.Name}: {type}";
        var location = GivenValue(layout, "location");
        switch (side)
        {
            case (true, false) when location < 0 && !(context.Bound?.TryGetValue(d.Name, out location) ?? false):
                context.Error = $"the attribute {d.Name} has no location";
                break;
            case (true, false):
                context.Attributes.Add(new Attribute(d.Name, type, location));
                break;
            case (false, true):
                if (location < 0) location = context.Outputs.Count == 0 ? 0 : context.Outputs.Max() + 1;
                context.Outputs.Add(location);
                break;
            case (true, true) or (false, false)
                when location < 0 && context.Locations.TryGetValue(d.Name, out var known):
                location = known;
                break;
            default:
                location = location >= 0 ? location : context.NextLocation;
                context.Locations[d.Name] = location;
                context.NextLocation = Math.Max(context.NextLocation, location + Math.Max(size, 1));
                break;
        }

        if (context.NextLocation > MaxLocations && context.Error.Length == 0)
            context.Error = "more interface locations than a stage has";
        return $"location = {Math.Max(location, 0)}";
    }

    // A location per column, 16 bytes per matrix column in std140; (0, 0) when unknown
    internal static (int Columns, int Rows) Shape(string type)
    {
        if (type is "float" or "int" or "uint" or "bool") return (1, 1);
        var tail = type.Length > 1 && char.IsAsciiDigit(type[^1]) ? type[^1] - '0' : 0;
        if (type is ['v', 'e', 'c', _] or ['i' or 'u' or 'b', 'v', 'e', 'c', _]) return (1, tail);
        return type is ['m', 'a', 't', _] && tail is >= 2 and <= 4 ? (tail, tail) : (0, 0);
    }

    // One input per matrix column, each with its own OpenGL pointer (instanced mat4 at 5 reads 5..8), or columns past
    // the first read nothing
    public static Attribute[] Columns(Attribute[] inputs)
    {
        if (!NotNull(inputs) || Array.TrueForAll(inputs, static a => Shape(a.Type).Columns <= 1)) return inputs;
        var columns = new List<Attribute>(inputs.Length + 6);
        foreach (var a in inputs.Bounded(MaxLocations))
        {
            var (count, rows) = Shape(a.Type);
            if (count <= 1) columns.Add(a);
            else
                for (var c = 0; c < Math.Min(count, 4); c++)
                    columns.Add(new Attribute($"{a.Name}[{c}]", $"vec{rows}", a.Location + c));
        }

        return [.. columns];
    }

    // std140 element size: vec3 12 bytes, a matrix a vec4 per column
    public static int Bytes(string type)
    {
        var (columns, rows) = Shape(type);
        return columns > 1 ? 16 * columns : 4 * rows;
    }

    // A float per component (a matrix's diagonal for a single value); null without an initializer
    private static float[]? Initial(string? init, string type, string name, Context context)
    {
        if (init is null) return null;
        var (columns, rows) = Shape(type);
        var open = init.IndexOf('(', StringComparison.Ordinal);
        var inner = init.EndsWith(')') && open > 0 ? init[(open + 1)..^1] : init;
        var parts = inner.Split(',', StringSplitOptions.TrimEntries);
        var values = new float[Math.Max(columns * rows, 1)];
        for (var i = 0; i < Math.Min(Math.Min(parts.Length, values.Length), 16); i++)
        {
            var part = parts[i] switch { "true" => "1", "false" => "0", var p => p.TrimEnd('f', 'F', 'u', 'U') };
            if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                context.Error = $"no port for the default of {name}: {init}";
        }

        if (parts.Length != 1)
        {
            if (parts.Length != values.Length) context.Error = $"no port for the default of {name}: {init}";
            return values;
        }

        // one value fills every component, or a matrix's diagonal
        for (var i = 1; i < Math.Min(values.Length, 16); i++)
            values[i] = columns > 1 && i % (rows + 1) != 0 ? 0 : values[0];
        return values;
    }
}
