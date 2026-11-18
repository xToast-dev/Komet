using System.Collections.Frozen;
using System.Numerics;

namespace Komet.Vulkan;

// Komet's own full-target program (the masked clear): ported on a builder through the shader cache
// (ProgramPorts.Made), so a later start reads it from disk; drawn as one triangle with its uniforms pushed.
internal sealed partial class SceneRenderer
{
    private static readonly FrozenDictionary<string, int> NoAttributes =
        FrozenDictionary<string, int>.Empty;

    // The port once made; begun on the first ask. wait: until it is made (a draw that cannot be left out)
    private static GlslPort.Ported? OwnPort(OwnProgram own, bool wait)
    {
        if (own.Port is not null || own.Unported.Length > 0) return own.Port;

        var (stages, hot) = (own.Stages, own.Hot);
        var building = own.Building ??= new Build<GlslPort.Ported>(Builds.Kind.Port,
            () => (ProgramPorts.Made(stages, out var why, hot), why));
        if (!building.Done && !(wait && building.Awaited(Hitches.Kind.Port))) return null;

        (own.Port, own.Building) = (building.Take(out var error), null);
        if (own.Port is null) own.Unported = error.Length > 0 ? error : "?";

        return own.Port;
    }

    private static void OwnGone(OwnProgram own)
    {
        own.Building?.Drop();
        own.Building = null;
    }

    // No blending; each colour attachment written through its mask (masks null: whole), those output leaves out (-1:
    // none) not at all
    private static GlTap.Fixed FullTarget(VulkanFrame.Target target,
        (bool Colors, int Output, GlTap.BlendState? Masks) colors,
        uint caps, uint depth, uint stencil)
    {
        Span<uint> words = stackalloc uint[GlTap.MaxBuffers];
        words.Clear();
        for (var i = 0; colors.Colors && i < Math.Min(target.Colors.Length, GlTap.MaxBuffers); i++)
        {
            if (target.Colors[i] is not null && (colors.Output < 0 || colors.Output == i))
            {
                words[i] = colors.Masks is { } masks ? masks[i] & (FullMask << 22) : FullMask << 22;
            }
        }

        return new GlTap.Fixed(caps, depth, 0, 0,
            new GlTap.BlendState(words[0], words[1], words[2], words[3], words[4], words[5], words[6], words[7]),
            stencil);
    }

    // Each pushed member named in names gets the first components of its value (a float the X)
    private static void PushedValues(Span<uint> into, GlslPort.Block hot, string[] names, ReadOnlySpan<Vector4> values)
    {
        if (!Assert(into.Length * 4 >= hot.Size)) return;

        into.Clear();
        foreach (var member in hot.Members.Bounded(16))
        {
            var (at, which, count) = (member.Offset / 4, Array.IndexOf(names, member.Name),
                Math.Min(GlslPort.Shape(member.Type).Rows, 4));
            if (!Index(which, values.Length) || at < 0 || at + count > into.Length) continue;

            var value = values[which];
            for (var i = 0; i < Math.Min(count, 4); i++)
            {
                into[at + i] = BitConverter.SingleToUInt32Bits(value[i]);
            }
        }
    }

    private sealed class OwnProgram(string name, string vertex, string fragment, string[] hot)
    {
        public ProgramPorts.Stages Stages { get; } = new(name, vertex, fragment, NoAttributes);
        public HashSet<string> Hot { get; } = new(hot, StringComparer.Ordinal);
        public Build<GlslPort.Ported>? Building { get; set; }
        public GlslPort.Ported? Port { get; set; }
        public string Unported { get; set; } = ""; // why it has no port
    }
}
