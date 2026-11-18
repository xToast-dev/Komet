using System.Numerics;
using Komet.Core;

namespace Komet.Rendering;

internal static partial class RenderDebug
{
    private static State? _reference, _alternative;
    internal static bool HasReference => _reference is not null;
    internal static bool ShowingReference { get; private set; }

    internal static int OverrideCount
    {
        get
        {
            if (!Active) return 0;

            return BitOperations.PopCount(_disabledTerrain) + DisabledShaders.Count +
                   (_view == View.Filled ? 0 : 1) + (_noCull ? 1 : 0) + (_noDepth ? 1 : 0);
        }
    }

    internal static void SaveReference()
    {
        _reference = Capture();
        _alternative = null;
        ShowingReference = false;
    }

    internal static void Compare()
    {
        if (_reference is null) return;

        if (ShowingReference && _alternative is { } alternative)
        {
            Apply(alternative);
            ShowingReference = false;
        }
        else
        {
            _alternative = Capture();
            Apply(_reference);
            ShowingReference = true;
        }
    }

    private static State Capture() => new(_view, _disabledTerrain, _noCull, _noDepth, [.. DisabledShaders]);

    private static void Apply(State state)
    {
        (_view, _disabledTerrain, _noCull, _noDepth) =
            (state.Raster == View.Filled || Links.Vulkan.NonSolidFill ? state.Raster : View.Filled, state.Terrain,
                state.Cull, state.Depth);
        DisabledShaders.Clear();
        foreach (var shader in state.Shaders.Bounded(ShaderHub.MaxPasses))
        {
            _ = DisabledShaders.Add(shader);
        }

        Changed();
    }

    private static void Edited()
    {
        ShowingReference = false;
        _alternative = null;
    }

    private static void ClearComparison() => (_reference, _alternative, ShowingReference) = (null, null, false);

    private sealed record State(View Raster, uint Terrain, bool Cull, bool Depth, (string Mod, string Pass)[] Shaders);
}
