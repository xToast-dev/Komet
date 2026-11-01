using Komet.Diagnostics;
using System.Numerics;

namespace Komet.Vulkan;

// Every GL draw, clear and blit outside the terrain passes; what it cannot take is missing from the picture.
internal sealed partial class SceneRenderer : IGlWrites, IDisposable
{
    private const int MaxUnported = 1024, MaxScenePrograms = 256;

    private readonly TerrainRenderer _terrain;
    private readonly ILogger? _logger;
    private readonly Dictionary<int, Drawn> _programs = [];
    private readonly Dictionary<int, string> _unported = [];
    private readonly Dictionary<int, Build<GlslPort.Ported>> _porting = [];
    private int _ports; // ports drawn with so far, each a number of its own

    private sealed class Drawn(TerrainRenderer.Program program, int[] points)
    {
        public TerrainRenderer.Program Program { get; } = program;
        public int[] Points { get; } = points;
        public string Name => Program.Name;
        public (Formats Inputs, uint Topology, VulkanFrame.Target Target, GlTap.Fixed State, TerrainPipeline? Made)? Last { get; set; }
        public (GlTap.VertexArray Array, long Changes, Layout? Layout)? Laid { get; set; }

        public TerrainDraw.Texture[] Resolved { get; set; } = [];
        public TerrainDraw.Program? Recorded { get; set; }

        // Hot uniforms settled (watched over first draws, or came with them)
        public bool Settled { get; set; }

        // Since settling: draws and mirror packs at the last count, and re-watch rounds
        public int Draws { get; set; }
        public long PacksAt { get; set; }
        public long FrameAt { get; set; }
        public int Rounds { get; set; }

        // Port with the settled hot uniforms being made, and their names
        public (Build<GlslPort.Ported> Port, string Hot)? Again { get; set; }

        // Port in use (PipelineKey), and the next one, made, with its pipelines being made
        public int Port { get; init; }

        // ShaderCache key of its pipelines (SceneRenderer.Warm), "" for none
        public string Sources { get; init; } = "";
        public (GlslPort.Ported Ported, int Port, PipelineKey[] Pipelines)? Next { get; set; }

        // Last texture resolve key and its images: reused while nothing changed
        public Versions Key { get; set; }
        public SharedImage[] Images { get; set; } = [];

        // Reused across other binds while units hold the same textures and samplers (Bound) and nothing was deleted,
        // set or changed (Held)
        public Versions Held { get; set; }
        public (int Unit, uint Kind, uint Texture, int Sampler)[] Bound { get; set; } = [];

        // Texture kind per sampler, and resolves by unit contents, each valid while its Held is the program's (units cycle
        // among a few textures)
        public uint[] Kinds { get; } = [.. program.Ported.Samplers.Select(s => TargetOf(s.Type))];
        public Dictionary<Units, Seen> Seen { get; } = [];
    }

    private SceneRenderer(TerrainRenderer terrain, ILogger? logger, HostBuffer constants)
    {
        (_terrain, _logger, Constants) = (terrain, logger, constants);
        Mirrors = new BufferMirrors(terrain.Device, terrain.Frame)
        {
            Mapped = terrain.Maps.Read, Early = terrain.Images, Pooled = terrain.Pools.Range
        };
        terrain.Pools.Held = Mirrors.Taken;
        _pipelines = new PipelineSet<PipelineKey>(terrain.Device, terrain.Frame, logger, MaxPipelines);
    }

    public static SceneRenderer? Create(TerrainRenderer terrain, ILogger? logger)
    {
        if (!NotNull(terrain) || HostBuffer.Create(terrain.Device, 64) is not { } constants) return null;
        _ = constants.Take(32, out var into, 16);
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(into);
        (words[3], words[7]) = (BitConverter.SingleToUInt32Bits(1), 1); // OpenGL's (0, 0, 0, 1), as floats, then as ints
        return new SceneRenderer(terrain, logger, constants);
    }

    public BufferMirrors Mirrors { get; }

    // (0, 0, 0, 1) as floats at 0 and integers at 16: what a disabled attribute reads
    private HostBuffer Constants { get; }

    public System.Func<int, string, bool> Takes { get; set; } = (_, _) => false;

    // Draws and clears are taken only between frame start and end
    public bool Framing { get; set; }

    public string Unported => string.Join("; ", ((IEnumerable<string>)_unported.Values).Bounded(16));

    public long Draws { get; private set; }
    public long Clears { get; private set; }
    public long Left { get; private set; }

    public long Ticks { get; private set; }

    private Drawn? ProgramFor(int id, out string why)
    {
        why = "";
        if (id <= 0) return Refused(out why, "no program in use");
        if (_programs.TryGetValue(id, out var known)) return known;
        if (_unported.TryGetValue(id, out var failed)) return Refused(out why, failed);
        var name = ProgramPorts.Name(id);
        var program = _terrain.Find(id);
        if (program is null)
        {
            // the cap first: no port is started for a program that could not be drawn
            if (_programs.Count >= MaxScenePrograms)
                return Unportable(id, $"{name} (past the cap of {MaxScenePrograms} programs drawn)", out why);
            var ported = Ported(id, name, out var waiting, out var error);
            if (waiting) return null;
            if (ported is null) return Unportable(id, $"{name} (no Vulkan port: {error})", out why);

            // hot uniforms settle over the first draws unless the port has an earlier start's
            program = _terrain.Watch(name, id, ported, measuring: ported.HotUniforms.Size == 0);
            if (program is null) return Refused(out why, $"{name} (no room to watch another program)");
        }

        var drawn = new Drawn(program, ProgramPorts.Points(id, program.Ported))
        {
            Settled = program.Ported.HotUniforms.Size > 0, // the terrain's own, ported with its hot uniforms, or cached ones
            Port = ++_ports, Sources = PipelineRecipes.SourcesOf(id)
        };
        _programs[id] = drawn;
        Warm(drawn);
        return drawn;
    }

    // Read back here, ported on a builder: waiting until done
    private GlslPort.Ported? Ported(int id, string name, out bool waiting, out string error)
    {
        (waiting, error) = (false, "");
        if (!Assert(_porting.Count <= MaxScenePrograms)) return null;
        if (!_porting.TryGetValue(id, out var porting))
        {
            var start = Hitches.Now;
            porting = ProgramPorts.Started(id, name, out error);
            Hitches.Since(Hitches.Kind.Port, start);
            if (porting is null) return null;
            _porting[id] = porting;
        }

        if (!porting.Done && !porting.Awaited(Hitches.Kind.Port)) // waits: the draw would be missing
        {
            waiting = true;
            return null;
        }

        _ = _porting.Remove(id);
        var ported = porting.Take(out error);
        _logger?.Notification("Komet: Vulkan port of {0}: {1}", name,
            ported is null ? error : $"{FrameClock.ToMs(porting.Ticks):0} ms {porting.Where}");
        return ported;
    }

    // Refused for good, until the GL name is forgotten
    private Drawn? Unportable(int id, string reason, out string why)
    {
        if (_unported.Count < MaxUnported) _unported[id] = reason;
        return Refused(out why, reason);
    }

    private static Drawn? Refused(out string why, string reason)
    {
        why = reason;
        return null;
    }

    // The GL name may return for another program. keep: a port whose pipelines stay
    public void Forget(int id, int keep = 0)
    {
        if (!Assert(id >= 0) || !Assert(_programs.Count <= MaxScenePrograms)) return;
        _ = _unported.Remove(id);
        if (_porting.Remove(id, out var porting)) porting.Drop();
        if (!_programs.Remove(id, out var gone)) return;
        gone.Again?.Port.Drop();
        ForgetPipelines(id, keep);
        _terrain.Forget(id);
    }

    // OpenGL's picture is not shown, so the draw is missing
    private bool Leave(string why)
    {
        Left++;
        _asked = !GlTap.LaterRange; // a later range of a multi-draw: no Touched comes to take it
        _terrain.Miss(why);
        return false;
    }

    public void BufferData(uint buffer, long size, IntPtr data, bool persistent)
    {
        if (Assert(size >= 0)) Mirrors.Data(buffer, size, data, persistent);
        if (persistent) _terrain.Maps.Specified(buffer, data != IntPtr.Zero);
    }

    public void BufferSubData(uint buffer, long offset, long size, IntPtr data)
    {
        if (Assert(offset >= 0) && Assert(size >= 0)) Mirrors.SubData(buffer, offset, size, data);
        _ = _terrain.PoolWritten(buffer, offset, size, data);
    }

    public void BufferCopy(uint from, uint to, long fromOffset, long toOffset, long size)
    {
        if (Assert(size >= 0) && Assert(fromOffset >= 0)) Mirrors.Copied(from, to, fromOffset, toOffset, size);
    }

    public void BufferUnseen(uint buffer, string why)
    {
        if (buffer > 0) Mirrors.Unseen(buffer, why);
    }

    public void BuffersGone(ReadOnlySpan<uint> buffers)
    {
        if (Assert(buffers.Length < 1 << 20)) Mirrors.Gone(buffers);
        _terrain.BuffersGone(buffers);
    }

    public void TextureTuned(uint texture, uint name, float value)
    {
        if (texture > 0 && Assert(texture < int.MaxValue)) _terrain.Tuned((int)texture, name, value);
    }

    public bool TextureMipmapped(uint texture) =>
        texture > 0 && Assert(texture < int.MaxValue) && _terrain.Mipmapped((int)texture);

    public void TextureChanged(uint texture)
    {
        if (texture > 0 && Assert(texture < int.MaxValue)) _terrain.Changed((int)texture);
    }

    public void TextureImaged(uint texture, uint target, (int Level, int Levels) levels,
        (int Format, int Width, int Height, int Depth) spec, (uint Format, uint Type) data, IntPtr pixels)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && Assert(levels.Level >= 0))
            _terrain.Imaged((int)texture, target, levels, spec, data, pixels);
    }

    public bool TextureSubImaged(uint texture, uint target, int level,
        (int X, int Y, int Z, int Width, int Height, int Depth) box, (uint Format, uint Type) data, IntPtr pixels) =>
        texture > 0 && Assert(texture < int.MaxValue) && Assert(target > 0) &&
        _terrain.SubImaged((int)texture, level, box, data, pixels);

    public bool TextureCopied((uint Texture, int Level, int X, int Y, int Z) to,
        (uint Texture, int Level, int X, int Y, int Z) from, (int Width, int Height, int Depth) size) =>
        to.Texture is > 0 and < int.MaxValue && Assert(from.Texture < int.MaxValue) && _terrain.Copied(to, from, size);

    public bool TextureRead(uint texture, int level, (int X, int Y, int Z) at, (int Fbo, int Read) from,
        (int X, int Y, int Width, int Height) rect) =>
        texture is > 0 and < int.MaxValue && Assert(level >= 0) && _terrain.FramebufferRead((int)texture, level, at, from, rect);

    public void Dispose()
    {
        _ = _terrain.Device.WaitIdle();
        QueriesGone();
        _pipelines.Dispose();
        foreach (var porting in _porting.Values.ToArray().Bounded(MaxScenePrograms)) porting.Drop();
        foreach (var drawn in _programs.Values.ToArray().Bounded(MaxScenePrograms)) drawn.Again?.Port.Drop();
        OwnGone(_clear);
        _porting.Clear();
        _programs.Clear();
        Mirrors.Dispose();
        Constants.Dispose();
    }
}

// Komet's own full-target program (the masked clear): ported on a builder through the shader cache
// (ProgramPorts.Made), so a later start reads it from disk; drawn as one triangle with its uniforms pushed.
internal sealed partial class SceneRenderer
{
    private static readonly Dictionary<string, int> NoAttributes = [];

    private sealed class OwnProgram(string name, string vertex, string fragment, string[] hot)
    {
        public string Name { get; } = name;
        public ProgramPorts.Stages Stages { get; } = new(name, vertex, fragment, NoAttributes);
        public HashSet<string> Hot { get; } = new(hot, StringComparer.Ordinal);
        public Build<GlslPort.Ported>? Building { get; set; }
        public GlslPort.Ported? Port { get; set; }
        public string Unported { get; set; } = ""; // why it has no port
    }

    // The port once made; begun on the first ask. wait: until it is made (a draw that cannot be left out)
    private static GlslPort.Ported? OwnPort(OwnProgram own, bool wait)
    {
        if (own.Port is not null || own.Unported.Length > 0) return own.Port;
        var (stages, hot) = (own.Stages, own.Hot);
        var building = own.Building ??= new Build<GlslPort.Ported>(Builds.Kind.Port,
            () => (ProgramPorts.Made(stages, out var why, hot), why));
        if (!building.Done && !(wait && building.Awaited(Hitches.Kind.Port))) return null;
        (own.Port, own.Building) = (building.Take(out var error), null);
        if (own.Port is null) own.Unported = error.Length > 0 ? error : "?";
        return own.Port;
    }

    private static void OwnGone(OwnProgram own)
    {
        own.Building?.Drop();
        own.Building = null;
    }

    // No blending; each colour attachment written through its mask (masks null: whole), those output leaves out (-1:
    // none) not at all
    private static GlTap.Fixed FullTarget(VulkanFrame.Target target, (bool Colors, int Output, GlTap.BlendState? Masks) colors,
        uint caps, uint depth, uint stencil)
    {
        Span<uint> words = stackalloc uint[GlTap.MaxBuffers];
        words.Clear();
        for (var i = 0; colors.Colors && i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
            if (target.Colors[i] is not null && (colors.Output < 0 || colors.Output == i))
                words[i] = colors.Masks is { } masks ? masks[i] & (FullMask << 22) : FullMask << 22;
        return new GlTap.Fixed(caps, depth, 0, 0,
            new GlTap.BlendState(words[0], words[1], words[2], words[3], words[4], words[5], words[6], words[7]), stencil);
    }

    // Each pushed member named in names gets the first components of its value (a float the X)
    private static void PushedValues(Span<uint> into, GlslPort.Block hot, string[] names, ReadOnlySpan<Vector4> values)
    {
        if (!Assert(into.Length * 4 >= hot.Size)) return;
        into.Clear();
        foreach (var member in hot.Members.Bounded(16))
        {
            var (at, which, count) = (member.Offset / 4, Array.IndexOf(names, member.Name), Math.Min(GlslPort.Shape(member.Type).Rows, 4));
            if (!Index(which, values.Length) || at < 0 || at + count > into.Length) continue;
            var value = values[which];
            for (var i = 0; i < Math.Min(count, 4); i++) into[at + i] = BitConverter.SingleToUInt32Bits(value[i]);
        }
    }
}

// Draws wait for their ports and pipelines, so they start early: ports as the engine links (Prewarm, world load), then
// an earlier start's pipelines (ShaderCache recipes) before the first draw. New builds or drivers still wait once.
internal sealed partial class SceneRenderer
{
    private const int MaxWarming = MaxScenePrograms, WarmedAFrame = 16;

    private readonly List<int> _warming = [];

    public long PipelinesStarted => _pipelines.Started;

    // Linked under the record: port begun now (a relinked name is another program)
    public void Prewarm(int id, string name)
    {
        if (!Assert(id > 0) || !NotNull(name)) return;
        if (_programs.ContainsKey(id) || _unported.ContainsKey(id) || _porting.ContainsKey(id)) Forget(id);
        if (_porting.Count >= MaxScenePrograms || _warming.Count >= MaxWarming || _programs.Count >= MaxScenePrograms) return;
        var porting = ProgramPorts.Started(id, name, out _);
        if (porting is null) return; // its first draw says why
        _porting[id] = porting;
        _warming.Add(id);
    }

    // Each frame start: done ports taken, known pipelines begun; allocation-free while none is done
    public void Warming()
    {
        if (_warming.Count == 0 || !Assert(_porting.Count <= MaxScenePrograms)) return;
        var (taken, count) = (0, _warming.Count);
        for (var n = 0; n < Math.Min(count, MaxWarming) && taken < WarmedAFrame; n++)
        {
            var i = count - 1 - n; // from the back: what is taken goes without moving what is still to look at
            var id = _warming[i];
            if (_porting.TryGetValue(id, out var porting) && !porting.Done) continue;
            _warming.RemoveAt(i);
            if (!_porting.ContainsKey(id) || _programs.ContainsKey(id)) continue; // forgotten, or drawn already
            _ = ProgramFor(id, out _);
            taken++;
        }
    }

    private void Warm(Drawn program)
    {
        if (program.Sources.Length == 0) return;
        var (id, name, ported, device) = (program.Program.Id, program.Name, program.Program.Ported, _terrain.Device);
        foreach (var recipe in ShaderCache.Recipes(program.Sources).Bounded(MaxPipelines))
        {
            if (recipe.Inputs.StartsWith(PipelineRecipes.Terrain, StringComparison.Ordinal) ||
                PipelineRecipes.Unpacked(recipe) is not var (state, attributes, target)) continue; // the terrain's: its own
            // skip old recipes missing a port input (instanced matrix columns): LayoutOf would refuse and log the
            // pipeline
            if (!Covers(ported, attributes)) continue;
            var key = new PipelineKey(id, program.Port, recipe.Inputs, recipe.Target, state);
            var topology = recipe.Topology;
            _pipelines.Start(key, (name, ported),
                p => TerrainPipeline.Started(device, p, attributes, (state, target, topology)));
        }
    }

    // Each input location of the port has exactly one attribute in the recipe
    private static bool Covers(GlslPort.Ported ported, TerrainPools.Attribute[] attributes)
    {
        foreach (var input in GlslPort.Columns(ported.Attributes).Bounded(MaxInputs))
            if (attributes.Count(a => a.Location == input.Location) != 1) return false;
        return true;
    }

    // First-time pipeline for a draw: kept for the next start
    private static void Learned(Drawn program, Formats formats, (PipelineKey Key, uint Topology, TerrainPipeline.Target Into) made)
    {
        if (program.Sources.Length == 0) return;
        var key = made.Key;
        ShaderCache.Remember(program.Sources, PipelineRecipes.Packed((key.Inputs, key.Target, made.Topology), key.State,
            formats.Attributes, made.Into));
    }
}

// Vulkan-only draws only mods ask for: indirect (GPU reads the mirror; multi-draw where supported), indexed line loops
// (indices + first copied as a strip), stencil test (dropped without stencil: OpenGL's passes), points (port sizes
// them).
internal sealed unsafe partial class SceneRenderer
{
    private const int ArraysCommand = 16, ElementsCommand = 20, MaxSeparateDraws = 1 << 12;

    private string Unindirect(GlTap.DrawCall call, GlTap.Indirect indirect)
    {
        if (!Assert(indirect.Draws > 0) || !Assert(call.Count >= 0)) return "an indirect draw of no commands";
        if (call.Mode == GlLineLoop) return "an indirect line loop";
        if (indirect.Offset % 4 != 0 || indirect.Stride % 4 != 0) return "an indirect draw of unaligned commands";
        if (indirect.CountBuffer != 0 && (!_terrain.Device.IndirectCount || VkApi.CmdDrawIndirectCount == null))
            return "an indirect draw with a count the GPU wrote, which the device does not draw";
        return "";
    }

    // Commands (and count) from the mirrors of the buffers OpenGL would read
    private bool Indirected(ref VulkanFrame.Op op, GlTap.Indirect indirect)
    {
        var indexed = op.Draw == VulkanFrame.DrawKind.Indexed;
        if (Mirrors.Read(indirect.Buffer) is not { } commands) return false;
        var span = ((ulong)(indirect.Draws - 1) * (ulong)indirect.Stride) + (ulong)(indexed ? ElementsCommand : ArraysCommand);
        if ((ulong)indirect.Offset + span > commands.Size) return false;
        var counted = indirect.CountBuffer != 0;
        op.Draw = (indexed, counted) switch
        {
            (true, true) => VulkanFrame.DrawKind.IndexedIndirectCount, (true, false) => VulkanFrame.DrawKind.IndexedIndirect,
            (false, true) => VulkanFrame.DrawKind.IndirectCount, _ => VulkanFrame.DrawKind.Indirect
        };
        (op.Indirect, op.IndirectOffset, op.Count, op.First) =
            (commands.Buffer, commands.Offset + (ulong)indirect.Offset, (uint)indirect.Draws, (uint)indirect.Stride);
        if (!counted) return true;
        if (Mirrors.Read(indirect.CountBuffer) is not { } count || (ulong)indirect.CountOffset + 4 > count.Size) return false;
        (op.CountBuffer, op.CountOffset) = (count.Buffer, count.Offset + (ulong)indirect.CountOffset);
        return Assert(op.CountBuffer != 0);
    }

    // Several indirect commands without multiDrawIndirect: one draw each
    private void Posted(VulkanFrame frame, VulkanFrame.Op op)
    {
        if (op.Draw is not (VulkanFrame.DrawKind.Indirect or VulkanFrame.DrawKind.IndexedIndirect) ||
            op.Count <= 1 || _terrain.Device.MultiDraws)
        {
            frame.Post(op);
            return;
        }

        var (count, offset, stride) = (op.Count, op.IndirectOffset, (ulong)op.First);
        for (var i = 0u; i < Math.Min(count, MaxSeparateDraws); i++)
        {
            (op.Count, op.IndirectOffset) = (1, offset + (i * stride));
            frame.Post(op);
        }
    }

    // Indices 0..count - 1 plus the first, GPU-copied before the segment's draws (ordered with mirror writes)
    private bool LoopedIndices(HostBuffer uploads, ref VulkanFrame.Op op)
    {
        var size = op.IndexType == Vk.IndexUint32 ? 4 : 2;
        var count = (int)op.Count;
        if (!Assert(count is > 0 and < 1 << 24)) return false;
        var at = uploads.Take(size * (count + 1), out _, 16);
        if (at < 0) return false;
        var from = (op.Index, op.IndexOffset);
        if (!Mirrors.CopyAhead(from, (uploads.Buffer, (ulong)at), (ulong)(size * count)) ||
            !Mirrors.CopyAhead(from, (uploads.Buffer, (ulong)(at + (size * count))), (ulong)size)) return false;
        (op.Index, op.IndexOffset, op.Count) = (uploads.Buffer, (ulong)at, (uint)count + 1);
        return true;
    }

    // No stencil in the target: OpenGL's test passes and writes nothing, so none is tested
    private static GlTap.Fixed Stenciled(GlTap.Fixed state, VulkanFrame.Target target)
    {
        if ((state.Caps & (uint)GlTap.Caps.Stencil) == 0) return state;
        var held = target.Depth is { } depth && (depth.Format.Aspect & Vk.AspectStencil) != 0;
        return held ? state : state with { Caps = state.Caps & ~(uint)GlTap.Caps.Stencil, Stencil = 0 };
    }

    private static (((uint, uint, uint) Front, (uint, uint, uint) Back) Values, Action<IntPtr>? Set) _stenciling;

    // Set before stencil-testing draws; the last delegate is reused while values stay (no closure per draw)
    private static Action<IntPtr> Stenciling(
        ((uint Reference, uint Compare, uint Write) Front, (uint Reference, uint Compare, uint Write) Back) values)
    {
        if (_stenciling.Set is { } same && _stenciling.Values == values) return same;
        _stenciling = (values, Stencil(values));
        return _stenciling.Set;
    }

    private static Action<IntPtr> Stencil(
        ((uint Reference, uint Compare, uint Write) Front, (uint Reference, uint Compare, uint Write) Back) values)
    {
        return commands =>
        {
            foreach (var (face, (reference, compare, write)) in (ReadOnlySpan<(uint, (uint, uint, uint))>)
                     [(Vk.StencilFront, values.Front), (Vk.StencilBack, values.Back)])
            {
                VkApi.CmdSetStencilReference(commands, face, reference);
                VkApi.CmdSetStencilCompareMask(commands, face, compare);
                VkApi.CmdSetStencilWriteMask(commands, face, write);
            }
        };
    }
}

// Sun glare is sized by occlusion query samples, so each Vulkan draw in an open GL query gets its own Vulkan query, the
// GL answer their sum (resolved before the frame slot recurs). Queries without Vulkan draws stay OpenGL's.
internal sealed unsafe partial class SceneRenderer
{
    private const uint SamplesPassed = 0x8914, AnySamples = 0x8C2F, QueryResult = 0x8866, QueryAvailable = 0x8867;
    private const int QueriesPerSlot = 64, MaxOcclusions = 256;

    private readonly Dictionary<uint, Occlusion> _occlusions = [];
    private ulong _queries;
    private uint _query; // the GL query open now, 0 for none
    private (long Frame, int Used) _slotQueries = (-1, 0);

    private sealed class Occlusion
    {
        public bool Any { get; set; }
        public List<(long Frame, uint Slot)> Slots { get; } = [];
        public long Resolved { get; set; }
        public bool Taken { get; set; } // a Vulkan draw was in it
    }

    public void Querying(uint target, uint id)
    {
        if (target is not (SamplesPassed or AnySamples)) return;
        _ = Assert(id == 0 || _query == 0); // OpenGL has one query of a target open at a time
        _query = id;
        if (id == 0) return;
        if (_occlusions.Count >= MaxOcclusions && !_occlusions.ContainsKey(id)) _occlusions.Clear();
        ref var occlusion = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_occlusions, id, out _);
        occlusion ??= new Occlusion(); // begun anew each frame: what it counted before goes
        occlusion.Slots.Clear();
        (occlusion.Any, occlusion.Resolved, occlusion.Taken) = (target == AnySamples, 0, false);
    }

    private long QueryBegin()
    {
        var frame = _terrain.Frame;
        if (_query == 0 || !_occlusions.TryGetValue(_query, out var occlusion) || !Assert(frame.Open)) return -1;
        _ = Assert(occlusion.Slots.Count <= QueriesPerSlot);
        if (_queries == 0 && !MadePool()) return -1;
        var first = (uint)(frame.Number % VulkanFrame.Slots * QueriesPerSlot);
        if (_slotQueries.Frame != frame.Number)
        {
            VkApi.CmdResetQueryPool(frame.Setup, _queries, first, QueriesPerSlot); // the setup runs before any rendering
            _slotQueries = (frame.Number, 0);
        }

        if (_slotQueries.Used >= QueriesPerSlot) return -1;
        var slot = first + (uint)_slotQueries.Used++;
        occlusion.Slots.Add((frame.Number, slot));
        occlusion.Taken = true;
        frame.Post(new VulkanFrame.Op
        {
            Kind = VulkanFrame.OpKind.BeginQuery, Pipeline = _queries, Count = slot,
            First = _terrain.Device.PreciseQueries ? Vk.QueryPrecise : 0
        });
        return slot;
    }

    private void QueryEnd(long slot)
    {
        if (slot < 0 || !Assert(_terrain.Frame.Open)) return;
        _terrain.Frame.Post(new VulkanFrame.Op { Kind = VulkanFrame.OpKind.EndQuery, Pipeline = _queries, Count = (uint)slot });
    }

    private bool MadePool()
    {
        _queries = _terrain.Device.QueryPool(Vk.QueryOcclusion, QueriesPerSlot * VulkanFrame.Slots);
        return _queries != 0;
    }

    public long? Answer(uint id, uint name)
    {
        if (!_occlusions.TryGetValue(id, out var occlusion) || !occlusion.Taken) return null;
        if (name is not (QueryResult or QueryAvailable)) return null;
        var (samples, available) = (occlusion.Resolved, true);
        foreach (var (_, slot) in occlusion.Slots.Bounded(QueriesPerSlot))
        {
            var (value, ready) = Result(slot, name == QueryResult);
            (samples, available) = (samples + value, available && ready);
        }

        if (name == QueryAvailable) return available ? 1 : 0;
        return occlusion.Any ? Math.Min(samples, 1) : samples;
    }

    private (long Samples, bool Ready) Result(uint slot, bool wait)
    {
        if (wait && _terrain.Frame.Open) _terrain.Frame.Close("an occlusion query's result asked for");
        var data = stackalloc ulong[2];
        var flags = Vk.Result64 | Vk.ResultAvailability | (wait ? Vk.ResultWait : 0);
        var result = VkApi.GetQueryPoolResults(_terrain.Device.Handle, _queries, slot, 1, 16, data, 16, flags);
        _ = Assert(result is Vk.Success or 1); // VK_NOT_READY is 1
        return ((long)data[0], data[1] != 0);
    }

    public void Resolve()
    {
        if (_queries == 0 || !Assert(!_terrain.Frame.Open)) return;
        using var occlusions = _occlusions.Values.GetEnumerator(); // every frame: no iterator; nothing here adds or removes one
        for (var o = 0; o < MaxOcclusions && occlusions.MoveNext(); o++)
        {
            var occlusion = occlusions.Current;
            var kept = 0;
            for (var i = 0; i < Math.Min(occlusion.Slots.Count, QueriesPerSlot); i++)
            {
                var (frame, slot) = occlusion.Slots[i];
                if (frame <= _terrain.Frame.Done) occlusion.Resolved += Result(slot, true).Samples;
                else occlusion.Slots[kept++] = occlusion.Slots[i];
            }

            occlusion.Slots.RemoveRange(kept, occlusion.Slots.Count - kept);
        }
    }

    private void QueriesGone()
    {
        if (_queries != 0) VkApi.DestroyQueryPool(_terrain.Device.Handle, _queries, null);
        _queries = 0;
        _occlusions.Clear();
    }
}

// Uniforms changing nearly every draw go into push constants so the large blocks stay uploaded. Found over the first
// draws, then the program is ported once more; ShaderCache keeps them for the next start.
internal sealed partial class SceneRenderer
{
    private const int HotBudget = 224, MaxHot = 64, RecheckDraws = 1024, RecheckFrames = 16, MaxRounds = 2;
    private const double HotShare = 0.34;

    // Draws watched before hot uniforms settle
    private const int Watched = 96;

    // Settled but still repacking in >= 1/4 of draws: watched over atypical draws (GUI: hotbar first, icons later), so
    // re-watched up to MaxRounds. Only for programs drawn many times a frame; once-a-frame passes repack every frame
    // anyway.
    private void Recheck(Drawn program)
    {
        if (program.Rounds >= MaxRounds || program.Next is not null || ++program.Draws < RecheckDraws) return;
        var mirror = program.Program.Mirror;
        var (packs, frames) = (mirror.Packs - program.PacksAt, _terrain.Frame.Number - program.FrameAt);
        (program.Draws, program.PacksAt, program.FrameAt) = (0, mirror.Packs, _terrain.Frame.Number);
        if (packs < RecheckDraws / 4 || frames > RecheckFrames) return;
        (program.Rounds, program.Settled) = (program.Rounds + 1, false);
        mirror.Remeasure();
        _logger?.Notification("Komet: Vulkan watches the uniforms of {0} again: {1} block packs in {2} draws", program.Name, packs,
            RecheckDraws);
    }

    // New port on a builder, then its pipelines again (Warm); the old port draws until all are made (Current), so the
    // switch leaves no draw to OpenGL
    private void Settle(Drawn program)
    {
        if (program.Settled && program.Again is null)
        {
            Recheck(program);
            return;
        }

        if (program.Again is var (again, names))
        {
            if (again.Done) Warmed(program, again, names);
            return;
        }

        var mirror = program.Program.Mirror;
        mirror.Drawn();
        if (mirror.Draws < Watched) return;
        program.Settled = true;
        // already pushed uniforms stay pushed, ahead of the ones changing now
        var pushed = program.Program.Ported.HotUniforms.Members.Select(static u => u.Name).ToArray();
        var hot = Budgeted(program.Program, [.. pushed.Concat(mirror.Hot(HotShare)).Distinct(StringComparer.Ordinal)]);
        if (hot.Count == 0 || hot.SetEquals(pushed))
        {
            // nothing to push, or the port pushes these already: no port and no pipelines anew, and no more counting
            mirror.Measuring = false;
            if (hot.Count > 0)
                _logger?.Notification("Komet: Vulkan keeps the port of {0}: it pushes {1} already", program.Name, string.Join(", ", hot));
            return;
        }

        var start = Hitches.Now;
        var port = ProgramPorts.Started(program.Program.Id, program.Name, out var error, hot, settled: true);
        Hitches.Since(Hitches.Kind.Port, start);
        if (port is not null) program.Again = (port, string.Join(", ", hot));
        else
            _logger?.Notification("Komet: Vulkan port of {0} with {1} pushed: {2}", program.Name, string.Join(", ", hot), error);
    }

    private void Warmed(Drawn program, Build<GlslPort.Ported> again, string hot)
    {
        program.Again = null;
        var ported = again.Take(out var error);
        var (id, name, port) = (program.Program.Id, program.Name, ++_ports);
        _logger?.Notification("Komet: Vulkan port of {0} with {1} pushed: {2}", name, hot,
            ported is null ? error : $"{ported.HotUniforms.Size} bytes, {FrameClock.ToMs(again.Ticks):0} ms {again.Where}");
        if (ported is null || !Assert(program.Next is null)) return;
        var old = program.Port;
        program.Next = (ported, port,
            _pipelines.Warm(key => key.Program == id && key.Port == old, key => key with { Port = port }, (name, ported)));
    }

    // Switched to the next port once its pipelines are made, as this frame counts them
    private Drawn? Current(Drawn? program)
    {
        if (program?.Next is not var (ported, port, keys) || !_pipelines.Made(keys)) return program;
        var (id, name) = (program.Program.Id, program.Name);
        Forget(id, keep: port);
        if (_terrain.Watch(name, id, ported) is not { } watched) return null; // Forget made room: never
        var next = new Drawn(watched, ProgramPorts.Points(id, ported))
        {
            Settled = true, Port = port, Rounds = program.Rounds, Sources = program.Sources
        };
        _programs[id] = next;
        return next;
    }

    private static HashSet<string> Budgeted(TerrainRenderer.Program program, IReadOnlyList<string> changed)
    {
        var hot = new HashSet<string>(StringComparer.Ordinal);
        var members = program.Ported.VertexUniforms.Members.Concat(program.Ported.FragmentUniforms.Members)
            .Concat(program.Ported.HotUniforms.Members).GroupBy(u => u.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var bytes = 0;
        foreach (var name in changed.Take(MaxHot).ToArray().Bounded(MaxHot))
        {
            if (!members.TryGetValue(name, out var u) || u.Count > 4) continue;
            var (size, align) = Std430(u);
            var at = (bytes + align - 1) / align * align;
            if (size == 0 || at + size > HotBudget) continue;
            bytes = at + size;
            _ = hot.Add(name);
        }

        return hot;
    }

    // std430 size/alignment: scalar 4, vec2 8, vec3 and vec4 16, a matrix its columns as vec4s
    private static (int Size, int Align) Std430(GlslPort.Uniform u)
    {
        var (columns, rows) = GlslPort.Shape(u.Type);
        if (columns * rows == 0) return (0, 4);
        var align = rows switch { 1 => 4, 2 => 8, _ => 16 };
        var element = columns > 1 ? columns * align : 4 * rows;
        var stride = (element + align - 1) / align * align;
        return (u.Count > 1 ? stride * u.Count : element, align);
    }
}
