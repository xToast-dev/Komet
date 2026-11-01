using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.Client;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// GPU occlusion culling of the opaque terrain in two passes, so nothing shows a frame late; only while Vulkan draws it. Each RenderOpaque Render
// call runs as written, but its pool draws are held (IndirectDraw.Intercept) and issued in the postfix, under the
// pass's program, textures and blending, from commands cull.comp writes. Surface passes (opaque 0, topsoil 5): pass 0
// against the previous frame's pyramid (shifted by the camera step); after the last, this frame's pyramid is built from
// the ground drawn so far and pass 1 retests what pass 0 hid, drawing it late under its own call's program and
// textures. Later passes: pass 0 against this frame's pyramid. Late draws cannot wait for the whole terrain: later
// passes change opaque uniforms and blending.
internal static partial class OcclusionCulling
{
    public const int MaxRanges = 1 << 19, MaxDraws = 4096;
    private const int MaxPools = 4096, MaxPasses = 16, MaxAtlases = 64, Opaque = 0, Topsoil = 5;
    private const float Far = 1e7f;

    // Ranges for upload where the backend has no staging (Unstaged), made at first use
    private static Occlusion.Range[] Ranges = [];
    // first range per pool draw for cull.comp; shadow draws from MaxDraws on
    private static readonly uint[] Starts = new uint[2 * MaxDraws];
    private static readonly Draw[] Draws = new Draw[2 * MaxDraws];
    private static readonly Vec3f?[] Offsets = new Vec3f?[2 * MaxDraws];
    private static readonly List<MeshRef> Held = [], Mini = [];
    private static readonly System.Func<MeshRef, bool, bool> Holder = Hold;
    private static ICoreClientAPI? _api;
    private static ILogger? _logger;
    private static ChunkRenderer? _renderer;
    private static bool _opaque, _holding, _useSsbos, _surface, _lastSurface;
    private static Vec3d? _camera; // the frame's: Eye, set by its first held call (no copy a frame)
    private static readonly Vec3d Eye = new();
    private static (double X, double Y, double Z) _view; // the eye relative to Eye: the engine culls from the player's feet
    private static string _origin = "origin";

    public static bool Enabled { get; set; } = true;
    public static bool Installed { get; private set; }

    // The knob's one chain: 0 the engine's culling, 1 culled against a depth pyramid, 2 the terrain culled fully on the GPU
    // (rows) with back-facing directions left out (FaceSorting). Each step needs the one before.
    public static int Level
    {
        get
        {
            if (!Enabled) return 0;
            return RowsEnabled ? 2 : 1;
        }
        set => (Enabled, RowsEnabled) = (value >= 1, value >= 2);
    }

    // Vulkan-only device without GPU-count draws: off for now, switch kept
    public static bool Suspended { get; set; }

    internal static bool Active => Enabled && !Suspended;

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Clear();
        if (!NotNull(harmony) || !NotNull(api) || !NotNull(logger)) return;
        (_api, _logger) = (api, logger);
        var self = typeof(OcclusionCulling);
        var opaque = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque),
            [typeof(float)]);
        var render = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var bind = AccessTools.DeclaredMethod(typeof(ShaderProgramBase), nameof(ShaderProgramBase.BindTexture2D),
            [typeof(string), typeof(int), typeof(int)]);
        if (!NotNull(opaque) || !NotNull(render) || !NotNull(bind) ||
            !Assert(Il.Binds(render, AccessTools.Method(self, nameof(Holding)))) ||
            !Assert(AccessTools.Field(typeof(ChunkRenderer), "poolsByRenderPass") != null)) return;
        _ = harmony.Patch(opaque, new HarmonyMethod(AccessTools.Method(self, nameof(Opening))),
            new HarmonyMethod(AccessTools.Method(self, nameof(Closing))) { priority = Priority.Last });
        _ = harmony.Patch(render,
            new HarmonyMethod(AccessTools.Method(self, nameof(Holding))) { priority = Priority.First },
            new HarmonyMethod(AccessTools.Method(self, nameof(Issuing))) { priority = Priority.Last });
        _ = harmony.Patch(bind, postfix: new HarmonyMethod(AccessTools.Method(self, nameof(Bound))));
        var shadow = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)]);
        if (NotNull(shadow))
            _ = harmony.Patch(shadow, new HarmonyMethod(AccessTools.Method(self, nameof(ShadowOpening))),
                new HarmonyMethod(AccessTools.Method(self, nameof(ShadowClosing))) { priority = Priority.Last });
        IndirectDraw.Intercept = Holder;
        InstallRows(harmony);
        Installed = true;
    }

    public static void Clear()
    {
        if (ReferenceEquals(IndirectDraw.Intercept, Holder)) IndirectDraw.Intercept = null;
        (_opaque, _holding, _camera, _renderer, Installed) = (false, false, null, null, false);
        (_casting, _shadowing, SortedDraws, _planned) = (false, false, 0, 0);
        (_api, _logger) = (null, null);
        Held.Clear();
        Mini.Clear();
        // the last frames' pools (location lists and all), VAOs and programs, or they outlive their world until as many
        // draws come again
        Array.Clear(Plan);
        Array.Clear(Draws);
        Array.Clear(Records);
        Array.Clear(Snapshots);
        Release();
    }

    // A Render call that threw past its postfix left draws held; released here, or every later draw would vanish. A
    // shadow pass that threw past ShadowClosing would send this pass's calls down the shadow path.
    private static void Opening(ChunkRenderer __instance)
    {
        _ = Assert(!_holding);
        (_holding, _renderer, _rowsCall, _shadowing, _shadowCall) = (false, __instance, false, false, false);
        Links.Mark("culling: the opaque terrain begins");
        _opaque = Active && VulkanDraws && NotNull(__instance) && Ready(); // OpenGL keeps the engine's culling
        if (_opaque) BeginFrame();
    }

    private static void Closing()
    {
        _ = Assert(!_holding);
        _holding = false;
        if (!_opaque) return;
        if (_late) FinishSurfaces(); // no topsoil call came to do it
        _opaque = false;
        EndFrame();
    }

    private static void Holding(MeshDataPoolManager __instance, Vec3d playerpos, string originUniformName,
        EnumFrustumCullMode frustumCullMode)
    {
        if (_shadowing)
        {
            HoldingShadow(__instance, playerpos, originUniformName, frustumCullMode);
            return;
        }

        if (!_opaque || !NotNull(__instance) || !NotNull(playerpos) || Pools(__instance) is not { } pools) return;
        var (pass, last) = Place(__instance);
        var surface = !_builtThisFrame && pass is Opaque or Topsoil;
        if (!surface && _late) FinishSurfaces(); // a pass after the surfaces although topsoil never finished them
        if (!surface && !_builtThisFrame) return; // before the ground is down only the surfaces are culled
        var locations = Scan(pools);
        var rows = _rowsFrame;
        var backFaces = rows && pass is Opaque or Topsoil && BackFacesCulled();
        if (!Room(locations * (backFaces ? Runs : 1), pools.Count) ||
            (rows && !Rowed(Culler(__instance), pools, rows))) return;
        if (_camera is null)
            (Eye.X, Eye.Y, Eye.Z, _camera, _view) = (playerpos.X, playerpos.Y, playerpos.Z, Eye, Seen());
        (_holding, _origin, _surface, _lastSurface) = (Capture(), originUniformName, surface, pass == Topsoil && last);
        _rowsCall = _holding && rows; // the GPU now does the engine's FrustumCull of these pools
        _backFaces = _rowsCall && backFaces;
        Held.Clear();
    }

    // View matrix eye (-Rᵀt): LocalEyePos plus third person distance
    private static (double, double, double) Seen()
    {
        if (_api?.Render?.CurrentModelviewMatrix is not { Length: 16 } m) return default;
        var (x, y, z) = ((double)m[12], (double)m[13], (double)m[14]);
        var view = (-(m[0] * x + m[1] * y + m[2] * z), -(m[4] * x + m[5] * y + m[6] * z), -(m[8] * x + m[9] * y + m[10] * z));
        return Finite(view.Item1) && Finite(view.Item2) && Finite(view.Item3) ? view : default;
    }

    private static int Scan(List<MeshDataPool> pools)
    {
        var locations = 0;
        Mini.Clear();
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            locations += Locations(pools[i]).Count;
            if (Dimension(pools[i]) != 0) Mini.Add(ModelRef(pools[i]));
        }

        return locations;
    }

    private static (int Pass, bool Last) Place(MeshDataPoolManager manager)
    {
        if (_renderer is null || PassPools(_renderer) is not { } passes) return (-1, false);
        for (var pass = 0; pass < Math.Min(passes.Length, MaxPasses); pass++)
        {
            var atlases = passes[pass];
            if (atlases is null) continue;
            var at = Array.IndexOf(atlases, manager);
            if (at >= 0) return (pass, LastAtlas(atlases, at));
        }

        return (-1, false);
    }

    // A pass's array holds three atlases more than are made (RuntimeAddBlockTextureAtlas fills them), so the last one
    // drawn is the last set, not the array's last
    private static bool LastAtlas(MeshDataPoolManager[] atlases, int at)
    {
        for (var i = at + 1; i < Math.Min(atlases.Length, MaxAtlases); i++)
            if (atlases[i] is not null) return false;
        return true;
    }

    private static bool Hold(MeshRef modelRef, bool useSSBOs)
    {
        if (!_holding || modelRef is not VAO || Mini.Contains(modelRef) || !Assert(Held.Count < MaxPools)) return false;
        Held.Add(modelRef);
        _useSsbos = useSSBOs;
        return true;
    }

    // Render draws pools in order, so the held one is nearly always next
    private static bool Taken(MeshRef model, ref int next)
    {
        if (!NotNull(model)) return false;
        if (next == Held.Count || !ReferenceEquals(Held[next], model)) return Held.Contains(model);
        next++;
        return true;
    }

    private static void Issuing(MeshDataPoolManager __instance)
    {
        if (!_holding) return;
        var rows = _rowsCall;
        (_holding, _rowsCall) = (false, false);
        var (pools, shadow) = (Pools(__instance), _shadowCall);
        _shadowCall = false;
        if (rows && shadow && NotNull(pools) && Held.Count > 0)
        {
            IssuingShadow(Culler(__instance), pools);
            return;
        }

        if (!NotNull(pools) || Held.Count == 0 || _camera is not { } camera)
        {
            if (_lastSurface && _late) FinishSurfaces(); // a last surface call that drew nothing (no topsoil there)
            return;
        }

        if (rows)
        {
            IssuingRows(Culler(__instance), pools, camera);
            return;
        }

        var (first, draws) = (_frameRanges, _frameDraws);
        var (ranges, held) = (first, 0);
        _planned = 0;
        for (var i = 0; i < Math.Min(pools.Count, MaxPools); i++)
        {
            var pool = pools[i];
            if (Dimension(pool) != 0 || !Taken(ModelRef(pool), ref held) || !Index(draws, MaxDraws)) continue;
            var count = Math.Clamp(pool.indicesGroupsCount, 0, MaxRanges - ranges);
            Plan[_planned++] = new Planned(pool, ranges, count);
            Drawn(draws++, pool, camera, ranges, count);
            ranges += count;
        }

        Held.Clear();
        var staged = Fill((first, ranges - first), (camera.X, camera.Y, camera.Z));
        Cull((first, ranges - first), (_frameDraws, draws - _frameDraws), staged: staged);
        if (_lastSurface && _late) FinishSurfaces();
    }

    // A located-less range must still draw (list goes up unboxed). Reads only pool and mirror: safe in parallel.
    private static int Gather(MeshDataPool pool, Span<Occlusion.Range> into, (double X, double Y, double Z) camera)
    {
        var swept = FrustumSweep.Boxes(pool, into, camera);
        if (swept >= 0) return swept;
        var written = Occlusion.Match(pool.indicesStartsByte, pool.indicesSizes, pool.indicesGroupsCount,
            Locations(pool), into, camera, out var unmatched);
        return unmatched == 0
            ? written
            : Unboxed(pool.indicesStartsByte, pool.indicesSizes, pool.indicesGroupsCount, into);
    }

    // Ranges no test can hide (boxes reaching behind the camera, which cull.comp keeps)
    internal static int Unboxed(int[] starts, int[] sizes, int groups, Span<Occlusion.Range> into)
    {
        if (!NotNull(starts) || !NotNull(sizes) || !Assert(groups >= 0)) return 0;
        var count = Math.Min(Math.Min(groups, sizes.Length), Math.Min(starts.Length / 2, into.Length));
        for (var i = 0; i < Math.Min(count, MaxRanges); i++)
            into[i] = new Occlusion.Range(-Far, -Far, -Far, sizes[i], Far, Far, Far, starts[2 * i] / 4);
        return count;
    }

    // Origin uniform into the draw's own Vec3f, kept across frames
    private static void Drawn(int d, MeshDataPool pool, Vec3d camera, int start, int count)
    {
        var (origin, offset) = (PoolOrigin(pool), Offsets[d] ??= new Vec3f());
        (offset.X, offset.Y, offset.Z) = !NotNull(origin)
            ? (0f, 0f, 0f)
            : ((float)(origin.X - camera.X), (float)(origin.Y - camera.Y), (float)(origin.Z - camera.Z));
        (Starts[d], Draws[d]) = ((uint)start, new Draw((VAO)ModelRef(pool), offset, start, count));
    }

    private readonly record struct Draw(VAO Vao, Vec3f Offset, int Start, int Count);
}

// A held call's ranges go into backend staging (Vulkan) or Ranges for upload (GL). Pool places are known ahead, so big
// calls gather per pool on free workers. A pool with fewer ranges than its list leaves empty ones (draw nothing).
internal static unsafe partial class OcclusionCulling
{
    private const int ParallelRanges = 4096, RangesPerHelper = 8192;

    private static readonly Planned[] Plan = new Planned[MaxDraws];
    private static readonly Action<int> GatherJob = GatherOne;
    private static int _planned, _stagedFirst;
    private static Occlusion.Range* _staged;
    private static (double X, double Y, double Z) _eye;

    private readonly record struct Planned(MeshDataPool Pool, int At, int Count);

    // true when they went into backend staging
    private static bool Fill((int First, int Count) ranges, (double X, double Y, double Z) camera)
    {
        if (ranges.Count <= 0) return false;
        _staged = (Occlusion.Range*)Gpu.Staged(_ranges, ranges.First * RangeBytes, ranges.Count * RangeBytes);
        (_stagedFirst, _eye) = (ranges.First, camera);
        if (_staged == null) _ = Unstaged(); // made here: the workers only write into it
        var parallel = ranges.Count >= ParallelRanges && _planned > 1 && WorkerPool.Free &&
                       WorkerPool.RunFrame(GatherJob, _planned, ranges.Count / RangesPerHelper) ==
                       WorkerPool.FrameResult.Done;
        if (!parallel)
            for (var j = 0; j < Math.Min(_planned, MaxDraws); j++) GatherOne(j); // idempotent: a failed batch runs again
        var staged = _staged != null;
        _staged = null;
        return staged;
    }

    // Into backend staging at first, as Fill does (GPU tests); false when it has none
    internal static bool Stage(ReadOnlySpan<Occlusion.Range> ranges, int first)
    {
        if (ranges.IsEmpty || !Assert(first >= 0 && first + ranges.Length <= MaxRanges)) return false;
        var staged = (Occlusion.Range*)Gpu.Staged(_ranges, first * RangeBytes, ranges.Length * RangeBytes);
        if (staged == null) return false;
        ranges.CopyTo(new Span<Occlusion.Range>(staged, ranges.Length));
        return true;
    }

    // Ranges, made on the main thread at first use
    private static Occlusion.Range[] Unstaged()
    {
        if (Ranges.Length == 0) Ranges = new Occlusion.Range[MaxRanges];
        return Ranges;
    }

    private static void GatherOne(int j)
    {
        if (!Index(j, _planned)) return;
        var (pool, at, count) = Plan[j];
        var into = _staged != null
            ? new Span<Occlusion.Range>(_staged + (at - _stagedFirst), count)
            : Ranges.AsSpan(at, count);
        var written = Gather(pool, into, _eye);
        if (written < count) into[Math.Max(written, 0)..].Clear();
    }
}

// Shadow passes cull from the same rows, without occlusion, while Vulkan draws them. Draws (MaxDraws on), commands (ShadowBase on), counts (after pass 1) have
// their own place so camera passes never overwrite them. Casters culled by the camera view (ShadowView) when made.
internal static partial class OcclusionCulling
{
    private static bool _shadowing, _shadowCall, _casting;
    private static int _shadowMode, _shadowRanges, _shadowDraws = MaxDraws, _shadowFrame = -1;
    private static Vec3d? _shadowCamera;

    // Under Vulkan: whether it takes GL's draws now; counted draws have no GL fallback, so only then is a call held
    internal static Func<bool>? Takes { get; set; }

    private static void ShadowOpening()
    {
        _ = Assert(!_holding);
        (_holding, _rowsCall, _shadowCall) = (false, false, false); // as Opening: a call that threw left them set
        _shadowing = Active && RowsEnabled && VulkanDraws && Ready() && _rowsReady;
        _casting = _shadowing && ShadowView.Casting;
    }

    private static void ShadowClosing()
    {
        _ = Assert(!_holding);
        (_shadowing, _shadowCall, _casting) = (false, false, false);
    }

    private static void HoldingShadow(MeshDataPoolManager manager, Vec3d playerpos, string origin, EnumFrustumCullMode mode)
    {
        var pass = mode switch
        {
            EnumFrustumCullMode.CullInstantShadowPassNear => 1,
            EnumFrustumCullMode.CullInstantShadowPassFar => 2,
            _ => 0
        };
        if (pass == 0 || !NotNull(manager) || !NotNull(playerpos) || Pools(manager) is not { } pools) return;
        if (VulkanDraws && Takes?.Invoke() != true) return; // e.g. DistantShadows' own map: OpenGL's, culled by the CPU
        if (_shadowFrame != _frame) (_shadowRanges, _shadowDraws, _shadowFrame) = (0, MaxDraws, _frame);
        var locations = Scan(pools);
        if (_shadowRanges + locations > MaxRanges || _shadowDraws + pools.Count > 2 * MaxDraws ||
            !Rowed(Culler(manager), pools, true)) return;
        (_holding, _rowsCall, _shadowCall, _shadowCamera, _shadowMode, _origin) =
            (true, true, true, playerpos, pass, origin);
        Held.Clear();
    }

    private static void IssuingShadow(FrustumCulling? culler, List<MeshDataPool> pools)
    {
        if (culler is null || _shadowCamera is not { } camera) return;
        var (first, cursor) = (_shadowRanges, _shadowRanges);
        var draws = Lay(pools, camera, ref cursor, (_shadowDraws, 2 * MaxDraws), 1);
        if (draws > _shadowDraws)
            CullShadow((first, cursor - first), (_shadowDraws, draws - _shadowDraws), culler, camera);
    }

    private static void CullShadow((int First, int Count) ranges, (int First, int Count) draws, FrustumCulling culler,
        Vec3d camera)
    {
        if (!Assert(draws.First >= MaxDraws && draws.First + draws.Count <= 2 * MaxDraws) ||
            !Assert(ranges.First + ranges.Count <= MaxRanges)) return;
        var counters = CounterBuffers[_frame % Ring];
        UploadRows(draws, culler, camera, (_shadowMode, false));
        Prime(draws, counters, both: false);
        BindRows(counters);
        RowsPass(ViewProjection, (0, 0, 0), (1, Keep, StatUints + MaxDraws, ShadowBase), draws, _callMaxRows);
        Issue(draws, (ShadowBase, StatUints + MaxDraws), counters);
        (_shadowRanges, _shadowDraws) = (ranges.First + ranges.Count, draws.First + draws.Count);
        Gpu.Texture(DepthPyramid.Unit, 0);
    }

    // One shadow call as its own frame (GPU tests), casters culled by the view when given
    internal static bool RunShadow(List<MeshDataPool> pools, FrustumCulling culler, Vec3d camera, EnumFrustumCullMode mode,
        FrustumCulling? view = null)
    {
        if (!NotNull(pools) || !NotNull(culler) || !Ready() || !_rowsReady) return false;
        BeginFrame(true);
        _shadowing = RowsEnabled;
        ShadowView.Use(view);
        _casting = view is not null;
        var manager = (MeshDataPoolManager)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(MeshDataPoolManager));
        (Pools(manager), Culler(manager)) = (pools, culler);
        HoldingShadow(manager, camera, "origin", mode);
        var held = _holding;
        foreach (var pool in pools.Bounded(MaxPools))
            if (held && ModelRef(pool) is { } model)
                Held.Add(model);
        if (held) Issuing(manager);
        ShadowClosing();
        ShadowView.Use(null);
        EndFrame();
        return held;
    }
}

// Late surface draws follow topsoil: each surface call records its program and bound textures to switch back and rebind
internal static partial class OcclusionCulling
{
    private const int MaxRecords = 64, MaxBindings = 256, MaxSnapshots = 1024;

    private static readonly List<Binding> Bindings = [];
    private static readonly Binding[] Snapshots = new Binding[MaxSnapshots];
    private static readonly Record[] Records = new Record[MaxRecords];
    private static int _snapshots, _records;
    private static bool _replaying;

    // Vulkan pool draw from backend command/count buffers (byte offsets), at most max; false if refused; null while OpenGL
    // draws the terrain (then nothing is culled)
    internal delegate bool CountedDraw(VAO vao, bool ssbo, (int Buffer, int Offset) commands, (int Buffer, int Offset) count,
        int max);

    internal static CountedDraw? Drawer { get; set; }

    // Draws Vulkan refused (terrain missing from that frame)
    public static long Refused { get; private set; }

    private static void Bound(ShaderProgramBase __instance, string samplerName, int textureId, int textureNumber)
    {
        if (!_opaque || _replaying || !NotNull(__instance) || !NotNull(samplerName)) return;
        var at = -1;
        for (var i = 0; i < Math.Min(Bindings.Count, MaxBindings) && at < 0; i++) // every bind: no closure
            if (ReferenceEquals(Bindings[i].Program, __instance) && Bindings[i].Sampler == samplerName)
                at = i;
        var binding = new Binding(__instance, samplerName, textureId, textureNumber);
        if (at >= 0) Bindings[at] = binding;
        else if (Bindings.Count < MaxBindings) Bindings.Add(binding);
    }

    private static void ClearBindings()
    {
        Bindings.Clear();
        (_snapshots, _records, _replaying) = (0, 0, false);
    }

    private static void Remember((int First, int Count) draws)
    {
        if (!Index(_records, MaxRecords) || !Assert(draws.Count > 0)) return;
        var program = ShaderProgramBase.CurrentShaderProgram;
        var first = _snapshots;
        for (var i = 0; i < Math.Min(Bindings.Count, MaxBindings) && _snapshots < MaxSnapshots; i++)
            if (ReferenceEquals(Bindings[i].Program, program))
                Snapshots[_snapshots++] = Bindings[i];
        Records[_records++] = new Record(program, first, _snapshots - first, draws.First, draws.Count);
    }

    private static void IssueLate(int counters)
    {
        if (!Assert(counters != 0)) return;
        var before = ShaderProgramBase.CurrentShaderProgram;
        _replaying = true;
        try
        {
            for (var r = 0; r < Math.Min(_records, MaxRecords); r++)
            {
                var record = Records[r];
                Switch(record.Program);
                for (var s = record.First; s < Math.Min(record.First + record.Textures, MaxSnapshots); s++)
                    Snapshots[s].Program.BindTexture2D(Snapshots[s].Sampler, Snapshots[s].Texture, Snapshots[s].Unit);
                Issue((record.DrawFirst, record.DrawCount), (MaxRanges, StatUints + MaxDraws), counters);
            }

            Switch(before);
            for (var b = 0; b < Math.Min(Bindings.Count, MaxBindings); b++)
                if (before is not null && ReferenceEquals(Bindings[b].Program, before))
                    before.BindTexture2D(Bindings[b].Sampler, Bindings[b].Texture, Bindings[b].Unit);
        }
        finally
        {
            _replaying = false;
        }
    }

    // Stop, then Use: Use refuses while another program is in use
    private static void Switch(ShaderProgramBase? program)
    {
        var active = ShaderProgramBase.CurrentShaderProgram;
        if (ReferenceEquals(active, program) || !Assert(program is null || !program.Disposed)) return;
        active?.Stop();
        program?.Use();
    }

    // Pass 0 at (0, StatUints), pass 1 at (MaxRanges, StatUints + MaxDraws); sorted: from the Sorted buffer
    private static void Issue((int First, int Count) draws, (int Commands, int Counts) at, int counters, bool sorted = false)
    {
        if (_api?.Render.CurrentActiveShader is not { } shader || !Assert(counters != 0) ||
            !Assert(draws.First + draws.Count <= 2 * MaxDraws)) return;
        var (commands, counts) = at;
        var buffer = sorted ? _sorted : _commands;
        if (Drawer is { } drawer && Assert(buffer != 0)) Handed(drawer, shader, draws, (commands, counts), (buffer, counters));
    }

    // To the Vulkan frame, origin uniform set as the engine's (the frame mirrors uniforms)
    private static void Handed(CountedDraw drawer, IShaderProgram shader, (int First, int Count) draws,
        (int Commands, int Counts) at, (int Commands, int Counters) buffers)
    {
        for (var d = draws.First; d < Math.Min(draws.First + draws.Count, 2 * MaxDraws); d++)
        {
            var draw = Draws[d];
            if (draw.Count == 0) continue;
            shader.Uniform(_origin, draw.Offset);
            if (!drawer(draw.Vao, _useSsbos, (buffers.Commands, (at.Commands + draw.Start) * CommandBytes),
                    (buffers.Counters, 4 * (at.Counts + d)), draw.Count)) Refused++;
            RuntimeStats.drawCallsCount++;
        }
    }

    private readonly record struct Binding(ShaderProgramBase Program, string Sampler, int Texture, int Unit);

    private readonly record struct Record(
        ShaderProgramBase? Program, int First, int Textures, int DrawFirst, int DrawCount);
}
