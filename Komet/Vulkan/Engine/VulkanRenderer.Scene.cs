using Vintagestory.Client.NoObf;
using static Komet.Rendering.Fields;

namespace Komet.Vulkan;

// With the scene on, GlTap's Touch group judges every OpenGL call beside Vulkan's work, so the terrain's own close points
// (after the opaque terrain, the transparent terrain, the water plants) are not needed and the segments run on.
internal static partial class VulkanRenderer
{
    public enum Part
    {
        Sky,
        Entities,
        Particles,
        Post,
        Other
    }

    private static readonly Dictionary<string, Part> Parts = new(StringComparer.Ordinal)
    {
        ["sky"] = Part.Sky, ["nightsky"] = Part.Sky, ["celestialobject"] = Part.Sky, ["aurora"] = Part.Sky,
        ["cloudvolumetric"] = Part.Sky, ["cloudmap"] = Part.Sky, ["entityanimated"] = Part.Entities,
        ["shadowmapentityanimated"] = Part.Entities, ["helditem"] = Part.Entities, ["instanced"] = Part.Entities,
        ["particlescube"] = Part.Particles, ["particlesquad"] = Part.Particles, ["particlesquad2d"] = Part.Particles,
        ["findbright"] = Part.Post, ["blur"] = Part.Post, ["godrays"] = Part.Post, ["ssao"] = Part.Post,
        ["bilateralblur"] = Part.Post, ["luma"] = Part.Post, ["blit"] = Part.Post, ["final"] = Part.Post,
        ["transparentcompose"] = Part.Post, ["colorgrade"] = Part.Post
    };

    private static SceneRenderer? _scene;
    private static readonly bool[] Parting = new bool[5]; // the parts the scene takes this frame
    private static readonly Dictionary<int, Part> PartOf = []; // by GL program, worked out once

    public static bool Scene { get; set; } = true;
    public static bool Sky { get; set; } = true;
    public static bool Entities { get; set; } = true;
    public static bool Particles { get; set; } = true;
    public static bool PostProcessing { get; set; } = true;
    public static bool Other { get; set; } = true;

    private static bool AnyPart => Array.IndexOf(Parting, true) >= 0;

    private static bool SceneOn => _scene is not null && GlTap.Scene && AnyPart;

    internal static Part PartNamed(string name) =>
        NotNull(name) && Parts.TryGetValue(name, out var part) && Assert(part <= Part.Other) ? part : Part.Other;

    private static bool Takes(int program, string name)
    {
        if (!Assert(program > 0) || _api?.World is not ClientMain game) return false;
        if (!PartOf.TryGetValue(program, out var part))
        {
            if (PartOf.Count >= 1024) PartOf.Clear();
            PartOf[program] = part = PartNamed(name);
        }

        var stage = Stage(game);
        var takes = Parting[(int)part] && (Staged(stage) || Windowed(stage));
        if (takes) Mark(part); // its draw is recorded next
        return takes;
    }

    private static bool Staged(EnumRenderStage stage) =>
        Assert(stage >= EnumRenderStage.Before) && stage is EnumRenderStage.ShadowFar or EnumRenderStage.ShadowNear or
            EnumRenderStage.Opaque or EnumRenderStage.OIT or EnumRenderStage.AfterOIT or
            EnumRenderStage.AfterPostProcessing or EnumRenderStage.AfterFinalComposition;

    // A depth-only draw in a shadow pass commutes with OpenGL's work: it joins the deferred segment
    private static bool Deferring() =>
        _api?.World is ClientMain game && Assert(Parting.Length == 5) &&
        Stage(game) is EnumRenderStage.ShadowFar or EnumRenderStage.ShadowNear;

    private static bool ClearsTaken() =>
        _api?.World is ClientMain game && Assert(Parting.Length == 5) && AnyPart &&
        (Staged(Stage(game)) || Windowed(Stage(game)) || (Stage(game) == EnumRenderStage.Before && LazyHandoff));

    private static bool ClearDeferring() =>
        _api?.World is ClientMain game && Assert(Parting.Length == 5) && NotNull(_scene) &&
        Stage(game) == EnumRenderStage.Before;

    private static void DecideScene()
    {
        var on = Enabled && Scene;
        (Parting[0], Parting[1], Parting[2]) = (on && Sky, on && Entities, on && Particles);
        (Parting[3], Parting[4]) = (on && PostProcessing, on && Other);
        _ = Assert(Parting.Length == 5) && Assert(!AnyPart || on);
    }

    // Switching modes taps anew and makes every mirror again: writes went unheard in between
    private static bool Tapped(bool scene)
    {
        if (GlTap.Tapped && GlTap.Scene == scene) return Assert(GlTap.Tapped);
        GlTap.Untap();
        _watched = false;
        _scene?.Mirrors.Forget();
        if (!GlTap.Tap(scene))
        {
            _logger?.Warning("Komet: OpenTK's GL table lacks {0}", GlTap.Missing);
            return scene ? GlTap.Tap() : Assert(!GlTap.Tapped);
        }

        return Assert(GlTap.Tapped);
    }

    // What Hooked wired last: unchanged, it runs every frame without making the dozen delegates of the scene's methods anew
    private static (SceneRenderer? Scene, TerrainRenderer? Renderer, bool Lazy) _wired;

    private static void Hooked(TerrainRenderer renderer, bool on)
    {
        if (!NotNull(renderer)) return;
        if (on && _scene is null) _scene = SceneRenderer.Create(renderer, _logger);
        if (on && _scene is { } wired && _wired == (wired, renderer, LazyHandoff) && ReferenceEquals(GlTap.Writes, wired)) return;
        if (on && _scene is { } scene)
        {
            _wired = (scene, renderer, LazyHandoff);
            (scene.Takes, scene.Deferring, scene.Clearing, scene.SamplerObject) = (Takes, Deferring, ClearsTaken, Sampler);
            scene.ClearDeferring = ClearDeferring;
            (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Blitting) = (scene.Draw, scene.Clear, scene, scene.Blit);
            (GlTap.Querying, GlTap.Answering, GlTap.Touching) = (scene.Querying, scene.Answer, Touched);
            (renderer.Frame.Opened, renderer.Frame.Ending) = (scene.Opened, FrameEnding);
            (renderer.Frame.PoolsFirst, Rendering.FacePacking.Through) = (renderer.PoolsFirst, LazyHandoff ? renderer.Faces : null);
            unsafe
            {
                Rendering.FacePacking.Room = LazyHandoff ? renderer.Room : null;
            }

            Rendering.FacePacking.Committed = LazyHandoff ? renderer.Committed : null;
            renderer.Frame.Submitting = Rendering.FacePacking.Settle;
            (GlTap.Quieting, renderer.Frame.Lazy) = (renderer.Frame.GlQuiet, LazyHandoff);
            renderer.Copies.Trusting = true;
        }
        else
        {
            _wired = default;
            _scene?.FlushEarly(); // OpenGL's again, before it hears nothing more of the scene
            if (_scene is { } off) off.Between = false;
            (GlTap.Drawing, GlTap.Clearing, GlTap.Writes, GlTap.Querying, GlTap.Answering) = (null, null, null, null, null);
            GlTap.Blitting = null;
            (GlTap.Touching, renderer.Frame.Opened, renderer.Frame.Ending, GlTap.Quieting) = (null, null, null, null);
            (Rendering.FacePacking.Through, Rendering.FacePacking.Room, Rendering.FacePacking.Committed) = (null, null, null);
            renderer.Frame.Lazy = false;
            renderer.Copies.Trusting = false;
        }

        GlTap.ProgramGone = Deleted;
        _ = Assert(!on || GlTap.Drawing is not null || _scene is null);
    }

    // Runs with no segment open
    private static void FrameEnding()
    {
        if (_scene is not { } scene || !Assert(_renderer is not null)) return;
        scene.Mirrors.Ending();
        scene.Resolve();
        _renderer!.PoolsEnding();
    }

    private static void Touched(GlTap.Touch kind, uint id)
    {
        if (_renderer is not { } renderer || !Assert(kind <= GlTap.Touch.Mipmap)) return;
        _scene?.Touching(kind, id); // the clears kept between frames come first where it may touch their images
        renderer.Touched(kind, id);
    }

    // GL program names are reused: the port and mirror go with the program
    private static void Deleted(uint program)
    {
        if (!Assert(program > 0) || !Assert(program < int.MaxValue)) return;
        _ = PartOf.Remove((int)program);
        ProgramPorts.Forget((int)program);
        var at = Array.IndexOf(Built, (int)program);
        if (at >= 0) Built[at] = 0; // a rebuilt terrain program with the same name is ported anew
        Guarded(() =>
        {
            _scene?.Forget((int)program);
            _renderer?.Forget((int)program);
        });
    }

    private static void SceneGone()
    {
        _scene?.FlushEarly(); // the clears kept between frames are OpenGL's again
        _scene?.Dispose();
        _scene = null;
        PartOf.Clear();
        Array.Clear(Parting);
        _ = Assert(PartOf.Count == 0) && Assert(!AnyPart);
    }

    private static string SceneParts()
    {
        if (!SceneOn || !Assert(Parting.Length == 5)) return "";
        string[] names = ["sky", "entities", "particles", "post-processing", "the rest"];
        var on = names.Where((_, i) => Parting[i]).ToArray();
        _ = Assert(on.Length <= names.Length);
        return string.Join(", ", on);
    }
}
