using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.MathTools;

namespace Komet.Rendering;

// AnimatableRenderer, the API's renderer for block entities with a running animation (doors, chests, translocators, the machinery in
// ruins), draws in Opaque, ShadowFar and ShadowNear (and OIT with transparent parts) every frame wherever the block entity is: no
// frustum test, no range. Each draw is a shader switch, a dozen uniforms, the joint matrices into a uniform buffer and a draw call. The
// bench world keeps 15 of them running 256 to 768 blocks away: 45 draws a frame, 38 of them outside the view or the shadow map, a
// tenth of the main thread. The prefix skips a draw whose geometry lies wholly outside the volume the stage's own matrices clip to.
//
// That volume is FrustumCulling's: MainRenderLoop computes its planes from the camera's matrices right before Opaque (and OIT follows
// without a change), SystemRenderShadowMap from the light's before any other ShadowFar or ShadowNear renderer (render order 0), all in
// world coordinates; the terrain culls against the same planes. The sphere tested holds every vertex the shader can place: the mesh's
// radius around its centre (measured when mainThreadInit uploads it), widened by each joint matrix the animator holds now (the one
// uploaded next: |M(v - c)| + |Mc - c| <= |M|F * r + |Mc - c|), taken through the model matrix as the draw builds it and widened again
// by what vertexwarp.vsh can add - wind bend, water waves, the drunk warp and the temporal glitch, from the uniforms it reads. A mesh
// whose joint ids reach past the animator's matrices, and any stage other than those four, is drawn as the engine draws it.
//
// A skipped draw leaves the GL state a drawn one leaves behind (the engine does not restore it, and a later renderer may draw with
// it): outside OIT the depth mask on, blend on in its standard mode, and face culling off - or on, in Opaque and ShadowNear, for a
// renderer without backface culling. Its shader switch ends on the shader that was active before, so there is nothing to restore;
// textures, the VAO and the uniform buffer are bound by whoever draws next. The skipped body is the one this was written against
// (EngineShape), and another mod's patch on it, which the skip would bypass, leaves every draw to the engine.
internal static class AnimatableCulling
{
    // EngineShape of Shaped() in Vintage Story 1.22.7
    internal const ulong Fingerprint = 0xDEECF7F8AC7EAFF5UL;

    private const int MaxVertices = 1 << 20, MaxJoints = 4096, MatrixFloats = 16;
    private const float Root3 = 1.7320508f;

    private static readonly ConditionalWeakTable<AnimatableRenderer, Bounds> Known = [];
    private static readonly float[] Model = Mat4f.Create(); // main thread only
    private static MethodBase?[] _seams = [];
    private static ILogger? _logger;
    private static bool _shaped, _foreign;

    public static bool Enabled { get; set; } = true;

    // The engine's body is not the one verified, or another mod patches it: every draw is the engine's
    internal static bool Blocked { get; private set; } = true;

    internal static bool Matched => _shaped;

    // Draws skipped and drawn, totals while Counting.Hud (main thread)
    public static long Culled { get; private set; }
    public static long Drawn { get; private set; }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "pos")]
    private static extern ref Vec3d Pos(AnimatableRenderer renderer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "animator")]
    private static extern ref AnimatorBase Animator(AnimatableRenderer renderer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "capi")]
    private static extern ref ICoreClientAPI Capi(AnimatableRenderer renderer);

    public static void Install(Harmony harmony, ILogger? logger, ulong fingerprint = Fingerprint)
    {
        (_shaped, Blocked, _seams, _logger, _foreign) = (false, true, Seams(), logger, false);
        if (!NotNull(harmony) || !Assert(_seams.Length == 2) || !NotNull(_seams[0]) || !NotNull(_seams[1])) return;
        _shaped = EngineShape.Matches(Shaped(), fingerprint, nameof(AnimatableCulling), logger);
        if (!_shaped) return;
        _ = NotNull(harmony.Patch(_seams[0], new HarmonyMethod(Frame)));
        _ = NotNull(harmony.Patch(_seams[1], postfix: new HarmonyMethod(Measured)));
        Recheck();
    }

    // The draw the prefix may skip and the upload whose mesh it measures; KometModSystem asks again on LevelFinalize
    internal static void Recheck()
    {
        var seamed = _shaped && Assert(_seams.Length == 2);
        _foreign = EngineShape.Report(_logger, nameof(AnimatableCulling), _foreign,
            seamed && EngineShape.Foreign(_seams, EngineShape.Kinds.Replacing, null, typeof(AnimatableCulling)));
        Blocked = !seamed || _foreign;
    }

    private static MethodBase?[] Seams()
    {
        MethodBase?[] seams =
        [
            AccessTools.DeclaredMethod(typeof(AnimatableRenderer), nameof(AnimatableRenderer.OnRenderFrame),
                [typeof(float), typeof(EnumRenderStage)]),
            AccessTools.DeclaredMethod(typeof(AnimatableRenderer), "mainThreadInit",
                [typeof(ICoreClientAPI), typeof(MeshData), typeof(EnumRenderStage)])
        ];
        return Assert(seams.Length == 2) ? seams : [];
    }

    // The body whose GL state a skipped draw reproduces
    internal static MethodBase?[] Shaped()
    {
        MethodBase?[] shaped = [.. Seams().AsSpan(0, 1)];
        return Assert(shaped.Length <= EngineShape.MaxMethods) ? shaped : [];
    }

    // Postfix on mainThreadInit: the mesh's centre and radius, and the highest joint id it uses (0 without joint ids)
    internal static void Measured(AnimatableRenderer __instance, MeshData meshdata)
    {
        if (!NotNull(__instance) || meshdata?.xyz is not { } xyz) return;
        var count = Math.Min(meshdata.VerticesCount, Math.Min(xyz.Length / 3, MaxVertices));
        if (!Assert(count >= 0)) return;
        var (min, max) = (new Vec3f(float.MaxValue, float.MaxValue, float.MaxValue),
            new Vec3f(float.MinValue, float.MinValue, float.MinValue));
        for (var i = 0; i < Math.Min(count, MaxVertices); i++)
        {
            var (x, y, z) = (xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
            (min.X, min.Y, min.Z) = (Math.Min(min.X, x), Math.Min(min.Y, y), Math.Min(min.Z, z));
            (max.X, max.Y, max.Z) = (Math.Max(max.X, x), Math.Max(max.Y, y), Math.Max(max.Z, z));
        }

        var centre = count == 0
            ? new Vec3f() : new Vec3f((min.X + max.X) / 2, (min.Y + max.Y) / 2, (min.Z + max.Z) / 2);
        var radius = 0f;
        for (var i = 0; i < Math.Min(count, MaxVertices); i++)
            radius = Math.Max(radius,
                Distance(xyz[3 * i] - centre.X, xyz[3 * i + 1] - centre.Y, xyz[3 * i + 2] - centre.Z));
        var bounds = new Bounds(centre, radius, Joints(meshdata.CustomInts));
        if (Finite(radius)) Known.AddOrUpdate(__instance, bounds);
    }

    // The highest joint id a vertex reads, from CustomInts, which the shader binds as jointId; none means it reads 0
    private static int Joints(CustomMeshDataPartInt? ints)
    {
        if (ints?.Values is not { } values || !Assert(ints.Count >= 0)) return 0;
        var (top, low, count) = (0, 0, Math.Min(ints.Count, values.Length));
        for (var i = 0; i < Math.Min(count, MaxVertices); i++)
            (top, low) = (Math.Max(top, values[i]), Math.Min(low, values[i]));
        return low < 0 ? MaxJoints : top; // a negative id is no joint this reasons about
    }

    // Prefix on OnRenderFrame: false skips a draw that would put nothing on screen or into a shadow map
    internal static bool Frame(AnimatableRenderer __instance, EnumRenderStage stage)
    {
        if (!Enabled || Blocked || !NotNull(__instance) || !__instance.ShouldRender ||
            stage is not (EnumRenderStage.Opaque or EnumRenderStage.OIT or EnumRenderStage.ShadowFar
                or EnumRenderStage.ShadowNear)) return true;
        // the engine's own exit
        if (__instance.mtmeshrefOpaque is not { Disposed: false, Initialized: true }) return true;
        if (Visible(__instance))
        {
            if (Counting.Hud) Drawn++;
            return true;
        }

        Leave(__instance, stage);
        if (Counting.Hud) Culled++;
        return false;
    }

    // Whether the sphere around everything the draw can put anywhere touches the stage's clip volume
    private static bool Visible(AnimatableRenderer renderer)
    {
        var (capi, animator, pos) = (Capi(renderer), Animator(renderer), Pos(renderer));
        if (capi?.Render?.DefaultFrustumCuller is not { } culler || animator?.Matrices is not { } matrices ||
            pos is null || !Known.TryGetValue(renderer, out var bounds)) return true;
        if (!Assert(matrices.Length % MatrixFloats == 0) || bounds.TopJoint >= matrices.Length / MatrixFloats)
            return true;
        var reach = Reach(matrices, bounds);
        var (centre, scale) = Place(renderer, pos, bounds.Centre);
        var radius = scale * reach + Warp(capi.Render.ShaderUniforms);
        return !Finite(radius) || culler.SphereInFrustum(centre.X, centre.Y, centre.Z, radius);
    }

    // The farthest any joint matrix can take a vertex from the mesh's centre: |M(v - c)| + |Mc - c| over all of them
    private static float Reach(float[] matrices, Bounds bounds)
    {
        var (c, reach) = (bounds.Centre, 0f);
        for (var j = 0; j < Math.Min(matrices.Length / MatrixFloats, MaxJoints); j++)
        {
            var m = matrices.AsSpan(j * MatrixFloats, MatrixFloats);
            var (x, y, z) = (m[0] * c.X + m[4] * c.Y + m[8] * c.Z + m[12] - c.X,
                m[1] * c.X + m[5] * c.Y + m[9] * c.Z + m[13] - c.Y,
                m[2] * c.X + m[6] * c.Y + m[10] * c.Z + m[14] - c.Z);
            reach = Math.Max(reach, Frobenius(m) * bounds.Radius + Distance(x, y, z));
        }

        return Assert(reach >= 0) ? reach : float.PositiveInfinity;
    }

    // The mesh centre in world coordinates and the largest stretch of the model matrix, built as OnRenderFrame builds it without the
    // camera offset: the custom transform, or the half-block turn about Y with the renderer's scale
    private static (Vec3d Centre, float Scale) Place(AnimatableRenderer renderer, Vec3d pos, Vec3f c)
    {
        var model = Model;
        _ = Mat4f.Identity(model);
        if (renderer.CustomTransform is { Length: MatrixFloats } custom) _ = Mat4f.Multiply(model, model, custom);
        else
        {
            _ = Mat4f.Translate(model, model, 0.5f, 0f, 0.5f);
            _ = Mat4f.Scale(model, model, renderer.ScaleX, renderer.ScaleY, renderer.ScaleZ);
            _ = Mat4f.RotateY(model, model, (renderer.rotationDeg?.Y ?? 0f) * GameMath.DEG2RAD);
            _ = Mat4f.Translate(model, model, -0.5f, 0f, -0.5f);
        }

        var centre = new Vec3d(pos.X + model[0] * c.X + model[4] * c.Y + model[8] * c.Z + model[12],
            pos.Y + model[1] * c.X + model[5] * c.Y + model[9] * c.Z + model[13],
            pos.Z + model[2] * c.X + model[6] * c.Y + model[10] * c.Z + model[14]);
        return (centre, Frobenius(model));
    }

    // How far vertexwarp.vsh can move a vertex, per its uniforms: the wind bend (at most 4) and its wiggle, water waves, the global
    // warp, the drunk warp (at most the intensity per axis) and the temporal glitch (at most 50 * (waviness - 0.1) per axis)
    private static float Warp(DefaultShaderUniforms? uniforms)
    {
        if (!NotNull(uniforms)) return float.PositiveInfinity;
        var wind = 6f + 6f * Math.Abs(uniforms.WindWaveIntensity) * (1 + Math.Abs(uniforms.WindSpeed)) +
                   Math.Abs(uniforms.WaterWaveIntensity);
        var warp = 50f * Math.Max(0f, uniforms.GlitchWaviness - 0.1f) + 0.1f * Math.Abs(uniforms.GlobalWorldWarp) +
                   2f * Math.Abs(uniforms.PerceptionEffectIntensity);
        return wind + Root3 * warp;
    }

    // The GL state OnRenderFrame leaves behind when it draws, set without drawing
    private static void Leave(AnimatableRenderer renderer, EnumRenderStage stage)
    {
        if (stage == EnumRenderStage.OIT || Capi(renderer)?.Render is not { } render) return;
        render.GLDepthMask(true);
        render.GlToggleBlend(true);
        if (stage is EnumRenderStage.Opaque or EnumRenderStage.ShadowNear && !renderer.backfaceCulling)
            render.GlEnableCullFace();
        else render.GlDisableCullFace();
    }

    // At least the largest stretch of the matrix's 3x3 part
    private static float Frobenius(ReadOnlySpan<float> m) => MathF.Sqrt(m[0] * m[0] + m[1] * m[1] + m[2] * m[2] +
        m[4] * m[4] + m[5] * m[5] + m[6] * m[6] + m[8] * m[8] + m[9] * m[9] + m[10] * m[10]);

    private static float Distance(float x, float y, float z) => MathF.Sqrt(x * x + y * y + z * z);

    private sealed record Bounds(Vec3f Centre, float Radius, int TopJoint);
}
