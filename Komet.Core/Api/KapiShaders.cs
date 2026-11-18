using Komet.Core;

namespace Komet.Api;

/// <summary>Which renderer draws the world.</summary>
public enum KapiRenderer
{
    /// <summary>The game's own OpenGL renderer.</summary>
    // ReSharper disable once InconsistentNaming the renderer's proper name
    OpenGL,

    /// <summary>Komet's Vulkan renderer (Komet › Overview › Renderer).</summary>
    Vulkan
}

/// <summary>Where a <see cref="KapiPass" /> draws.</summary>
public enum KapiStage
{
    /// <summary>
    ///     After the game's final composition, before the GUI: the finished picture, tone mapped, with bloom, god rays
    ///     and vignettes; its depth is the scene's.
    /// </summary>
    Final
}

/// <summary>
///     A fullscreen pass of the mod's own, for <see cref="KapiShaders.AddPass" /> and
///     <see cref="KapiShaders.TryAddPasses" />.
/// </summary>
/// <remarks>
///     <see cref="Fragment" /> is GLSL 330 that defines <c>main()</c> and writes <c>color</c>. Komet puts this prelude
///     before it and sets uniforms by name before each draw:
///     <code>
/// #version 330 core
/// #define KOMET 1
/// #define KOMET_VULKAN 1          // only while Vulkan draws
/// in vec2 uv;                 // 0..1, (0, 0) bottom left
/// out vec4 color;
/// uniform sampler2D scene;    // the picture so far (a copy: read any pixel)
/// uniform sampler2D depth;    // the scene's depth, 0..1
/// uniform vec2 resolution;    // in pixels
/// uniform float time;         // seconds, for animation
/// </code>
///     Passes of one stage draw in <see cref="Order" />, lower first. Line numbers in compile errors are the mod's.
/// </remarks>
/// <param name="id">The pass's name, unique within the mod.</param>
/// <param name="fragment">The GLSL body after the prelude.</param>
public sealed class KapiPass(string id, string fragment)
{
    /// <summary>The pass's name, unique within the mod.</summary>
    public string Id { get; } = NotNull(id) ? id : "";

    /// <summary>The GLSL body after the prelude: <c>main()</c> and whatever it needs.</summary>
    public string Fragment { get; } = NotNull(fragment) ? fragment : "";

    /// <summary>Where it draws; <see cref="KapiStage.Final" /> by default.</summary>
    public KapiStage Stage { get; init; } = KapiStage.Final;

    /// <summary>Passes of one stage draw lower first.</summary>
    public int Order { get; init; }

    /// <summary>Asked each frame; null draws always. A callback that throws turns the pass off.</summary>
    public Func<bool>? Enabled { get; init; }

    /// <summary>Uniforms of this pass by name, set before each draw (1 to 4 numbers each).</summary>
    public Dictionary<string, Func<float[]>> Uniforms { get; } = [];

    // A copy as registered: uniforms the mod adds later do not reach the registered pass
    internal KapiPass Copy()
    {
        var pass = new KapiPass(Id, Fragment) { Stage = Stage, Order = Order, Enabled = Enabled };
        foreach (var (name, value) in Uniforms.Bounded(ShaderHub.MaxUniforms)) pass.Uniforms[name] = value;
        return pass;
    }
}

/// <summary>
///     Shaders, through <see cref="KapiMod.Shaders" />: defines, uniforms and source edits for the game's own programs,
///     and fullscreen passes of the mod's own.
/// </summary>
/// <remarks>
///     Everything runs on whichever renderer draws: under Vulkan, Komet ports every program itself. Every program, pass
///     included, has <c>#define KOMET 1</c>, and <c>#define KOMET_VULKAN 1</c> while Vulkan draws.
/// </remarks>
/// <example>
///     <code language="csharp">
/// komet.Shaders.Uniform("mymodWarmth", () =&gt; config.Warmth);
/// var grade = komet.Shaders.TryAddPasses(new KapiPass("grade", source) { Enabled = () =&gt; config.Grade });
/// if (!grade.Active) StartOwnRenderer(); // the error is in grade.Error and in the log
/// </code>
/// </example>
public sealed class KapiShaders
{
    /// <summary>How many defines, uniforms, edits and passes Komet takes from all mods together.</summary>
    public const int MaxDefines = ShaderHub.MaxDefines,
        MaxUniforms = ShaderHub.MaxUniforms,
        MaxEdits = ShaderHub.MaxEdits,
        MaxPasses = ShaderHub.MaxPasses;

    private readonly KapiMod _mod;

    internal KapiShaders(KapiMod mod) => _mod = mod;

    /// <summary>Komet's shader module runs and takes registrations.</summary>
    public static bool Available => ShaderHub.Available;

    /// <summary>The renderer drawing the world now; <see cref="RendererChanged" /> tells when it switches.</summary>
    public static KapiRenderer Renderer => ShaderHub.Renderer;

    /// <summary>
    ///     The renderer switched. Komet reloads the game's shaders and rebuilds the passes itself, so
    ///     <c>KOMET_VULKAN</c> is right again. A handler that throws is logged once and unsubscribed.
    /// </summary>
    public event EventHandler? RendererChanged
    {
        add
        {
            if (value is null || !_mod.Keep(() => ShaderHub.RendererChanged -= value)) return;

            ShaderHub.RendererChanged += value;
        }
        remove => ShaderHub.RendererChanged -= value;
    }

    /// <summary>
    ///     Adds <c>#define name value</c> to programs, after their <c>#version</c> line. The value is read at each load:
    ///     after changing it, call <see cref="Reload" />.
    /// </summary>
    /// <param name="name">A GLSL name.</param>
    /// <param name="value">The define's value, read at each load.</param>
    /// <param name="programs">
    ///     The game's program names (<c>chunkopaque</c>, <c>final</c>, ...) or <c>pass:id</c> for a pass of the mod;
    ///     none gives it to every program and every pass of the mod.
    /// </param>
    /// <returns>The registration; disposing it takes the define away after the next reload.</returns>
    public KapiRegistration Define(string name, Func<string> value, params string[] programs) =>
        _mod.Keep(ShaderHub.Define(_mod.ModId, name, value, programs));

    /// <summary>
    ///     A uniform set on every program and pass that declares it, each time it draws. Live: no reload needed.
    /// </summary>
    /// <param name="name">A GLSL name.</param>
    /// <param name="value">The value, asked once per frame.</param>
    /// <returns>The registration.</returns>
    public KapiRegistration Uniform(string name, Func<float> value) =>
        _mod.Keep(ShaderHub.Uniform(_mod.ModId, name, value));

    /// <summary>A <c>vec2</c>, <c>vec3</c> or <c>vec4</c> uniform, by the length of the array (1 to 4 numbers).</summary>
    /// <param name="name">A GLSL name.</param>
    /// <param name="value">The numbers, asked once per frame.</param>
    /// <returns>The registration.</returns>
    public KapiRegistration Uniform(string name, Func<float[]> value) =>
        _mod.Keep(ShaderHub.Uniform(_mod.ModId, name, value));

    /// <summary>Rewrites a program's source as it loads. Komet's own edits come first, so match lines loosely.</summary>
    /// <param name="program">The game's program name, or <c>*</c> for every program.</param>
    /// <param name="vertex">Takes the vertex stage's source and gives the new one; null leaves it alone.</param>
    /// <param name="fragment">Takes the fragment stage's source and gives the new one; null leaves it alone.</param>
    /// <returns>The registration; disposing it takes the edit away after the next reload.</returns>
    public KapiRegistration Edit(string program, System.Func<string, string>? vertex,
        System.Func<string, string>? fragment) =>
        _mod.Keep(ShaderHub.Edit(_mod.ModId, program, vertex, fragment));

    /// <summary>Builds the game's shaders anew with the current defines and edits. Several calls in one frame reload once.</summary>
    public static void Reload() => ShaderHub.Reload();

    /// <summary>
    ///     Adds a pass, built when the renderer is ready and again after every reload. The same id replaces the mod's
    ///     pass added before. Use <see cref="TryAddPasses" /> to know at once whether it compiles.
    /// </summary>
    /// <param name="pass">The pass.</param>
    /// <returns>The registration; refused (not <see cref="KapiRegistration.Active" />) for an invalid pass.</returns>
    public KapiRegistration AddPass(KapiPass pass) =>
        NotNull(pass)
            ? _mod.Keep(ShaderHub.AddPass(_mod.ModId, pass.Copy()), kind: KapiKind.Pass)
            : _mod.Keep(Nothing.Instance, "a pass is needed");

    /// <summary>
    ///     Compiles the passes now, on the main thread, and adds them together, or none of them. For a mod that falls
    ///     back to a renderer of its own when Komet cannot take its effect.
    /// </summary>
    /// <param name="passes">One or more passes, drawn as one effect.</param>
    /// <returns>The registration; when refused, <see cref="KapiRegistration.Error" /> says why.</returns>
    public KapiRegistration TryAddPasses(params KapiPass?[]? passes)
    {
        if (passes is not { Length: > 0 } || passes.Any(p => p is null))
            return _mod.Keep(Nothing.Instance, "one or more passes are needed");

        var result = ShaderHub.TryAddEffect(_mod.ModId, [.. passes.OfType<KapiPass>().Select(static p => p.Copy())]);
        return _mod.Keep(result.Registration ?? Nothing.Instance, result.Error, KapiKind.Pass);
    }

    /// <summary>
    ///     Rewrites the fragment of another mod's pass (or one of this mod's) before it compiles. Edits run in the order
    ///     they were added, for passes added later too.
    /// </summary>
    /// <param name="modId">The mod the pass belongs to.</param>
    /// <param name="passId">The pass's id.</param>
    /// <param name="fragment">Takes the pass's fragment and gives the new one.</param>
    /// <returns>The registration.</returns>
    public KapiRegistration EditPass(string modId, string passId, System.Func<string, string> fragment) =>
        _mod.Keep(ShaderHub.EditPass(_mod.ModId, modId, passId, fragment));
}
