using HarmonyLib;
using Komet.Core;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
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
    private static Occlusion.Range[] _unstagedRanges = [];

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

    private static (double X, double Y, double Z)
        _view; // the eye relative to Eye: the engine culls from the player's feet

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

    internal static bool Active => Enabled && !Suspended && !RenderDebug.TerrainChanged;

    public static void Install(Harmony harmony, ICoreClientAPI api, ILogger logger)
    {
        Clear();

        (_api, _logger) = (api, logger);
        var self = typeof(OcclusionCulling);
        var opaque = AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderOpaque),
            [typeof(float)]);
        var render = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var bind = AccessTools.DeclaredMethod(typeof(ShaderProgramBase), nameof(ShaderProgramBase.BindTexture2D),
            [typeof(string), typeof(int), typeof(int)]);
        if (!NotNull(opaque) || !NotNull(render) || !NotNull(bind) ||
            !Assert(Il.Binds(render, AccessTools.Method(self, nameof(Holding)))) ||
            !Assert(AccessTools.Field(typeof(ChunkRenderer), "poolsByRenderPass") != null))
        {
            return;
        }

        _ = harmony.Patch(opaque, new HarmonyMethod(AccessTools.Method(self, nameof(Opening))),
            new HarmonyMethod(AccessTools.Method(self, nameof(Closing))) { priority = Priority.Last });
        _ = harmony.Patch(render,
            new HarmonyMethod(AccessTools.Method(self, nameof(Holding))) { priority = Priority.First },
            new HarmonyMethod(AccessTools.Method(self, nameof(Issuing))) { priority = Priority.Last });
        _ = harmony.Patch(bind, postfix: new HarmonyMethod(AccessTools.Method(self, nameof(Bound))));
        var shadow =
            AccessTools.DeclaredMethod(typeof(ChunkRenderer), nameof(ChunkRenderer.RenderShadow), [typeof(float)]);
        if (NotNull(shadow))
        {
            _ = harmony.Patch(shadow, new HarmonyMethod(AccessTools.Method(self, nameof(ShadowOpening))),
                new HarmonyMethod(AccessTools.Method(self, nameof(ShadowClosing))) { priority = Priority.Last });
        }

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
        ClearBatch();
        ClearShadowBatch();
        Release();
    }

    // A Render call that threw past its postfix left draws held; released here, or every later draw would vanish. A
    // shadow pass that threw past ShadowClosing would send this pass's calls down the shadow path.
    private static void Opening(ChunkRenderer __instance)
    {
        _ = Assert(!_holding);
        (_holding, _renderer, Bypassing, _shadowing, _shadowCall) = (false, __instance, false, false, false);
        Links.Vulkan.Mark("culling: the opaque terrain begins");
        _opaque = Active && VulkanDraws && NotNull(__instance) && Ready(); // OpenGL keeps the engine's culling
        if (_opaque) BeginFrame();
    }

    private static void Closing()
    {
        _ = Assert(!_holding);
        _holding = false;
        if (!_opaque) return;

        if (_late)
        {
            FinishSurfaces(); // no topsoil call came to do it
        }

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
        if (!surface && _late)
        {
            FinishSurfaces(); // a pass after the surfaces although topsoil never finished them
        }

        if (!surface && !_builtThisFrame)
        {
            return; // before the ground is down only the surfaces are culled
        }

        var locations = Scan(pools);
        var rows = _rowsFrame;
        var backFaces = rows && pass is Opaque or Topsoil && BackFacesCulled();
        if (rows && HoldingBatched(__instance, pools, playerpos,
                (originUniformName, surface, pass == Topsoil && last, backFaces)))
        {
            return;
        }

        if (!Room(locations * (backFaces ? Runs : 1), pools.Count) ||
            (rows && !Rowed(Culler(__instance), pools, rows)))
        {
            return;
        }

        if (_camera is null)
        {
            (Eye.X, Eye.Y, Eye.Z, _camera, _view) = (playerpos.X, playerpos.Y, playerpos.Z, Eye, Seen());
        }

        (_holding, _origin, _surface, _lastSurface) = (Capture(), originUniformName, surface, pass == Topsoil && last);
        Bypassing = _holding && rows; // the GPU now does the engine's FrustumCull of these pools
        _backFaces = Bypassing && backFaces;
        Held.Clear();
    }

    // View matrix eye (-Rᵀt): LocalEyePos plus third person distance
    private static (double, double, double) Seen()
    {
        if (_api?.Render?.CurrentModelviewMatrix is not { Length: 16 } m) return default;

        var (x, y, z) = ((double)m[12], (double)m[13], (double)m[14]);
        var view = (-(m[0] * x + m[1] * y + m[2] * z), -(m[4] * x + m[5] * y + m[6] * z),
            -(m[8] * x + m[9] * y + m[10] * z));
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
    private static bool LastAtlas(MeshDataPoolManager?[] atlases, int at)
    {
        for (var i = at + 1; i < Math.Min(atlases.Length, MaxAtlases); i++)
        {
            if (atlases[i] is not null) return false;
        }

        return true;
    }

    private static bool Hold(MeshRef modelRef, bool useSsbos)
    {
        if (!_holding || modelRef is not VAO || Mini.Contains(modelRef) || !Assert(Held.Count < MaxPools)) return false;

        Held.Add(modelRef);
        _useSsbos = useSsbos;
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

        var rows = Bypassing;
        (_holding, Bypassing) = (false, false);
        var (pools, shadow, batched) = (Pools(__instance), _shadowCall, _batchCall);
        (_shadowCall, _batchCall) = (false, -1);
        if (batched >= 0 && IssuingBatched(batched))
        {
            if (_lastSurface && _late) FinishSurfaces();

            return;
        }

        // laid out otherwise than drawn: culled on its own, as without the batch
        if (batched >= 0 && (!NotNull(pools) || !Rowed(Culler(__instance), pools, true))) Held.Clear();

        if (IssueShadow(rows, shadow, pools, __instance)) return;

        if (!NotNull(pools) || Held.Count == 0 || _camera is not { } camera)
        {
            if (_lastSurface && _late)
            {
                FinishSurfaces(); // a last surface call that drew nothing (no topsoil there)
            }

            return;
        }

        if (rows)
        {
            IssuingRows(Culler(__instance), pools, camera);
            return;
        }

        Issue(pools, camera);
    }

    private static bool IssueShadow(bool rows, bool shadow, List<MeshDataPool>? pools,
        MeshDataPoolManager manager)
    {
        if (!rows || !shadow || !NotNull(pools) || Held.Count == 0) return false;

        var entry = _shadowBatchCall;
        _shadowBatchCall = -1;
        if (entry >= 0 && IssuingShadowBatched(entry)) return true;

        if (entry < 0 || Rowed(Culler(manager), pools, true))
        {
            IssuingShadow(Culler(manager), pools);
        }
        else
        {
            Held.Clear();
        }

        return true;
    }

    // The frame's own ranges: one per pool, in the manager's order, culled by the GPU pyramid
    private static void Issue(List<MeshDataPool> pools, Vec3d camera)
    {
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
        {
            into[i] = new Occlusion.Range(-Far, -Far, -Far, sizes[i], Far, Far, Far, starts[2 * i] / 4);
        }

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
