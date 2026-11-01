using HarmonyLib;
using Komet.Rendering;
using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Vulkan;

// Stages read back from the driver; while GlTap records (VulkanRenderer.Only), from what the engine gave the driver
internal static class ProgramPorts
{
    private const int MaxShaders = 8, MaxAttributes = 32, MaxBlocks = 32;
    private const int VertexShader = 0x8B31, FragmentShader = 0x8B30;

    private static readonly Dictionary<int, string> Names = [];
    // Ports begun as the world loads (VulkanRenderer.EarlyLinked), before Vulkan-only's first frame; Started hands them
    // over to the draw or the terrain that asks for the same program. Main thread.
    private static readonly Dictionary<int, (Build<GlslPort.Ported> Build, bool Hot)> Early = [];
    private const int MaxEarly = 256;
    private static readonly System.Reflection.FieldInfo? Registered = AccessTools.Field(typeof(ShaderRegistry), "shaderPrograms");

    // Read on the engine's thread and never written after, so a builder may port it
    public sealed record Stages(string Named, string Vertex, string Fragment, IReadOnlyDictionary<string, int> Bound);

    public static GlslPort.Ported? Port(int program, string name, out string error, IReadOnlySet<string>? hot = null,
        bool settled = false)
    {
        var source = Read(program, name, out error);
        return source is null ? null : Made(source, out error, hot, settled);
    }

    // Stages read back here; shaderc and cache on a builder, which owns hot and the source
    public static Build<GlslPort.Ported>? Started(int program, string name, out string error, IReadOnlySet<string>? hot = null,
        bool settled = false)
    {
        error = "";
        if (!settled && Early.Remove(program, out var early))
        {
            if (early.Hot == (hot is not null)) return early.Build; // the same port, begun while the world loaded
            early.Build.Drop();
        }

        var source = Read(program, name, out error);
        if (source is null) return null;
        var copied = hot is null ? null : new HashSet<string>(hot, StringComparer.Ordinal);
        return new Build<GlslPort.Ported>(Builds.Kind.Port, () => (Made(source, out var why, copied, settled), why));
    }

    // A program linked while the world loads: its port begins now on a builder, so the first Vulkan-only frame finds it made
    // instead of waiting for it (a join waited for 25 ports, up to 170 ms each, otherwise)
    public static void Prestart(int program, string name, IReadOnlySet<string>? hot)
    {
        if (program <= 0 || !NotNull(name) || Early.Count >= MaxEarly || Early.ContainsKey(program)) return;
        if (Started(program, name, out _, hot) is { } build) Early[program] = (build, hot is not null);
    }

    // The world goes, or Vulkan-only stops: ports nobody will take
    public static void DropEarly()
    {
        foreach (var (_, early) in Early.Bounded(MaxEarly)) early.Build.Drop();
        Early.Clear();
    }

    public static Stages? Read(int program, string name, out string error)
    {
        error = "";
        if (!Assert(program > 0) || !Assert(name.Length > 0)) return null;
        if (GlTap.LinkedOf(program) is { } linked) return Recorded(linked, name, out error);
        GL.GetProgram(program, GetProgramParameterName.AttachedShaders, out int count);
        var shaders = new int[Math.Clamp(count, 0, MaxShaders)];
        if (shaders.Length > 0) GL.GetAttachedShaders(program, shaders.Length, out _, shaders);
        var (vertex, fragment) = ("", "");
        foreach (var shader in shaders.Bounded(MaxShaders))
        {
            GL.GetShader(shader, ShaderParameter.ShaderType, out int type);
            if (type == VertexShader) vertex = Source(shader);
            else if (type == FragmentShader) fragment = Source(shader);
            else
            {
                error = $"a stage Vulkan draws no port of (0x{type:X})";
                return null;
            }
        }

        if (vertex.Length == 0 || fragment.Length == 0)
        {
            error = "no vertex or fragment source";
            return null;
        }

        return new Stages(name, vertex, fragment, Attributes(program));
    }

    // Locations from glBindAttribLocation: inputs with neither these nor a layout location have no port
    private static Stages? Recorded(GlTap.Linked linked, string name, out string error)
    {
        error = "";
        if (linked.Other != 0) error = $"a stage Vulkan draws no port of (0x{linked.Other:X})";
        else if (linked.Vertex.Length == 0 || linked.Fragment.Length == 0) error = "no vertex or fragment source";
        return error.Length == 0 ? new Stages(name, linked.Vertex, linked.Fragment, linked.Attributes) : null;
    }

    // No hot: what the scene settled on earlier for these sources; settled: hot is kept for the next start. Any thread.
    public static GlslPort.Ported? Made(Stages source, out string error, IReadOnlySet<string>? hot = null, bool settled = false)
    {
        var ported = Making(source, out error, hot, settled);
        if (source is { Named.Length: > 0 }) ShaderMods.Ported[source.Named] = ported is null ? error : ""; // Active shaders' status
        return ported;
    }

    private static GlslPort.Ported? Making(Stages source, out string error, IReadOnlySet<string>? hot, bool settled)
    {
        error = "";
        if (!NotNull(source) || !Assert(source.Named.Length > 0)) return null;
        var (name, vertex, fragment, attributes) = (source.Named, source.Vertex, source.Fragment, source.Bound);
        var sources = ShaderCache.Active ? ShaderCache.Sources(vertex, fragment, attributes) : "";
        if (sources.Length == 0) return GlslPort.Build(name, vertex, fragment, attributes, out error, hot);
        var remembered = hot is null ? ShaderCache.Settled(sources) : null;
        var ported = Cached(sources, source, remembered ?? hot, out error);
        // the settled ones no longer fit: as the scene sees it first
        if (ported is null && remembered is not null) ported = Cached(sources, source, hot, out error);
        // kept only for a port that succeeded, so a failing hot set is not tried again at the next start
        if (ported is not null && settled && hot is not null) ShaderCache.Settle(sources, hot);
        return ported;
    }

    private static GlslPort.Ported? Cached(string sources, Stages source, IReadOnlySet<string>? hot, out string error)
    {
        error = "";
        if (ShaderCache.Port(sources, hot, source.Named) is { } cached) return cached;
        var ported = GlslPort.Build(source.Named, source.Vertex, source.Fragment, source.Bound, out error, hot);
        if (ported is not null) ShaderCache.Keep(sources, hot, ported);
        return ported;
    }

    private static string Source(int shader)
    {
        if (!Assert(shader > 0)) return "";
        GL.GetShader(shader, ShaderParameter.ShaderSourceLength, out var length);
        if (!Assert(length >= 0) || length == 0) return "";
        GL.GetShaderSource(shader, length, out _, out var source);
        return NotNull(source) ? source : "";
    }

    private static Dictionary<string, int> Attributes(int program)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        GL.GetProgram(program, GetProgramParameterName.ActiveAttributes, out int count);
        for (var i = 0; i < Math.Min(count, MaxAttributes); i++)
        {
            var name = GL.GetActiveAttrib(program, i, out _, out _);
            var location = GL.GetAttribLocation(program, name);
            if (location >= 0 && NotNull(name)) found[name] = location;
        }

        return found;
    }

    // Current binding per block (glUniformBlockBinding may change it), -1 if unused
    public static int[] Points(int program, GlslPort.Ported ported)
    {
        if (!Assert(program > 0) || !NotNull(ported)) return [];
        var points = new int[Math.Min(ported.Blocks.Length, MaxBlocks)];
        for (var i = 0; i < Math.Min(points.Length, MaxBlocks); i++)
        {
            var block = ported.Blocks[i];
            if (block.Storage)
            {
                var index = GL.GetProgramResourceIndex(program, ProgramInterface.ShaderStorageBlock, block.Name);
                ProgramProperty[] property = [ProgramProperty.BufferBinding];
                var value = new int[1];
                if (index != -1)
                    GL.GetProgramResource(program, ProgramInterface.ShaderStorageBlock, index, 1, property, 1, out _,
                        value);
                points[i] = index == -1 ? -1 : value[0];
                continue;
            }

            if (GlTap.BlockPointOf(program, block.Name, block.GlBinding) is { } recorded)
            {
                points[i] = recorded;
                continue;
            }

            var uniform = GL.GetUniformBlockIndex(program, block.Name);
            points[i] = -1;
            if (uniform < 0) continue;
            GL.GetActiveUniformBlock(program, uniform, ActiveUniformBlockParameter.UniformBlockBinding, out int point);
            points[i] = point;
        }

        return points;
    }

    // The engine's pass name, else the number; asked of the registry once per program
    public static string Name(int program)
    {
        if (program <= 0) return "no program";
        if (Names.TryGetValue(program, out var name)) return name;
        var programs = Registered?.GetValue(null) as ShaderProgram[] ?? [];
        if (!Assert(programs.Length < 1 << 16)) return $"program {program}";
        for (var i = 0; i < Math.Min(programs.Length, 1 << 16); i++)
            if (programs[i] is { ProgramId: > 0, PassName: { } pass } known) _ = Names.TryAdd(known.ProgramId, pass);
        name = $"program {program}"; // not the engine's: a mod's own
        return Names.TryAdd(program, name) ? name : Names[program];
    }

    // Every program the registry holds linked now (id, pass name): what was linked before Vulkan-only began
    public static (int Id, string Name)[] Linked()
    {
        var programs = Registered?.GetValue(null) as ShaderProgram[] ?? [];
        if (!Assert(programs.Length < 1 << 16)) return [];
        List<(int, string)> linked = [];
        for (var i = 0; i < Math.Min(programs.Length, 1 << 16); i++)
            if (programs[i] is { ProgramId: > 0, PassName: { Length: > 0 } pass } known)
                linked.Add((known.ProgramId, pass));
        return [.. linked];
    }

    // Its name may come back for another
    public static void Forget(int program)
    {
        if (Early.Remove(program, out var early)) early.Build.Drop(); // relinked: the old sources' port
        _ = Names.Remove(program);
    }
}

// ShaderMods' port reader (Modules.Wire): the sources now, the port as work for a thread
internal static class VulkanPorts
{
    public static (Action? Port, string Error) Read(int program, string pass)
    {
        if (!NotNull(pass) || !Assert(program >= 0)) return (null, "no program");
        var stages = ProgramPorts.Read(program, pass, out var error);
        return stages is null ? (null, error) : (() => _ = ProgramPorts.Made(stages, out _), "");
    }
}
