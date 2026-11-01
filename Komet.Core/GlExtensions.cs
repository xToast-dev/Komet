using OpenTK.Graphics.OpenGL;

namespace Komet.Core;

// The GL extensions the driver offers, asked once (main thread, with a context). glGetError is left alone: it would steal an
// error the engine's CheckGlError expects.
internal static class GlExtensions
{
    private const int MaxExtensions = 1024;
    private static HashSet<string>? _extensions;

    public static bool Offered(string extension)
    {
        if (!NotNull(extension)) return false;
        if (_extensions is { } known) return known.Contains(extension);
        var count = GL.GetInteger(GetPName.NumExtensions);
        if (!Assert(count > 0)) return false;
        var made = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Math.Min(count, MaxExtensions); i++)
            if (GL.GetString(StringNameIndexed.Extensions, i) is { } name)
                _ = made.Add(name);
        _extensions = made;
        return made.Contains(extension);
    }
}
