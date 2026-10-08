using OpenTK.Graphics.OpenGL;

namespace Komet.Vulkan;

internal static partial class VulkanRenderer
{
    private const ulong Budget = 12UL << 30, LogGrowth = 512UL << 20;
    private const long GuardMs = 250;
    private const int ReportEvery = 600, LogEvery = 3600, VerifyAt = 300;
    private static ulong _logged;
    private static long _guarded;
    private static string _waits = "";

    private static void Report(string waits)
    {
        if (!NotNull(waits)) return;
        var phase = waits.Length > 0 ? Phase.Waiting : Phase.Running;
        if (phase != State || waits != _waits)
        {
            Status = waits.Length > 0 ? waits : "drawing: " + Passes();
            if (waits.Length > 0 || State != Phase.Running) _logger?.Notification("Komet: Vulkan {0}", Status);
        }

        (State, _waits) = (phase, waits);
        _ = Assert(State is Phase.Running or Phase.Waiting);
    }

    private static string Passes()
    {
        var names = new List<string>(4);
        if (Taking[(int)Pass.LiquidDepth]) names.Add("liquid depth");
        if (Taking[(int)Pass.Shadows])
            names.Add(FrustumSweep.Casters && !ShadowView.Differed ? "shadow maps (casters culled by the view)" : "shadow maps");
        if (Taking[(int)Pass.Opaque]) names.Add(OcclusionCulling.Drawer is null ? "opaque" : "opaque (culled on the GPU)");
        if (Taking[(int)Pass.Transparent]) names.Add("transparent");
        if (Taking[(int)Pass.WaterPlants]) names.Add("water plants");
        if (SceneParts() is { Length: > 0 } parts) names.Add("and of the rest " + parts);
        if (_windowing)
            names.Add(_renderer?.Showing == true ? "the window, shown by Vulkan" + Unshown()
                : "the window, shown by OpenGL");
        _ = Assert(names.Count <= 7);
        return names.Count == 0 ? "no pass (all switched off)" : string.Join(", ", names);
    }

    private static string Unshown() =>
        _renderer is { Unshown: > 0 } renderer && Assert(renderer.Unshown < long.MaxValue)
            ? $" ({renderer.Unshown} frames by OpenGL, as Vulkan could not)"
            : "";

    private static void Periodically(TerrainRenderer renderer)
    {
        var frame = renderer.Frame.Number;
        if (!NotNull(renderer) || !Assert(frame >= 0)) return;
        if (frame == VerifyAt) Verify(renderer);
        if (frame % ReportEvery != 1) return;
        Status = (_waits.Length > 0 ? _waits + "; " : "") + Short(renderer);
        if (frame % LogEvery == 1 || frame == ReportEvery + 1) _logger?.Notification("Komet: {0}", Full(renderer));
    }

    // The HUD window's numbers since the last report: draws a frame through Vulkan and left to OpenGL, handoffs, memory, and what
    // closed segments most often. Zero while Vulkan does not run.
    internal static (double Draws, double Left, double Segments, long MemoryMb, string[] Closers) Snapshot()
    {
        if (State != Phase.Running || _renderer is not { } renderer) return (0, 0, 0, 0, []);
        var frames = Math.Max(renderer.Frame.Frames, 1);
        var (draws, left) = (renderer.Draws + (_scene?.Draws ?? 0), renderer.Left + (_scene?.Left ?? 0));
        var closers = renderer.Frame.Closers.OrderByDescending(static c => c.Value).Take(6).Select(static c => c.Key).ToArray();
        _ = Assert(frames > 0) && Assert(draws >= 0);
        return ((double)draws / frames, (double)left / frames, (double)renderer.Frame.Segments / frames, (long)(VkMemory.Bytes >> 20),
            closers);
    }

    // A long HUD line made the panel slow to draw; the full report goes to the log
    private static string Short(TerrainRenderer renderer)
    {
        var frames = Math.Max(renderer.Frame.Frames, 1);
        _ = Assert(frames > 0) && NotNull(renderer);
        var scene = _scene is { } s && SceneOn
            ? $"{(double)s.Draws / frames:0} other draws ({(double)s.Left / frames:0.0} left to OpenGL), "
            : "";
        var own = renderer.OwnDraws > 0 ? $" ({(double)renderer.OwnDraws / frames:0} on Komet's depth)" : "";
        return $"Vulkan draws {Passes()}: {(double)renderer.Draws / frames:0} pool draws{own}, {scene}" +
               $"{(double)renderer.Frame.Segments / frames:0.0} handoffs, {(double)renderer.Left / frames:0.0} left to " +
               $"OpenGL a frame; {VkMemory.Bytes >> 20} MB";
    }

    private static string Full(TerrainRenderer renderer)
    {
        var frame = renderer.Frame;
        var frames = Math.Max(frame.Frames, 1);
        var ms = FrameClock.TickMs / frames;
        _ = Assert(frames > 0) && Finite(ms);
        return $"terrain in Vulkan ({Passes()}): {frames} frames, recording {renderer.TakeTicks * ms:0.00} ms (packing " +
               $"{TerrainDraw.PackTicks * ms:0.00}) a frame, {(double)renderer.Draws / frames:0} pool draws and " +
               $"{(double)frame.Segments / frames:0.0} segments a frame, {renderer.Left} left to OpenGL" +
               (renderer.Reasons.Count > 0 ? $" ({string.Join("; ", renderer.Reasons.Take(4))})" : "") +
               $", handoff a frame {frame.OpenTicks * ms:0.00} ms opening, {frame.SignalTicks * ms:0.00} ms OpenGL's " +
               $"signal, {frame.SubmitTicks * ms:0.00} ms submitting, {frame.WaitTicks * ms:0.00} ms OpenGL's wait, " +
               $"{frame.CleanSignals} of {frame.CleanSignals + frame.DirtySignals} signals left out (nothing shared touched), " +
               $"{frame.LazyWaits} waits for Vulkan, {frame.LateSignals} late signals, " +
               $"{renderer.Pools.Count} pools ({renderer.Pools.Bytes >> 20} MB, {Born} adopted as made, " +
               $"{renderer.Device.Arena.AheadMade} arena blocks made ahead, {renderer.Device.Arena.AheadWaited} waited for), " +
               Copied(renderer) + "; closed by " +
               string.Join(", ", frame.Closers.OrderByDescending(c => c.Value).Take(8).Select(c => $"{c.Key} {c.Value}")) +
               Culled(renderer, frames) + Owned(renderer, frames) + SceneReport(frames) + renderer.Frame.Times() +
               Hitches.Report() +
               Builds.Report() + ShaderCache.Report() + "; memory " +
               VkMemory.Report() + "; " + Held();
    }

    private static string SceneReport(long frames)
    {
        if (_scene is not { } scene || !Assert(frames > 0)) return "";
        var ms = FrameClock.TickMs / frames;
        var mirrors = scene.Mirrors;
        _ = Assert(Finite(ms));
        return $"; the rest of the frame: {(double)scene.Draws / frames:0} draws, {(double)scene.Clears / frames:0.0} " +
               $"clears ({scene.EarlyClears} kept from between frames), {(double)scene.Blits / frames:0.0} blits, " +
               $"{(double)scene.Left / frames:0.0} left to OpenGL a frame" +
               (scene.Reasons.Count > 0 ? $" ({string.Join("; ", scene.Reasons.Take(6))})" : "") +
               $", recording {scene.Ticks * ms:0.00} ms a frame; {mirrors.Count} buffer mirrors, {mirrors.Bytes >> 20} MB, " +
               $"{mirrors.Writes} writes, {mirrors.Renames} renamed, {mirrors.Imports} imported on the GPU " +
               $"({mirrors.ImportedBytes >> 10} KB, {mirrors.Asked} sizes asked of OpenGL)" +
               (mirrors.Imports > 0 ? $" ({mirrors.Reasons})" : "");
    }

    private static string Copied(TerrainRenderer renderer)
    {
        var (copies, staging) = (renderer.Copies, renderer.Staging);
        _ = Assert(copies.Private <= copies.Count) && Assert(staging.Owned >= 0);
        return $"{copies.Count} texture copies ({copies.Private} private, {copies.Evictions} made room for others; " +
               $"{copies.Packs} levels packed, " +
               $"{copies.PackedBytes >> 20} MB{copies.MostPacked()}, {copies.Fills} fills, {copies.Moves} moved between kinds, " +
               $"{renderer.Uploads} uploads and mipmaps made from host memory; staging {staging.Bytes >> 20} MB, " +
               $"{staging.Owned} ranges of their own)";
    }

    private static string Culled(TerrainRenderer renderer, long frames)
    {
        if (!NotNull(renderer) || !Assert(frames > 0) || OcclusionCulling.Drawer is null) return "";
        return $"; OcclusionCulling in Vulkan: {(double)renderer.Counted / frames:0.0} draws a frame, " +
               $"{OcclusionCulling.Refused} refused, {renderer.Compute.Refused} passes without a segment or memory" +
               (OcclusionCulling.RowsUsed > 0
                   ? $", from rows ({OcclusionCulling.RowsUsed} rows, {OcclusionCulling.Rewrites} regions written in full)"
                   : "") + $", {(double)OcclusionCulling.SortedDraws / frames:0.0} draws a frame sorted near to far, shadow casters " +
               (ShadowView.Differed ? "no longer culled by the view (it differed from the engine's)" : "culled by the view") +
               DrawnTerrain();
    }

    private static string Owned(TerrainRenderer renderer, long frames)
    {
        if (!NotNull(renderer) || !Assert(frames > 0)) return "";
        if (!OwnDepth) return "; Komet's own depth for the opaque terrain and the shadow maps switched off";
        return $"; Komet's own depth (compressed, tested before shading) for {(double)renderer.OwnDraws / frames:0.0} pool " +
               $"draws a frame (the opaque terrain{(SceneOn ? ", the shadow maps" : "; shadow maps need the scene")}), " +
               $"{(double)renderer.OwnCopies / frames:0.0} depth copies and {(double)renderer.OwnClears / frames:0.0} clears " +
               "in place of a copy a frame";
    }

    // NaN: no statistics read back yet (a report after the first frame, before the readback ring filled)
    internal static string DrawnTerrain()
    {
        OcclusionCulling.Sample();
        var (ranges, hidden, triangles, gone) = (OcclusionCulling.Stat(0), OcclusionCulling.Stat(1), OcclusionCulling.Stat(2),
            OcclusionCulling.Stat(3));
        if (double.IsNaN(ranges) || ranges <= 0 || !Finite(ranges) || !Assert(hidden <= ranges)) return "";
        var (late, lateTriangles) = (OcclusionCulling.LateRanges, OcclusionCulling.LateTriangles);
        return $"; the camera's opaque terrain a frame: {ranges - hidden + late:0} draw commands of {ranges:0} in the " +
               $"frustum, {(triangles - gone + lateTriangles) / 1e6:0.00} of {triangles / 1e6:0.00} million triangles";
    }

    // Per-pool and per-frame values (origin, the counters) differ by nature
    private static void Verify(TerrainRenderer renderer)
    {
        var differ = new List<string>();
        foreach (var program in renderer.Programs.ToArray().Bounded(256))
            foreach (var u in program.Ported.VertexUniforms.Members.Concat(program.Ported.FragmentUniforms.Members)
                         .ToArray().Bounded(512))
                Compare(program, u, differ);
        _ = Assert(differ.Count <= 256 * 512) && Assert(renderer.Programs.Count <= 256);
        _logger?.Notification("Komet: Vulkan terrain uniforms against OpenGL: {0}",
            differ.Count == 0 ? "all alike" : string.Join("; ", differ.Take(60)));
    }

    private static void Compare(TerrainRenderer.Program program, GlslPort.Uniform u, List<string> differ)
    {
        var location = GL.GetUniformLocation(program.Id, u.Name);
        var (columns, rows) = GlslPort.Shape(u.Type);
        if (location < 0 || columns * rows is 0 or > 16 || !NotNull(differ)) return;
        var held = program.Mirror.Held(u);
        var gl = new float[16];
        GL.GetUniform(program.Id, location, gl);
        var integer = u.Type is "int" or "bool" || u.Type.StartsWith("ivec", StringComparison.Ordinal);
        for (var i = 0; i < Math.Min(columns * rows, 16); i++)
        {
            var mine = i < held.Length ? held[i] : 0;
            var value = integer ? (int)mine : BitConverter.UInt32BitsToSingle(mine);
            var theirs = gl[i]; // an int uniform read as floats comes converted
            if (Math.Abs(value - theirs) > 1e-4 * Math.Max(1, Math.Abs(theirs)))
                differ.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                    $"{program.Name}.{u.Name}[{i}] mirror {value} GL {theirs}"));
        }
    }

    // Lets go of everything for this world before the kernel's OOM killer takes the game
    private static bool MemoryGuard(TerrainRenderer renderer)
    {
        if (!NotNull(renderer) || Environment.TickCount64 - _guarded < GuardMs) return false;
        _guarded = Environment.TickCount64;
        var bytes = VkMemory.Bytes;
        if (bytes > _logged + LogGrowth || bytes + LogGrowth < _logged)
        {
            _logged = bytes;
            _logger?.Notification("Komet: Vulkan memory {0}; {1}", VkMemory.Report(), Held());
        }

        var (available, total) = VulkanWatch.SystemMemory();
        var low = total > 0 && available < total / 12;
        if (!low && bytes < Budget) return false;
        Fail($"Vulkan let go for this world: {(low ? "the system ran low on memory" : "Vulkan used too much")} " +
             $"({available >> 20} of {total >> 20} MB available); Vulkan held {VkMemory.Report()}");
        _logger?.Notification("Komet: Vulkan memory after letting go: {0}", VkMemory.Report());
        return Assert(_failed) && Assert(_renderer is null);
    }
}
