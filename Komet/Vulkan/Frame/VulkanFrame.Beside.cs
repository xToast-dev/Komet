namespace Komet.Vulkan;

// What OpenGL may do while a segment is open, when the scene is Vulkan's (GlTap's Touch group hears of every call first). Beside
// a pending segment - OpenGL not signalled yet, so its calls run before all of the segment's work - a call passes that neither
// reads what the segment drew nor writes it, except a depth-only draw into a depth the segment drew depth-only into, both
// testing and writing with LESS or LEQUAL, which ends with the nearest depth whichever runs first. Beside a segment OpenGL has
// signalled, only uploads pass: into buffers the scene's draws read through their mirrors (not a terrain pool's shared memory)
// and into textures the segment does not hold (their copies are refreshed before a draw samples them). Anything else closes the
// segment first, so it runs after Vulkan's work, as the engine ordered it.
internal sealed partial class VulkanFrame
{
    private const uint CompareLess = 1, CompareLessOrEqual = 3;

    private readonly Dictionary<int, bool> _written = []; // GL texture of each image drawn into; true: depth only, commuting
    private readonly List<uint> _reads = [];
    private (Target? Target, bool Commuting, long Serial) _wrote;

    public System.Func<int, (int[] Colors, int Depth)> Attached { get; set; } = _ => ([], 0);

    public string Why { get; private set; } = ""; // what the last call beside a pending segment touched

    public void Wrote(Target target, bool commuting)
    {
        if (!NotNull(target) || !Assert(_open is not null)) return;
        if (ReferenceEquals(_wrote.Target, target) && _wrote.Commuting == commuting && _wrote.Serial == Serial) return;
        _wrote = (target, commuting, Serial); // the draws after it into the same target change nothing more
        foreach (var color in target.Colors.Bounded(MaxColors))
            if (color is not null)
                _written[color.Texture] = false;
        if (target.Depth is not { } depth || target.DepthReadOnly) return;
        _written[depth.Texture] = commuting && target.DepthOnly && _written.GetValueOrDefault(depth.Texture, true);
    }

    public void Beside(GlTap.Touch kind, uint id)
    {
        if (_open is null || !Assert(kind <= GlTap.Touch.Mipmap)) return;
        if ((_pending || kind is GlTap.Touch.Upload or GlTap.Touch.TextureUpload or GlTap.Touch.Mipmap) &&
            Compatible(kind, id)) return;
        var what = kind is GlTap.Touch.Draw or GlTap.Touch.Clear
            ? $" ({ProgramPorts.Name(GlTap.Program)} into {GlTap.DrawFramebuffer})"
            : "";
        var closer = $"OpenGL: {kind}{what}{(_pending ? " beside (" + Why + ")" : "")}";
        Close(closer);
        _ = Assert(_open is null);
    }

    private bool Compatible(GlTap.Touch kind, uint id)
    {
        _ = Assert(_open is not null) && Assert(_written.Count <= MaxImages * 2);
        return kind switch
        {
            GlTap.Touch.Upload => _pending || !Shares(id),
            GlTap.Touch.TextureUpload or GlTap.Touch.Mipmap => !_written.ContainsKey((int)id) && (_pending || !Took((int)id)),
            GlTap.Touch.Draw => Drawable(),
            GlTap.Touch.Clear => !Holds(GlTap.DrawFramebuffer),
            GlTap.Touch.ClearNamed => !Holds((int)id),
            GlTap.Touch.ClearTexture => !_written.ContainsKey((int)id),
            _ => false
        };
    }

    // Asked as it is now: a pool adopted after the segment opened is shared too
    private bool Shares(uint buffer)
    {
        if (buffer == 0 || !Assert(_shared.Set.Count < 1 << 20)) return false;
        var now = Buffers();
        if (!ReferenceEquals(now, _shared.Of)) _shared = (now, [.. now]);
        return _shared.Set.Contains(buffer);
    }

    private (uint[]? Of, HashSet<uint> Set) _shared = (null, []);

    private bool Took(int texture)
    {
        if (!Assert(texture >= 0) || texture == 0) return false;
        foreach (var shared in _held.Bounded(MaxImages))
            if (shared.Image.Texture == texture)
                return true;
        return false;
    }

    private bool Holds(int fbo)
    {
        if (fbo <= 0 || !Assert(_written.Count <= MaxImages * 2)) return false;
        var (colors, depth) = Attached(fbo);
        if (_written.ContainsKey(depth)) return true;
        foreach (var color in colors.Bounded(MaxColors))
            if (color > 0 && _written.ContainsKey(color))
                return true;
        return false;
    }

    private bool Drawable()
    {
        var fbo = GlTap.DrawFramebuffer;
        if (fbo > 0 && !Beside(fbo)) return false;
        GlTap.Sampled(_reads);
        foreach (var read in _reads.Bounded(256))
            if (_written.ContainsKey((int)read))
            {
                Why = $"reads texture {read}";
                return false;
            }

        Why = "";
        return true;
    }

    private bool Beside(int fbo)
    {
        Why = "writes";
        var (colors, depth) = Attached(fbo);
        var buffers = GlTap.DrawBuffersOf(fbo);
        for (var i = 0; i < Math.Min(buffers.Length, GlTap.MaxBuffers); i++)
            if (buffers[i] >= 0 && buffers[i] < colors.Length && _written.ContainsKey(colors[buffers[i]]))
                return false;
        if (depth <= 0 || !_written.TryGetValue(depth, out var commuting)) return true;
        var state = GlTap.State;
        if (!state.DepthTest) return Assert(depth > 0); // the depth untouched
        return commuting && state.DepthWrite && state.DepthCompare is CompareLess or CompareLessOrEqual;
    }
}
