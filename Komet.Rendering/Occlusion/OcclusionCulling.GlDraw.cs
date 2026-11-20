using OpenTK.Graphics.OpenGL;
using Vintagestory.Client.NoObf;

namespace Komet.Rendering;

// Vulkan compute mode: the cull runs on OpenGL's backend (it has to read the engine's depth texture), so the commands
// and counts are OpenGL buffers. Draw the culled ranges with the engine's indirect-count call; the window stays with
// OpenGL and no Vulkan swapchain is needed.
internal static partial class OcclusionCulling
{
    internal static readonly CountedDraw GlCounted = static (vao, ssbo, commands, count, max) =>
    {
        if (vao is null || max <= 0 || commands.Buffer == 0 || count.Buffer == 0) return false;

        try
        {
            GL.BindVertexArray(vao.VaoId);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer,
                ssbo ? ClientPlatformAbstract.singleIndexBufferId : vao.vboIdIndex);
            if (ssbo) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, vao.xyzVboId);

            GL.BindBuffer(BufferTarget.DrawIndirectBuffer, commands.Buffer);
            GL.BindBuffer(BufferTarget.ParameterBuffer, count.Buffer);
            GL.MultiDrawElementsIndirectCount(vao.drawMode, DrawElementsType.UnsignedInt, commands.Offset, count.Offset,
                max, CommandBytes);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger?.Warning("Komet: GPU-culled OpenGL draw failed: {0}", e.Message);
            return false;
        }
        finally
        {
            GL.BindBuffer(BufferTarget.ParameterBuffer, 0);
            GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
            if (ssbo) GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 3, 0);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
            GL.BindVertexArray(0);
        }
    };
}
