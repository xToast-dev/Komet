using Komet.Vulkan;
using OpenTK.Graphics.OpenGL;

namespace Komet.Test.Gpu;

[NonParallelizable]
public sealed class GlTapGpuTests
{
    private static readonly float[] Fog = [0.5f, 0.6f, 0.7f, 1], Light = [0.1f, 0.2f, 0.3f];

    [Test]
    public void WhatOpenGlIsGivenTheMirrorHoldsToo()
    {
        GameInstall.RequireAssets();
        using var rig = GpuRig.Open(GpuRig.Vulkan);
        using var scene = new TerrainScene(true);
        var mirror = new UniformMirror();
        GL.ProgramUniform1(scene.Program, GL.GetUniformLocation(scene.Program, "fogDensityIn"), 0.25f);
        Assert.That(GlTap.Tap(), Is.True);
        try
        {
            GlTap.Watch(scene.Program, scene.Ported, mirror);
            var density = scene.Ported.VertexUniforms.Members.Single(u => u.Name == "fogDensityIn");
            Assert.That(BitConverter.UInt32BitsToSingle(mirror.EntryOf(density.Name)!.Held[0]), Is.EqualTo(0.25f),
                "set before the watch began: seeded from the program");
            GL.UseProgram(scene.Program);
            GL.Uniform4(GL.GetUniformLocation(scene.Program, "rgbaFogIn"), 0.5f, 0.6f, 0.7f, 1f);
            GL.Uniform3(GL.GetUniformLocation(scene.Program, "pointLights[1]"), 1, Light);
            GL.Uniform1(GL.GetUniformLocation(scene.Program, "terrainTex"), 5);
            GL.ActiveTexture(TextureUnit.Texture5);
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            var sampler = GL.GenSampler();
            GL.BindSampler(5, sampler);
            GL.ActiveTexture(TextureUnit.Texture0);
            Assert.That(GlTap.Program, Is.EqualTo(scene.Program));
            GL.UseProgram(0);

            var fog = scene.Ported.VertexUniforms.Members.Single(u => u.Name == "rgbaFogIn");
            Assert.That(mirror.EntryOf(fog.Name)!.Held.Select(BitConverter.UInt32BitsToSingle), Is.EqualTo(Fog));
            var lights = scene.Ported.VertexUniforms.Members.Single(u => u.Name == "pointLights");
            Assert.That(mirror.EntryOf(lights.Name)!.Held.Skip(3).Take(3).Select(BitConverter.UInt32BitsToSingle), Is.EqualTo(Light),
                "element 1 of the array");
            Assert.That(GlTap.Unit((int)mirror.EntryOf("terrainTex")!.Held[0]), Is.EqualTo((texture, sampler)));
            var held = new float[4];
            GL.GetUniform(scene.Program, GL.GetUniformLocation(scene.Program, "rgbaFogIn"), held);
            Assert.That(held, Is.EqualTo(Fog), "and OpenGL got it");
            GL.DeleteTexture(texture);
            GL.DeleteSampler(sampler);
        }
        finally
        {
            GlTap.Untap();
        }
    }

}
