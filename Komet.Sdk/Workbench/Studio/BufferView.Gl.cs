using System.Diagnostics;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// The view's GL: one program, one sampler (no depth compare, nearest), one RGBA8 target and the vanilla snapshot
internal sealed partial class BufferView
{
    private const string Vertex = """
                                  #version 330 core
                                  out vec2 uv;
                                  void main()
                                  {
                                      vec2 corner = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
                                      uv = corner;
                                      gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
                                  }
                                  """;

    // mode: 0 as is, 1 hdr (tonemapped), 2 depth (linearised), 3 normal, 4 position, 5 shadow (contrast curve)
    private const string Fragment = """
                                    #version 330 core
                                    in vec2 uv;
                                    out vec4 color;
                                    uniform sampler2D source;
                                    uniform sampler2D other;
                                    uniform int mode;
                                    uniform int channel;
                                    uniform int flip;
                                    uniform float contrast;
                                    uniform float split;
                                    uniform vec2 range;
                                    uniform vec4 mark;
                                    vec3 shade(vec4 t)
                                    {
                                        if (channel >= 0) { float c = t[channel]; t = vec4(c, c, c, 1.0); }
                                        if (mode == 1) return t.rgb / (1.0 + t.rgb);
                                        if (mode == 2) { float z = t.r * 2.0 - 1.0; float d = 2.0 * range.x * range.y / (range.y + range.x - z * (range.y - range.x)); return vec3(1.0 - clamp(d / range.y, 0.0, 1.0)); }
                                        if (mode == 3) return channel >= 0 ? t.rgb : t.rgb * 0.5 + 0.5;
                                        if (mode == 4) return channel >= 0 ? fract(abs(t.rgb) / 32.0) : fract(abs(t.rgb) / 32.0);
                                        if (mode == 5) return vec3(pow(clamp(t.r, 0.0, 1.0), contrast));
                                        return t.rgb;
                                    }
                                    void main()
                                    {
                                        vec2 at = flip == 1 ? vec2(uv.x, 1.0 - uv.y) : uv;
                                        if (split >= 0.0 && uv.x > split) { color = vec4(texture(other, uv).rgb, 1.0); }
                                        else { color = vec4(shade(texture(source, at)), 1.0); }
                                        if (split >= 0.0 && abs(uv.x - split) < 0.002) color = vec4(1.0, 0.8, 0.2, 1.0);
                                        if (mark.x >= 0.0)
                                        {
                                            vec2 d = (uv - mark.xy) * mark.zw;
                                            float r = length(d);
                                            if (abs(r - 7.0) < 1.5) color = vec4(0.0, 0.0, 0.0, 1.0);
                                            if (abs(r - 7.0) < 0.75 || (r > 9.0 && r < 14.0 && min(abs(d.x), abs(d.y)) < 0.75)) color = vec4(1.0, 0.85, 0.2, 1.0);
                                        }
                                    }
                                    """;

    private bool Ringed => Marking && InspectedAt is not null;

    // The texture the preview shows this frame (sharp: the frame so far, upright); sharp itself when nothing to draw
    public int Picture(ICoreClientAPI capi, int sharp)
    {
        var target = Shown;
        var source = target.Kind == BufferKind.Final ? sharp : TextureOf(capi, target);
        if (_failed || source == 0 || (target.Kind == BufferKind.Final && Split < 0 && Channel < 0 && !Ringed) ||
            Missing(target).Length > 0)
        {
            LastMs = 0;
            return target.Kind == BufferKind.Final ? sharp : 0;
        }

        var clock = Stopwatch.StartNew();
        var drawn = Draw(capi, source, target);
        LastMs = clock.Elapsed.TotalMilliseconds;
        return drawn ? _target : sharp;
    }

    private bool Draw(ICoreClientAPI capi, int source, BufferTarget target)
    {
        var height = Math.Max(1, (int)((double)Width * capi.Render.FrameHeight / Math.Max(1, capi.Render.FrameWidth)));
        if ((_program == 0 && !Build()) || (_height != height && !Targets2(height))) return false;

        var saved = Save();
        try
        {
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fb);
            GL.Viewport(0, 0, Width, _height);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.DepthTest);
            GL.UseProgram(_program);
            GL.BindVertexArray(_vao);
            Bind(0, source, true);
            Bind(1, _snap != 0 && Snapshot == target.Name ? _snap : source, false);
            Uniforms(target, capi);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }
        finally
        {
            Restore(saved);
        }

        return true;
    }

    private void Uniforms(BufferTarget target, ICoreClientAPI capi)
    {
        GL.Uniform1(_uSource, 0);
        GL.Uniform1(_uOther, 1);
        GL.Uniform1(_uMode, target.Kind switch
        {
            BufferKind.Hdr => 1,
            BufferKind.Depth => 2,
            BufferKind.Normal => 3,
            BufferKind.Position => 4,
            BufferKind.Shadow => 5,
            _ => 0
        });
        GL.Uniform1(_uChannel, Channel);
        GL.Uniform1(_uFlip, target.Kind == BufferKind.Final ? 0 : 1);
        GL.Uniform1(_uContrast, Contrasts[Math.Clamp(Contrast, 0, Contrasts.Length - 1)]);
        GL.Uniform1(_uSplit, (float)(Split >= 0 && Snapshot == target.Name ? Split : -1));
        var mark = Ringed && InspectedAt is { } at ? at : (-1, 0);
        var scale = Math.Max(1, DisplayWidth) / Width; // the ring in screen pixels, not the target's
        GL.Uniform4(_uMark, (float)mark.U, (float)mark.V, (float)(Width * scale), (float)(_height * scale));
        GL.Uniform2(_uRange, 0.1f, Math.Max(64, capi.World.Player?.WorldData?.DesiredViewDistance ?? 256));
    }

    private void Bind(int unit, int texture, bool sampler)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.BindSampler(unit, sampler ? _sampler : 0);
    }

    // The current picture of the shown target becomes the vanilla side (taken while the project is off)
    public void TakeSnapshot()
    {
        if (_snapFb == 0 || _fb == 0) return;

        GL.GetInteger(GetPName.DrawFramebufferBinding, out var draw);
        GL.GetInteger(GetPName.ReadFramebufferBinding, out var read);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fb);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _snapFb);
        GL.BlitFramebuffer(0, 0, Width, _height, 0, 0, Width, _height, ClearBufferMask.ColorBufferBit,
            BlitFramebufferFilter.Nearest);
        GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
        Snapshot = Shown.Name;
    }

    public void ForgetSnapshot() => Snapshot = "";
}
