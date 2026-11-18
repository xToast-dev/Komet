using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Rendering;

internal static partial class RenderDebug
{
    private static readonly int[] Polygon = new int[2];

    private static bool Begin(MeshDataPoolManager __instance, out Saved __state)
    {
        __state = default;
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId) return true;

        if (!TerrainChanged) return true;

        var pass = Pass(__instance);
        if (pass < 0)
        {
            return true; // another mod's manager, not one of the terrain's
        }

        if (!TerrainAllowed(pass)) return false;

        if (_api?.World is not ClientMain game ||
            Stage(game) is not (EnumRenderStage.Opaque or EnumRenderStage.OIT or EnumRenderStage.AfterOIT))
        {
            return true; // keep shadow-map rasterization unchanged
        }

        if (Mode == View.Filled && !NoCull && !NoDepth) return true;

        __state = Save();
        Apply();
        return true;
    }

    internal static Saved Save()
    {
        GL.GetInteger(GetPName.PolygonMode, Polygon);
        return new Saved(true, Polygon[0], Polygon[1], GL.IsEnabled(EnableCap.CullFace),
            GL.IsEnabled(EnableCap.DepthTest));
    }

    private static void Apply()
    {
        var mode = Mode switch
        {
            View.Wireframe => PolygonMode.Line, View.Points => PolygonMode.Point, _ => PolygonMode.Fill
        };
        if (Mode != View.Filled) GL.PolygonMode(TriangleFace.FrontAndBack, mode);

        if (NoCull) GL.Disable(EnableCap.CullFace);

        if (NoDepth) GL.Disable(EnableCap.DepthTest);
    }

    internal static void Restore(Saved state)
    {
        if (!state.Altered) return;

        if (state.Front == state.Back)
        {
            GL.PolygonMode(TriangleFace.FrontAndBack, (PolygonMode)state.Front);
        }
        else
        {
            GL.PolygonMode(TriangleFace.Front, (PolygonMode)state.Front);
            GL.PolygonMode(TriangleFace.Back, (PolygonMode)state.Back);
        }

        if (state.Cull)
        {
            GL.Enable(EnableCap.CullFace);
        }
        else
        {
            GL.Disable(EnableCap.CullFace);
        }

        if (state.Depth)
        {
            GL.Enable(EnableCap.DepthTest);
        }
        else
        {
            GL.Disable(EnableCap.DepthTest);
        }
    }

    private static void End(Saved __state) => Restore(__state);

    internal readonly record struct Saved(bool Altered, int Front, int Back, bool Cull, bool Depth);
}
