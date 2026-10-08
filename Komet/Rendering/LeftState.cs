using Komet.Vulkan;

namespace Komet.Rendering;

// The GL state a skipped draw leaves behind (AnimatableCulling, PotCulling), set once per run of skipped draws: GlTap sees every
// state call and bumps Version, so a draw that finds the version where the last one left it needs no GL calls. Untapped, every
// call is made.
internal static class LeftState
{
    // AnimatableCulling's with and without face culling; PotCulling's: face culling off, standard blending
    public enum Kind
    {
        None,
        Culling,
        Unculled,
        Pot
    }

    private static long _version = -1;
    private static Kind _state;

    public static bool Holds(Kind state) =>
        Assert(state != Kind.None) && GlTap.Tapped && _state == state && GlTap.Version == _version;

    public static void Left(Kind state)
    {
        (_state, _version) = (state, GlTap.Tapped ? GlTap.Version : -1);
        _ = Assert(state != Kind.None);
    }
}
