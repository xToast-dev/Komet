namespace Komet.Vulkan;

// A draw it cannot take is left to OpenGL: GlTap's Touch group then closes the open segment where OpenGL's draw must follow
// Vulkan's work. So is a draw whose port or pipeline is still being made off the engine's thread (Builds), until it is done.
internal sealed partial class SceneRenderer : IGlWrites, IDisposable
{
    private const int MaxUnported = 1024, MaxScenePrograms = 256, MaxReasons = 32;

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

        // Whether its hot uniforms are settled (watched over its first draws, or it came with them)
        public bool Settled { get; set; }

        // Since it settled: its draws and its mirror's packs when they were last counted, and how often it was watched again
        public int Draws { get; set; }
        public long PacksAt { get; set; }
        public long FrameAt { get; set; }
        public int Rounds { get; set; }

        // Its port with the hot uniforms it settled on, being made, and their names
        public (Build<GlslPort.Ported> Port, string Hot)? Again { get; set; }

        // Which of the program's ports it draws with (PipelineKey), and the next one, made, with its pipelines being made
        public int Port { get; init; }
        public (GlslPort.Ported Ported, int Port, PipelineKey[] Pipelines)? Next { get; set; }

        // What its textures were last resolved against, and the images they gave: the same again while nothing changed
        public (long Textures, int Samplers, long Copies, long Targets, VulkanFrame.Target? Into) Key { get; set; }
        public SharedImage[] Images { get; set; } = [];

        // The same again after other binds too, while the units its samplers read hold the textures and sampler objects they
        // held then (Bound) and no texture was deleted, no sampler uniform set, no copy or target changed since (Held)
        public (long Deleted, int Samplers, long Copies, long Targets, VulkanFrame.Target? Into) Held { get; set; }
        public (int Unit, uint Kind, uint Texture, int Sampler)[] Bound { get; set; } = [];

        // The kind of each sampler's texture, and what the samplers resolved to before by what their units held, while Held
        // stays SeenHeld: the units switch between a few textures (a GUI element's, an entity's) without a resolve each time
        public uint[] Kinds { get; } = [.. program.Ported.Samplers.Select(s => TargetOf(s.Type))];
        public Dictionary<Units, Seen> Seen { get; } = [];
        public (long Deleted, int Samplers, long Copies, long Targets, VulkanFrame.Target? Into) SeenHeld { get; set; }
    }

    private SceneRenderer(TerrainRenderer terrain, ILogger? logger, HostBuffer constants)
    {
        _ = NotNull(terrain) && NotNull(constants);
        (_terrain, _logger, Constants) = (terrain, logger, constants);
        Mirrors = new BufferMirrors(terrain.Device, terrain.Frame, terrain.Staging);
        _pipelines = new PipelineSet<PipelineKey>(terrain.Device, terrain.Frame, logger, MaxPipelines);
    }

    public static SceneRenderer? Create(TerrainRenderer terrain, ILogger? logger)
    {
        if (!NotNull(terrain) || HostBuffer.Create(terrain.Device, 64) is not { } constants) return null;
        var at = constants.Take(32, out var into, 16);
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(into);
        (words[3], words[7]) = (BitConverter.SingleToUInt32Bits(1), 1); // OpenGL's (0, 0, 0, 1), as floats, then as ints
        _ = Assert(at == 0) && Assert(words.Length == 8);
        return new SceneRenderer(terrain, logger, constants);
    }

    public BufferMirrors Mirrors { get; }

    // (0, 0, 0, 1) as floats at 0 and as integers at 16: what a vertex shader reads from a disabled attribute
    private HostBuffer Constants { get; }

    public System.Func<int, string, bool> Takes { get; set; } = (_, _) => false;

    public System.Func<bool> Deferring { get; set; } = () => false;

    // Between the frame's start and its end: draws and clears are taken only then
    public bool Framing { get; set; }

    public long Draws { get; private set; }
    public long Clears { get; private set; }
    public long Left { get; private set; }

    // Draws left to OpenGL while their port was being made (the pipelines' are PipelineWaits)
    public long PortWaits { get; private set; }
    public long PipelineWaits => _pipelines.Waits;

    // Ports and pipelines being made for it now
    public int Building => _porting.Count + _pipelines.Building;
    public long Ticks { get; private set; }
    public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);

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
            var ported = Ported(id, name, out var waiting, out var error);
            if (waiting) return null;
            if (ported is null || _programs.Count >= MaxScenePrograms)
            {
                if (_unported.Count < MaxUnported) _unported[id] = $"{name} has no Vulkan port: {error}";
                return Refused(out why, $"{name} has no Vulkan port: {error}");
            }

            // its hot uniforms are settled over its first draws, unless the port has the ones an earlier start settled on
            program = _terrain.Watch(name, id, ported, measuring: ported.HotUniforms.Size == 0);
        }

        var drawn = new Drawn(program, ProgramPorts.Points(id, program.Ported))
        {
            Settled = program.Ported.HotUniforms.Size > 0, // the terrain's own, ported with its hot uniforms, or cached ones
            Port = ++_ports
        };
        _programs[id] = drawn;
        return drawn;
    }

    // Read back on this thread, ported on a builder: waiting until it is done
    private GlslPort.Ported? Ported(int id, string name, out bool waiting, out string error)
    {
        (waiting, error) = (false, "");
        if (!Assert(id > 0) || !Assert(_porting.Count <= MaxScenePrograms)) return null;
        if (!_porting.TryGetValue(id, out var porting))
        {
            var start = Hitches.Now;
            porting = ProgramPorts.Started(id, name, out error);
            Hitches.Since(Hitches.Kind.Port, start);
            if (porting is null) return null;
            _porting[id] = porting;
        }

        if (!porting.Done)
        {
            (waiting, PortWaits) = (true, PortWaits + 1);
            return null;
        }

        _ = _porting.Remove(id);
        var ported = porting.Take(out error);
        _logger?.Notification("Komet: Vulkan port of {0}: {1}", name,
            ported is null ? error : $"{FrameClock.ToMs(porting.Ticks):0} ms off the engine's thread");
        return ported;
    }

    private static Drawn? Refused(out string why, string reason)
    {
        why = reason;
        _ = Assert(reason.Length > 0) && Assert(why.Length > 0);
        return null;
    }

    // The engine rebuilt its shaders; the GL name may come back for another program. keep: a port whose pipelines stay
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

    private bool Leave(string why)
    {
        Left++;
        if (NotNull(why) && why.Length > 0 && Reasons.Count < MaxReasons && Reasons.Add(why))
            _logger?.Warning("Komet: Vulkan leaves a draw to OpenGL: {0}", why);
        _ = Assert(Left > 0);
        return false;
    }

    public void BufferData(uint buffer, long size, IntPtr data, bool persistent)
    {
        if (Assert(size >= 0) && NotNull(Mirrors)) Mirrors.Data(buffer, size, data, persistent);
    }

    public void BufferSubData(uint buffer, long offset, long size, IntPtr data)
    {
        if (Assert(offset >= 0) && Assert(size >= 0)) Mirrors.SubData(buffer, offset, size, data);
    }

    public void BufferCopy(uint from, uint to, long fromOffset, long toOffset, long size)
    {
        if (Assert(size >= 0) && Assert(fromOffset >= 0)) Mirrors.Copied(from, to, fromOffset, toOffset, size);
    }

    public void BufferUnseen(uint buffer, string why)
    {
        if (buffer > 0 && NotNull(Mirrors) && Assert(Mirrors.Count >= 0)) Mirrors.Unseen(buffer, why);
    }

    public void BuffersGone(ReadOnlySpan<uint> buffers)
    {
        if (Assert(buffers.Length < 1 << 20) && NotNull(Mirrors)) Mirrors.Gone(buffers);
    }

    public bool TextureUploaded(uint texture, int level, (int X, int Y, int Width, int Height) rect,
        (uint Format, uint Type) data, IntPtr pixels) =>
        texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain) &&
        _terrain.Uploaded((int)texture, level, rect, data, pixels);

    public void TextureSpecified(uint texture, int level, (int Format, int Width, int Height) spec,
        (uint Format, uint Type) data, IntPtr pixels)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && Assert(level >= 0))
            _terrain.Specified((int)texture, level, spec, data, pixels);
    }

    public void TextureTuned(uint texture, uint name, float value)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain.Copies))
            _terrain.Tuned((int)texture, name, value);
    }

    public bool TextureMipmapped(uint texture) =>
        texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain) && _terrain.Mipmapped((int)texture);

    public void TextureChanged(uint texture)
    {
        if (texture > 0 && Assert(texture < int.MaxValue) && NotNull(_terrain.Copies)) _terrain.Changed((int)texture);
    }

    public void Dispose()
    {
        _ = Assert(_programs.Count <= MaxScenePrograms) && _terrain.Device.WaitIdle();
        QueriesGone();
        _pipelines.Dispose();
        foreach (var porting in _porting.Values.ToArray().Bounded(MaxScenePrograms)) porting.Drop();
        foreach (var drawn in _programs.Values.ToArray().Bounded(MaxScenePrograms)) drawn.Again?.Port.Drop();
        _porting.Clear();
        _programs.Clear();
        Mirrors.Dispose();
        Constants.Dispose();
    }
}
