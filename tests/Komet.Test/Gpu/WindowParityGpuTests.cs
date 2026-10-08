using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class WindowParityGpuTests
{
    private const int Side = OcclusionGpuTests.Size;

    private const string Vertex = """
        #version 330 core
        layout(location = 0) in vec2 position;
        void main() { gl_Position = vec4(position, 0.0, 1.0); }
        """;

    private const string Fragment = """
        #version 330 core
        uniform vec4 color;
        out vec4 outColor;
        void main() { outColor = color; }
        """;

    private static readonly float[] Corners = [-0.8f, -0.7f, 0.9f, -0.3f, -0.1f, 0.85f];

    // raster: then the states Vulkan draws since the window is its - a line loop, a triangle as its edges (glPolygonMode), a
    // triangle XORed into the colors (glLogicOp) - each as OpenGL draws it
    [TestCase(false)]
    [TestCase(true)]
    public void TheWindowDrawnByVulkanIsOpenGlsWindow(bool raster)
    {
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        if (raster && (!rig.Device!.NonSolidFill || !rig.Device.LogicOps)) Assert.Ignore("the device lacks wireframe or logic ops");
        rig.Backend!.Flush();
        var (program, vao, buffer) = (Parity.Linked(Vertex, Fragment), GL.GenVertexArray(), GL.GenBuffer());
        GL.BindVertexArray(vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, buffer);
        GL.BufferData(BufferTarget.ArrayBuffer, Corners.Length * 4, Corners, BufferUsageHint.StaticDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8, 0);
        GL.EnableVertexAttribArray(0);
        GL.BindVertexArray(0);
        var more = raster ? Raster : (Action<int>?)null;
        var expected = Frame(program, vao, null, more);
        Assert.That(GlTap.Tap(scene: true), Is.True, GlTap.Missing);
        using var renderer = TerrainRenderer.Create(rig.Device!, 64, null, out var why);
        Assert.That(renderer, Is.Not.Null, why);
        using var scene = SceneParityGpuTests.Hooked(renderer!);
        float[] drawn;
        var (draws, clears) = (scene.Draws, scene.Clears);
        try
        {
            var stand = renderer!.Window(Side, Side);
            Assert.That(stand, Is.Not.Null, "the stand-in made");
            Assert.That(renderer.Start([stand!], [0], out why), Is.True, why);
            drawn = Frame(program, vao, renderer, more);
        }
        finally
        {
            renderer?.Unwindowed();
            GlTap.Untap();
            GL.DeleteVertexArray(vao);
            GL.DeleteBuffer(buffer);
            GL.DeleteProgram(program);
        }

        var differing = drawn.Zip(expected, (a, b) => Math.Abs(a - b) > 1.5f / 255).Count(d => d);
        Assert.Multiple(() =>
        {
            Assert.That(scene.Draws - draws, Is.EqualTo(raster ? 4 : 1), "every draw Vulkan's");
            if (raster)
            {
                Assert.That(scene.Reasons, Is.Empty, "nothing left to OpenGL");
                Assert.That(differing, Is.LessThanOrEqualTo(4 * 8), "at most a few pixels of a line's end rasterized otherwise");
                return;
            }

            Assert.That(scene.Clears - clears, Is.EqualTo(2), "both clears by Vulkan");
            Assert.That(Parity.Worst(drawn, expected), Is.LessThanOrEqualTo(1.5f / 255), "the real window after the swap");
        });
    }

    private static void Raster(int program)
    {
        var color = GL.GetUniformLocation(program, "color");
        GL.Uniform4(color, 1f, 1f, 0.2f, 1f);
        GL.DrawArrays(PrimitiveType.LineLoop, 0, 3);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
        GL.Uniform4(color, 0.3f, 0.3f, 1f, 1f);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        GL.Enable(EnableCap.ColorLogicOp);
        GL.LogicOp(LogicOp.Xor);
        GL.Uniform4(color, 0.5f, 0.5f, 0.5f, 0f);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.Disable(EnableCap.ColorLogicOp);
    }

    private static float[] Frame(int program, int vao, TerrainRenderer? renderer, Action<int>? more = null)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        GL.DrawBuffer(DrawBufferMode.Back);
        GL.Viewport(0, 0, Side, Side);
        GL.ClearColor(0.1f, 0.2f, 0.3f, 1f);
        GL.Disable(EnableCap.ScissorTest);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Enable(EnableCap.ScissorTest);
        GL.Scissor(8, 8, 20, 40);
        GL.ClearColor(0.9f, 0.5f, 0.1f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit);
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.UseProgram(program);
        GL.Uniform4(GL.GetUniformLocation(program, "color"), 0.2f, 0.9f, 0.4f, 0.6f);
        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
        GL.Disable(EnableCap.Blend);
        more?.Invoke(program);
        GL.BindVertexArray(0);
        GL.UseProgram(0);
        GL.Disable(EnableCap.Blend);
        if (renderer is not null)
        {
            Assert.That(renderer.Present(null), Is.False, "OpenGL shows the frame");
            renderer.Unwindowed(); // framebuffer 0 is the real window again
        }

        var pixels = new float[Side * Side * 4];
        GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
        GL.ReadBuffer(ReadBufferMode.Back);
        GL.ReadPixels(0, 0, Side, Side, PixelFormat.Rgba, PixelType.Float, pixels);
        return pixels;
    }
}
