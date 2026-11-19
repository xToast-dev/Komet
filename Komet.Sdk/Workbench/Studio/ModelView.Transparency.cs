using Komet.Rendering;
using OpenTK.Graphics.OpenGL;

namespace Komet.Sdk;

// The scene's transparency pass uses the engine's six OIT attachments. Each selected shader is drawn once.
// the three accumulation bins are then decoded into an RGBA layer over the scene.
internal sealed partial class ModelView
{
    private const int OitAttachments = 6;

    private const string ComposeFragment = """
                                           #version 330 core
                                           in vec2 uv;
                                           out vec4 color;
                                           uniform sampler2D reveal;
                                           uniform sampler2D coverage;
                                           uniform sampler2D bin0;
                                           uniform sampler2D bin1;
                                           uniform sampler2D bin2;
                                           void main() {
                                               vec3 visibility = vec3(1.0) - texture(reveal, uv).rgb;
                                               vec4 bins[3] = vec4[3](texture(bin0, uv), texture(bin1, uv), texture(bin2, uv));
                                               vec3 sum = vec3(0.0);
                                               float remaining = 1.0;
                                               for (int i = 0; i < 3; ++i) {
                                                   sum += bins[i].rgb / max(0.0001, bins[i].a) * visibility[i] * remaining;
                                                   remaining *= 1.0 - visibility[i];
                                               }
                                               color = vec4(sum / max(0.0001, 1.0 - remaining), 1.0 - texture(coverage, uv).r);
                                           }
                                           """;

    private static readonly DrawBuffersEnum[] OitOutputs =
    [
        DrawBuffersEnum.ColorAttachment0, DrawBuffersEnum.ColorAttachment1, DrawBuffersEnum.ColorAttachment2,
        DrawBuffersEnum.ColorAttachment3, DrawBuffersEnum.ColorAttachment4, DrawBuffersEnum.ColorAttachment5
    ];

    private static readonly float[] ClearReveal = [1, 1, 1, 1], ClearAccumulation = [0, 0, 0, 0];
    private static readonly string[] ComposeSamplers = ["reveal", "coverage", "bin0", "bin1", "bin2"];
    private readonly int[] _oitTextures = new int[OitAttachments];
    private readonly BlendState[] _savedBlend = new BlendState[OitAttachments];
    private int _oitFb, _compose;
    private bool _usesOit;

    private bool BeginTransparency(int height)
    {
        if (!TransparencyReady(height)) return false;

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _oitFb);
        GL.DepthMask(false);
        for (var i = 0; i < OitAttachments; i++)
        {
            GL.ClearBuffer(ClearBuffer.Color, i, i < 2 ? ClearReveal : ClearAccumulation);
            GL.Enable(IndexedEnableCap.Blend, i);
            GL.BlendEquation(i, BlendEquationMode.FuncAdd);
            // Reveal textures multiply the remaining visibility. Accumulation textures sum contributions.
            var src = i < 2 ? BlendingFactorSrc.Zero : BlendingFactorSrc.One;
            var dst = i < 2 ? BlendingFactorDest.SrcColor : BlendingFactorDest.One;
            GL.BlendFuncSeparate(i, src, dst, src, dst);
        }

        return true;
    }

    private bool TransparencyReady(int height)
    {
        if (_oitFb != 0) return true;

        if (_compose == 0) _compose = PreviewProgram(ModPasses.Vertex, ComposeFragment);
        if (_compose == 0) return false;

        _oitFb = GL.GenFramebuffer();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _oitFb);
        for (var i = 0; i < OitAttachments; i++)
        {
            _oitTextures[i] = Texture<float>(Width, height, PixelInternalFormat.Rgba16f, PixelFormat.Rgba, null);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0 + i,
                TextureTarget.Texture2D, _oitTextures[i], 0);
        }

        GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, _depth);
        GL.DrawBuffers(OitAttachments, OitOutputs);
        if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete)
        {
            return true;
        }

        ReleaseTransparencyTargets();
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
        return false;
    }

    private void CompositeTransparency()
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fb);
        GL.Disable(EnableCap.DepthTest);
        SceneBlend();
        GL.UseProgram(_compose);
        GL.BindVertexArray(_builtinVao);
        for (var unit = 0; unit < ComposeSamplers.Length; unit++)
        {
            BindPreviewTexture(_compose, ComposeSamplers[unit], unit, _oitTextures[unit < 2 ? unit : unit + 1]);
        }

        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.Enable(EnableCap.DepthTest);
        GL.DepthMask(true);
    }

    private static void SceneBlend()
    {
        GL.Enable(IndexedEnableCap.Blend, 0);
        GL.BlendEquation(0, BlendEquationMode.FuncAdd);
        GL.BlendFuncSeparate(0, BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha,
            BlendingFactorSrc.Zero, BlendingFactorDest.One);
    }

    private void SavePreviewBlend()
    {
        for (var i = 0; i < OitAttachments; i++)
        {
            _savedBlend[i] = new BlendState(GL.IsEnabled(IndexedEnableCap.Blend, i),
                IndexedBlend(GetPName.BlendSrcRgb, i), IndexedBlend(GetPName.BlendDstRgb, i),
                IndexedBlend(GetPName.BlendSrcAlpha, i), IndexedBlend(GetPName.BlendDstAlpha, i),
                IndexedBlend(GetPName.BlendEquationRgb, i), IndexedBlend(GetPName.BlendEquationAlpha, i));
        }
    }

    private static int IndexedBlend(GetPName name, int index)
    {
        GL.GetInteger((GetIndexedPName)name, index, out var value);
        return value;
    }

    private void RestorePreviewBlend()
    {
        for (var i = 0; i < OitAttachments; i++)
        {
            var blend = _savedBlend[i];
            if (blend.On) GL.Enable(IndexedEnableCap.Blend, i);
            else GL.Disable(IndexedEnableCap.Blend, i);

            GL.BlendFuncSeparate(i, (BlendingFactorSrc)blend.SrcRgb, (BlendingFactorDest)blend.DstRgb,
                (BlendingFactorSrc)blend.SrcAlpha, (BlendingFactorDest)blend.DstAlpha);
            GL.BlendEquationSeparate(i, (BlendEquationMode)blend.EquationRgb, (BlendEquationMode)blend.EquationAlpha);
        }
    }

    private static void BindPreviewTexture(int program, string name, int unit, int texture)
    {
        GL.ActiveTexture(TextureUnit.Texture0 + unit);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        GL.BindSampler(unit, 0);
        GL.Uniform1(GL.GetUniformLocation(program, name), unit);
    }

    private int PreviewProgram(string vertex, string fragment)
    {
        var (v, f) = (Stage(ShaderType.VertexShader, vertex), Stage(ShaderType.FragmentShader, fragment));
        var program = 0;
        if (v != 0 && f != 0)
        {
            program = GL.CreateProgram();
            GL.AttachShader(program, v);
            GL.AttachShader(program, f);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var linked);
            if (linked == 0)
            {
                _log = GL.GetProgramInfoLog(program);
                GL.DeleteProgram(program);
                program = 0;
            }
        }

        if (v != 0) GL.DeleteShader(v);

        if (f != 0) GL.DeleteShader(f);

        return program;
    }

    private void ReleaseTransparencyTargets()
    {
        if (_oitFb == 0) return;

        GL.DeleteFramebuffer(_oitFb);
        GL.DeleteTextures(OitAttachments, _oitTextures);
        Array.Clear(_oitTextures);
        _oitFb = 0;
    }

    private void DisposeTransparency()
    {
        if (_compose != 0) GL.DeleteProgram(_compose);

        _compose = 0;
    }

    private readonly record struct BlendState(
        bool On,
        int SrcRgb,
        int DstRgb,
        int SrcAlpha,
        int DstAlpha,
        int EquationRgb,
        int EquationAlpha);
}
