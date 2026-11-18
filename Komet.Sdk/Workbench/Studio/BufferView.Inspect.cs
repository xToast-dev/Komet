using OpenTK.Graphics.OpenGL;
using Vintagestory.API.MathTools;

namespace Komet.Sdk;

// One inspector line: the target, its value in words, the colour swatch where one makes sense (null none), Empty when
// the target holds nothing at that place (drawn faint, listed last)
internal readonly record struct InspectRow(string Name, string Value, float[]? Swatch, bool Empty);

// The pixel inspector: a click in the preview asks, the next Picture reads 1 px of every target at that place (screen
// targets at the same uv; the shadow maps where that pixel's world position lands in them) and keeps the lines, the
// targets without content at the end
internal sealed partial class BufferView
{
    private (double U, double V)? _ask;
    private readonly float[] _px = new float[4];
    private readonly List<InspectRow> _empty = [];

    public List<InspectRow> Inspected { get; } = [];
    public (double U, double V)? InspectedAt { get; private set; }

    public void Ask(double u, double v) => _ask = (Math.Clamp(u, 0, 1), Math.Clamp(v, 0, 1));

    // Once per click, in the frame's GUI stage
    public void Inspect(ICoreClientAPI capi)
    {
        if (_ask is not { } at) return;

        _ask = null;
        Inspected.Clear();
        _empty.Clear();
        InspectedAt = at;
        GL.GetInteger(GetPName.ReadFramebufferBinding, out var read);
        var depth = 1f;
        try
        {
            for (var i = 1; i < Math.Min(Targets.Length, MaxTargets); i++)
            {
                var target = Targets[i];
                if (target.Kind == BufferKind.Shadow) continue;

                var value = Missing(target).Length > 0 ? null : ReadAt(capi, target, at.U, 1 - at.V);
                depth = value is not null && target.Name == "Depth" ? value[0] : depth;
                Add(value is null ? Empty(target) : Row(target, value));
            }

            Shadows(capi, at, depth);
        }
        finally
        {
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
            Inspected.AddRange(_empty);
        }
    }

    private void Add(InspectRow row) => (row.Empty ? _empty : Inspected).Add(row);

    private static InspectRow Empty(BufferTarget target, string? why = null)
    {
        var missing = Missing(target);
        return new InspectRow(target.Name, why ?? (missing.Length > 0 ? missing : "No value here"), null, true);
    }

    private float[]? ReadAt(ICoreClientAPI capi, BufferTarget target, double u, double v)
    {
        var list = capi.Render.FrameBuffers;
        if (list is null || (int)target.Fb >= list.Count || list[(int)target.Fb] is not { FboId: > 0, Width: > 0, Height: > 0 } fb)
        {
            return null;
        }

        var (x, y) = ((int)Math.Clamp(u * fb.Width, 0, fb.Width - 1), (int)Math.Clamp(v * fb.Height, 0, fb.Height - 1));
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, fb.FboId);
        GL.GetInteger(GetPName.ReadBuffer, out var readBuffer); // per framebuffer: put back after the read
        GL.GetInteger(GetPName.PixelPackBufferBinding, out var pack);
        GL.BindBuffer(OpenTK.Graphics.OpenGL.BufferTarget.PixelPackBuffer, 0);
        Array.Clear(_px);
        _ = GL.GetError();
        if (target.Attachment < 0)
        {
            GL.ReadPixels(x, y, 1, 1, PixelFormat.DepthComponent, PixelType.Float, _px);
        }
        else
        {
            GL.ReadBuffer(ReadBufferMode.ColorAttachment0 + target.Attachment);
            GL.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.Float, _px);
            GL.ReadBuffer((ReadBufferMode)readBuffer);
        }

        GL.BindBuffer(OpenTK.Graphics.OpenGL.BufferTarget.PixelPackBuffer, pack);
        return GL.GetError() == ErrorCode.NoError ? (float[])_px.Clone() : null;
    }

    // The value in words: "Normal (x, y, z)", "Position (x, y, z)", "Depth d", "R · G · B · A"; normals and colours
    // with a swatch (a normal as the preview shows it, n × 0.5 + 0.5)
    private static InspectRow Row(BufferTarget target, float[] v)
    {
        var xyz = "(" + Format(v[0]) + ", " + Format(v[1]) + ", " + Format(v[2]) + ")";
        return target.Kind switch
        {
            BufferKind.Depth => new InspectRow(target.Name, "Depth " + Format(v[0]), null, false),
            BufferKind.Normal => new InspectRow(target.Name, "Normal " + xyz, [v[0] * 0.5f + 0.5f, v[1] * 0.5f + 0.5f,
                v[2] * 0.5f + 0.5f, 1], false),
            BufferKind.Position => new InspectRow(target.Name, "Position " + xyz, null, false),
            _ => new InspectRow(target.Name, "R " + Format(v[0]) + " · G " + Format(v[1]) + " · B " + Format(v[2]) +
                                             " · A " + Format(v[3]), [v[0], v[1], v[2], 1], false)
        };
    }

    // The pixel's camera-relative position from the depth and the inverse perspective × camera matrix, then each shadow
    // map's matrix: the stored depth there against the pixel's own (lit when the pixel is not behind it)
    private void Shadows(ICoreClientAPI capi, (double U, double V) at, float depth)
    {
        var inverse = Mat4d.Create();
        var viewProj = Mat4d.Create();
        _ = Mat4d.Mul(viewProj, capi.Render.PerspectiveProjectionMat, capi.Render.CameraMatrixOrigin);
        if (Mat4d.Invert(inverse, viewProj) is null) return;

        double[] ndc = [at.U * 2 - 1, (1 - at.V) * 2 - 1, depth * 2 - 1, 1];
        var world = Mat4d.MulWithVec4(inverse, ndc);
        if (Math.Abs(world[3]) < 1e-9) return;

        float[] pos = [(float)(world[0] / world[3]), (float)(world[1] / world[3]), (float)(world[2] / world[3]), 1];
        Add(new InspectRow("World (camera-relative)", "Position (" + Format(pos[0]) + ", " + Format(pos[1]) + ", " +
                                                      Format(pos[2]) + ")", null, false));
        var uniforms = capi.Render.ShaderUniforms;
        Shadow(capi, Targets[4], uniforms.ToShadowMapSpaceMatrixNear, pos);
        Shadow(capi, Targets[5], uniforms.ToShadowMapSpaceMatrixFar, pos);
    }

    private void Shadow(ICoreClientAPI capi, BufferTarget target, float[] matrix, float[] pos)
    {
        if (Missing(target).Length > 0)
        {
            Add(Empty(target));
            return;
        }

        var s = Mat4f.MulWithVec4(matrix, pos);
        if (s[0] is < 0 or > 1 || s[1] is < 0 or > 1)
        {
            Add(Empty(target, "Outside the map"));
            return;
        }

        if (ReadAt(capi, target, s[0], s[1]) is { } stored)
        {
            var lit = s[2] - 0.0005f <= stored[0];
            Add(new InspectRow(target.Name, target.Name + ": stored " + Format(stored[0]) + " · this pixel " + Format(s[2]) +
                                            (lit ? " · lit" : " · shadowed"), null, false));
        }
        else
        {
            Add(Empty(target));
        }
    }
}
