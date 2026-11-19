using HarmonyLib;
using Komet.Core;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

// Session-only diagnostics. The normal path tests one flag before touching game or graphics state.
internal static partial class RenderDebug
{
    private const int MaxPasses = 16, MaxAtlases = 64, MaxManagers = MaxPasses * MaxAtlases;
    private static readonly HashSet<(string Mod, string Pass)> DisabledShaders = [];
    private static readonly Dictionary<MeshDataPoolManager, int> Managers = new(ReferenceEqualityComparer.Instance);
    private static ICoreClientAPI? _api;
    private static ChunkRenderer? _terrain;
    private static View _view;
    private static uint _disabledTerrain;
    private static bool _noCull, _noDepth;

    internal static bool Installed { get; private set; }

    internal static bool Active { get; private set; }

    internal static bool TerrainChanged =>
        Installed && Active && (_disabledTerrain != 0 || _view != View.Filled || _noCull || _noDepth);

    internal static View Mode => Active && Links.Vulkan.NonSolidFill ? _view : View.Filled;
    internal static bool NoCull => Active && _noCull;
    internal static bool NoDepth => Active && _noDepth;

    internal static void Install(Harmony harmony, ICoreClientAPI api)
    {
        Clear();
        _api = api;
        ClientSettings.Inst.AddWatcher<bool>("developerMode", DeveloperChanged);
        var render = AccessTools.DeclaredMethod(typeof(MeshDataPoolManager), nameof(MeshDataPoolManager.Render));
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(RenderDebug), nameof(Begin)))
            { priority = Priority.First };
        if (!NotNull(render) || !Assert(Il.Binds(render, prefix.method)) ||
            !Assert(AccessTools.Field(typeof(ClientMain), "chunkRenderer") != null) ||
            !Assert(AccessTools.Field(typeof(ChunkRenderer), "poolsByRenderPass") != null))
        {
            return;
        }

        _ = harmony.Patch(render, prefix,
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(RenderDebug), nameof(End))));
        Installed = true;
    }

    internal static void SetView(View view)
    {
        if (!Enum.IsDefined(view) ||
            (view != View.Filled && !Links.Vulkan.NonSolidFill))
        {
            return;
        }

        if (_view == view) return;

        Edited();
        _view = view;
        Changed();
    }

    internal static void SetNoCull(bool value)
    {
        if (_noCull == value) return;

        Edited();
        _noCull = value;
        Changed();
    }

    internal static void SetNoDepth(bool value)
    {
        if (_noDepth == value) return;

        Edited();
        _noDepth = value;
        Changed();
    }

    internal static bool TerrainAllowed(int pass) =>
        !Active || (uint)pass >= MaxPasses || (_disabledTerrain & (1u << pass)) == 0;

    internal static void SetTerrain(int pass, bool enabled)
    {
        if ((uint)pass >= MaxPasses) return;

        if ((_disabledTerrain & (1u << pass)) == 0 == enabled) return;

        Edited();
        if (enabled)
        {
            _disabledTerrain &= ~(1u << pass);
        }
        else
        {
            _disabledTerrain |= 1u << pass;
        }

        Changed();
    }

    internal static bool ShaderAllowed(string mod, string pass) => !Active || !DisabledShaders.Contains((mod, pass));

    internal static void SetShader(string mod, string pass, bool enabled)
    {
        if (!DisabledShaders.Contains((mod, pass)) == enabled) return;

        Edited();
        if (enabled)
        {
            _ = DisabledShaders.Remove((mod, pass));
        }
        else if (DisabledShaders.Count < ShaderHub.MaxPasses)
        {
            _ = DisabledShaders.Add((mod, pass));
        }

        Changed();
    }

    private static void Changed()
    {
        Active = _view != View.Filled || _noCull || _noDepth || _disabledTerrain != 0 || DisabledShaders.Count > 0;
        FarShadowReuse.Clear();
        NearShadowReuse.Clear();
    }

    internal static void Reset()
    {
        ClearComparison();
        (_view, _disabledTerrain, _noCull, _noDepth, Active) = (View.Filled, 0, false, false, false);
        DisabledShaders.Clear();
        Managers.Clear();
        FarShadowReuse.Clear();
        NearShadowReuse.Clear();
    }

    internal static void Clear()
    {
        _ = ClientSettings.Inst.RemoveWatcher<bool>("developerMode", DeveloperChanged);
        Reset();
        (_api, _terrain, Installed) = (null, null, false);
    }

    private static void DeveloperChanged(bool enabled)
    {
        if (!enabled) Reset();
    }

    private static int Pass(MeshDataPoolManager manager)
    {
        if (_api?.World is not ClientMain game || TerrainOf(game) is not { } terrain) return -1;

        if (!ReferenceEquals(_terrain, terrain))
        {
            Managers.Clear();
            _terrain = terrain;
        }

        if (Managers.TryGetValue(manager, out var known)) return known;

        if (PassPools(terrain) is not { } passes) return -1;

        for (var p = 0; p < Math.Min(passes.Length, MaxPasses); p++)
        {
            if (passes[p] is { } atlases)
            {
                for (var a = 0; a < Math.Min(atlases.Length, MaxAtlases); a++)
                {
                    if (ReferenceEquals(atlases[a], manager))
                    {
                        if (Managers.Count < MaxManagers) Managers[manager] = p;

                        return p;
                    }
                }
            }
        }

        return -1;
    }

    internal enum View
    {
        Filled,
        Wireframe,
        Points
    }
}
