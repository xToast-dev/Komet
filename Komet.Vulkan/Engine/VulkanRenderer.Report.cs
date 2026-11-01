using System.Globalization;
using Komet.Chunks;
using Komet.Diagnostics;
using Komet.Rendering;
using Komet.Shapes;

namespace Komet.Vulkan;

// The status line, the HUD line and the periodic log report, Komet's holdings beside them, and the memory guard.
internal static partial class VulkanRenderer
{
    private const ulong LogGrowth = 512UL << 20;
    private const int FaceGroupBytes = 280; // a FaceSorting.Sorted with its four arrays and its entry
    private const long GuardMs = 250;
    private const int ReportEvery = 600, LogEvery = 3600, LeanEvery = 600;
    private static ulong _logged;
    private static long _guarded;
    private static string _waits = "";

    // The renderer's running totals, at the last report and over the window of frames up to it
    private readonly record struct Totals(long Frames, long Draws, long SceneDraws, long Missed, long Segments, long Bridged)
    {
        // Totals only grow while one renderer lives (Release starts them anew)
        public Totals Since(Totals then)
        {
            return new Totals(Frames - then.Frames, Draws - then.Draws, SceneDraws - then.SceneDraws, Missed - then.Missed,
                Segments - then.Segments, Bridged - then.Bridged);
        }
    }

    private static Totals _reported, _window;

    private static Totals Now(TerrainRenderer renderer)
    {
        return new Totals(renderer.Frame.Frames, renderer.Draws, _scene?.Draws ?? 0, renderer.Missed + renderer.MissedWaiting,
            renderer.Frame.Segments, renderer.Bridged);
    }

    // The last report's window; before the first, everything since the renderer began
    private static Totals Window(TerrainRenderer renderer) =>
        _window.Frames > 0 ? _window : Now(renderer);

    private static void Report(string waits)
    {
        var phase = waits.Length > 0 ? Phase.Waiting : Phase.Running;
        if (phase != State || waits != _waits)
        {
            Status = waits.Length > 0 ? waits : "drawing: " + Passes();
            if (waits.Length > 0 || State != Phase.Running) _logger?.Notification("Komet: Vulkan {0}", Status);
        }

        (State, _waits) = (phase, waits);
    }

    // Every terrain pass, the rest of the frame with the scene, and the window when Vulkan shows it
    private static string Passes()
    {
        var shadows = ShadowView.Differed
            ? "shadow maps"
            : $"shadow maps (casters culled by the view, {EntityCasters.Culled} of {EntityCasters.Tested} entity shadows)";
        var opaque = OcclusionCulling.Drawer is null ? "opaque" : "opaque (culled on the GPU)";
        var rest = SceneOn ? ", and of the rest sky, entities, particles, post-processing, the rest" : "";
        var window = "";
        if (_windowing) window = _renderer?.Showing == true ? ", the window, shown by Vulkan" + Unshown() : ", the window, not shown";
        return $"liquid depth, {shadows}, {opaque}, transparent, water plants{rest}{window}";
    }

    private static string Unshown() =>
        _renderer is { Unshown: > 0 } renderer
            ? $" ({renderer.Unshown} frames Vulkan could not show, the window kept the last)"
            : "";

    private static void Periodically(TerrainRenderer renderer)
    {
        var frame = renderer.Frame.Number;
        if (frame % LeanEvery == LeanEvery / 2 && LeanOn && renderer.LeanEarlier() is (> 0, var bytes) lean)
            _logger?.Notification("Komet: Vulkan-only lean: {0} textures OpenGL held from before ({1} MB) given a stand-in",
                lean.Count, bytes >> 20);
        if (frame % ReportEvery != 1) return;
        var now = Now(renderer);
        (_window, _reported) = (now.Since(_reported), now);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture; // "1.5 ms" in the English report whatever the game's language
        try
        {
            Status = (_waits.Length > 0 ? _waits + "; " : "") + Short(renderer);
            // frame 1 has nothing to report yet, and the first window (frames 1-600) is the post-start upload burst whose
            // per-pass times are not steady state: the first full report comes at frame 1201
            if ((frame % LogEvery == 1 && frame > 1) || frame == 2 * ReportEvery + 1)
                _logger?.Notification("Komet: {0}", Full(renderer));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    // A frame's average over the last report's window; zero while Vulkan does not run
    internal static (double Draws, double Left, double Segments, long MemoryMb, string[] Missing) Snapshot()
    {
        if (State != Phase.Running || _renderer is not { } renderer) return (0, 0, 0, 0, []);
        var window = Window(renderer);
        var frames = Math.Max(window.Frames, 1);
        var draws = window.Draws + window.SceneDraws;
        var missing = renderer.MissedTop(6);
        // what Vulkan leaves is missing from the picture, OpenGL draws none of it
        return ((double)draws / frames, (double)window.Missed / frames, (double)window.Segments / frames,
            (long)(VkMemory.Bytes >> 20), missing);
    }

    // A long HUD line made the panel slow to draw; the full report goes to the log
    private static string Short(TerrainRenderer renderer) => OnlyShort(renderer, Window(renderer));

    private static string Full(TerrainRenderer renderer)
    {
        var frame = renderer.Frame;
        var frames = Math.Max(frame.Frames, 1);
        var ms = FrameClock.TickMs / frames;
        return $"terrain in Vulkan ({Passes()}): {frames} frames, {(double)renderer.Draws / frames:0} pool draws and " +
               $"{(double)frame.Segments / frames:0.0} segments a frame, {frame.SubmitTicks * ms:0.00} ms submitting a frame, " +
               $"{renderer.Pools.Count} pools ({renderer.Pools.Bytes >> 20} MB: {renderer.Pools.Breakdown()}, {Born} adopted as made), " +
               OnlyHeld(renderer) + "; closed by " +
               string.Join(", ", frame.Closers.OrderByDescending(c => c.Value).Take(8).Select(c => $"{c.Key} {c.Value}")) +
               Culled(renderer, frames) + SceneReport(frames) + renderer.Frame.Times() +
               Hitches.Report() +
               Builds.Report() + ShaderCache.Report() + "; memory " +
               VkMemory.Report() + "; " + Held();
    }

    private static string SceneReport(long frames)
    {
        if (_scene is not { } scene) return "";
        var ms = FrameClock.TickMs / frames;
        var mirrors = scene.Mirrors;
        return $"; the rest of the frame: {(double)scene.Draws / frames:0} draws, {(double)scene.Clears / frames:0.0} " +
               $"clears, {(double)scene.Blits / frames:0.0} blits, " +
               $"{(double)scene.Left / frames:0.0} missing a frame, recording {scene.Ticks * ms:0.00} ms a frame; {mirrors.Count} buffer mirrors, {mirrors.Bytes >> 20} MB, " +
               $"{mirrors.Writes} writes, {mirrors.Renames} renamed, {mirrors.Imports} read back " +
               $"({mirrors.ImportedBytes >> 10} KB, {mirrors.Asked} sizes asked of OpenGL)" +
               (mirrors.Imports > 0 ? $" ({mirrors.Reasons})" : "");
    }

    // The HUD line over the last report's window: segments, and what is missing from the picture
    private static string OnlyShort(TerrainRenderer renderer, Totals window)
    {
        var frames = Math.Max(window.Frames, 1);
        var scene = _scene is not null ? $"{(double)window.SceneDraws / frames:0} other draws, " : "";
        return $"Vulkan-only draws {Passes()}: {(double)window.Draws / frames:0} pool draws, {scene}" +
               $"{(double)window.Segments / frames:0.0} segments a frame, " +
               $"{(double)window.Missed / frames:0.0} calls missing a frame " +
               $"({renderer.MissedKind} kinds{(renderer.Missed > 0 ? $": {renderer.MissedKinds(8)}" : "")}){(Bridging ? $", {(double)window.Bridged / frames:0.00} frames bridged a frame" : "")}; " +
               $"{VkMemory.Bytes >> 20} MB";
    }

    // The images and mappings of its own, and what it had to read back from OpenGL
    private static string OnlyHeld(TerrainRenderer renderer)
    {
        if (renderer.Images is not { } images || renderer.Maps is not { } maps) return "";
        return $"Vulkan-only: {images.Count} texture images (about {images.Bytes >> 20} MB), {images.Uploads} uploads, " +
               $"mipmaps and copies made by Vulkan, {images.Readbacks} read back from OpenGL ({images.ReadBytes >> 20} MB), " +
               $"{images.Unseen} changes it could not follow, {images.Refused} uploads or images it had no room or format " +
               $"for, {GlTap.UntrackedCalls} calls on textures it does not follow; {maps.Count} persistent mappings of " +
               $"its own ({maps.Bytes >> 20} MB, {maps.Leaked} let go unredirected), {renderer.PoolWrites} pool uploads ({renderer.PoolWritesLost} lost), " +
               $"{renderer.Pools.Readbacks} pool buffers read back from OpenGL; {renderer.Answered} OpenGL reads answered " +
               $"from Vulkan's images; {renderer.Missed} GPU calls missing from the picture" +
               (renderer.Missed > 0 ? $" ({renderer.MissedKinds(6)})" : "") +
               $", {renderer.MissedWaiting} draws missing while their port or pipeline was made" +
               (_scene is { Unported.Length: > 0 } scene ? $"; programs with no Vulkan port: {scene.Unported}" : "") +
               $"; {renderer.Snapshots} snapshots of images drawn into while sampled" + Warmed(renderer) +
               $"; {renderer.Frame.EarlyBegins} frames begun by work between frames, {GlTap.MultiDrawRanges} multi-draw " +
               $"ranges taken one by one (at most {GlTap.LargestMultiDraw} in one), " +
               $"{_scene?.Mirrors.PersistentReads ?? 0} inputs read from persistent mappings without OpenGL's fences, " +
               $"{_scene?.Mirrors.StagingRefused ?? 0} uploads with no staging room" +
               (OcclusionCulling.Suspended ? "; OcclusionCulling suspended (the device draws with no count the GPU wrote)" : "") +
               (GlTap.Leaned > 0
                   ? $"; lean: {GlTap.Leaned} large textures and buffers ({GlTap.LeanBytes >> 20} MB) kept out of OpenGL's " +
                     $"memory, {GlTap.LeanMapped} given back for a map"
                   : "") + ForMods(renderer.Device);
    }

    // What the device gives mods' raw OpenGL (VulkanDevice)
    private static string ForMods(VulkanDevice device)
    {
        var stencil = device.DepthStencil switch { Vk.FormatD24S8 => "D24S8", Vk.FormatD32S8 => "D32S8", _ => "none" };
        return $"; for mods: depth and stencil {stencil}, 8-bit indices {(device.IndexUint8 ? "yes" : "no")}, instance " +
               $"divisors past 1 {(device.Divisors ? "yes" : "no")}, several indirect draws at once " +
               $"{(device.MultiDraws ? "yes" : "no")}";
    }

    private static string Warmed(TerrainRenderer renderer) =>
        _scene is { } scene
            ? $"; {scene.PipelinesStarted} scene and {renderer.PipelinesStarted} terrain pipelines begun before their first " +
              "draw (known from earlier starts)"
            : "";

    private static string Culled(TerrainRenderer renderer, long frames)
    {
        if (OcclusionCulling.Drawer is null) return "";
        return $"; OcclusionCulling in Vulkan: {(double)renderer.Counted / frames:0.0} draws a frame, " +
               $"{OcclusionCulling.Refused} refused, {renderer.Compute.Refused} passes without a segment or memory" +
               (OcclusionCulling.RowsUsed > 0
                   ? $", from rows ({OcclusionCulling.RowsUsed} rows, {OcclusionCulling.Rewrites} regions written in full, {OcclusionCulling.Resyncs} brought up to date)"
                   : "") + $", {(double)OcclusionCulling.SortedDraws / frames:0.0} draws a frame sorted near to far, shadow casters " +
               (ShadowView.Differed ? "no longer culled by the view (it differed from the engine's)" : "culled by the view") +
               DrawnTerrain();
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

    // Lets go of everything for this world before the kernel's OOM killer takes the game. Video memory is not counted:
    // the driver pages it, and a device's own memory is no system memory.
    private static bool MemoryGuard()
    {
        if (Environment.TickCount64 - _guarded < GuardMs) return false;
        _guarded = Environment.TickCount64;
        var bytes = VkMemory.Bytes;
        if (bytes > _logged + LogGrowth || bytes + LogGrowth < _logged)
        {
            _logged = bytes;
            _logger?.Notification("Komet: Vulkan memory {0}; {1}", VkMemory.Report(), Held());
        }

        var (available, total) = VulkanWatch.SystemMemory();
        if (total <= 0 || available >= total / 12) return false;
        Fail($"Vulkan let go for this world: the system ran low on memory ({available >> 20} of {total >> 20} MB " +
             $"available); Vulkan held {VkMemory.Report()}");
        _logger?.Notification("Komet: Vulkan memory after letting go: {0}", VkMemory.Report());
        return true;
    }

    // Komet's largest holdings next to the Vulkan memory report: does the managed heap grow with Komet or the game. Bytes
    // are estimates (entry sizes); chunk lookups are the engine's chunks.
    internal static string Held()
    {
        var (mirrors, mirrorBytes) = _scene?.Mirrors is { } m ? (m.Count, m.ManagedBytes) : (0, 0);
        var tap = GlTap.Held;
        return $"Komet holds {mirrors} buffer mirrors " +
               $"({mirrorBytes >> 10} KB managed), the tap's {tap.Programs} programs, {tap.Arrays} vertex arrays, " +
               $"{tap.Textures} texture targets, {tap.Framebuffers} framebuffers ({tap.Bytes >> 10} KB), " +
               $"{FaceSorting.Located} face groups ({(long)FaceSorting.Located * FaceGroupBytes >> 10} KB), " +
               $"{ChunkLookup.Count} chunk lookups, {AnimationFrames.Count} animation sets" + Record();
    }

    // The record: what it follows, and how often it had to ask OpenGL for an object made before it
    private static string Record()
    {
        if (!GlTap.Tapped) return "";
        var held = GlTap.Records;
        return $", the Vulkan-only record of {held.Textures} textures, {held.Framebuffers} framebuffers, " +
               $"{held.Programs} programs ({held.Linked} linked under it), {held.Buffers} buffer sizes " +
               $"({held.Misses} objects asked from OpenGL)";
    }
}
