using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace Komet.Shapes;

// A chiseled block (BlockEntityMicroBlock, survival mod) builds a VoxelMaterial for each of its blocks and for each block of its
// chiseled neighbours on every tesselation, and VoxelMaterial.FromBlock looks up the six inside textures as "inside-" + face code: six
// new strings per material. Ruins are full of chiseled blocks, and in the world-join traces these strings were 86 MB in 50 s on the
// tesselation threads. The face codes are BlockFacing's own six strings, so the six names are made once; the lookup gets equal
// strings, the texture source sees the same keys. The rewrite replaces that one concatenation.
internal static class InsideTextures
{
    private const int MaxInstructions = 2048, Reach = 8, Faces = 6, MaxSites = 16;
    private const string Prefix = "inside-";
    private const string Material = "Vintagestory.GameContent.BlockEntityMicroBlock+VoxelMaterial";

    // Each face code and its name, filled on first use per face (the codes are BlockFacing's static strings)
    private static readonly string?[] Codes = new string?[Faces], Names = new string?[Faces];
    private static readonly Lock Gate = new();

    public static bool Enabled { get; set; } = true;
    public static bool Rewritten { get; private set; }

    public static void Install(Harmony harmony)
    {
        Rewritten = false;
        if (!NotNull(harmony) || Target() is not { } target) return;
        _ = NotNull(harmony.Patch(target, transpiler: new HarmonyMethod(Rewrite)));
    }

    internal static MethodInfo? Target()
    {
        var method = AccessTools.TypeByName(Material) is { } type ? AccessTools.DeclaredMethod(type, "FromBlock") : null;
        return method is null || Assert(method.IsStatic) ? method : null;
    }

    // Each String.Concat(string, string) right after an ldstr "inside-" (the compiler emits the loop's step twice), else the engine's IL
    internal static List<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
    {
        var name = AccessTools.Method(typeof(InsideTextures), nameof(Name));
        var concat = AccessTools.Method(typeof(string), nameof(string.Concat), [typeof(string), typeof(string)]);
        if (!Assert(Il.Take(instructions, MaxInstructions, out var code)) || !NotNull(name) || !NotNull(concat)) return code;
        var sites = new List<int>();
        for (var i = 0; i < Math.Min(code.Count, Il.MaxInstructions); i++)
        {
            if (code[i].opcode != OpCodes.Ldstr || code[i].operand is not Prefix) continue;
            var at = -1;
            for (var k = 1; k < Reach && i + k < code.Count; k++)
                if (code[i + k].Calls(concat))
                {
                    at = i + k;
                    break;
                }

            if (at < 0) return code; // a name built some other way: the engine's IL throughout
            sites.Add(at);
        }

        var done = sites.Count > 0;
        foreach (var at in sites.Bounded(MaxSites)) done &= Il.Substitute(code, at, name);
        Rewritten = done;
        return code;
    }

    // prefix + code, the same string object for each face code after its first
    internal static string Name(string prefix, string code)
    {
        if (!Enabled || !ReferenceEquals(prefix, Prefix) || !NotNull(code)) return string.Concat(prefix, code); // BlockFacing codes are set
        for (var i = 0; i < Faces; i++)
            if (ReferenceEquals(Volatile.Read(ref Codes[i]), code))
                return Names[i]!; // written before its code
        return Learn(prefix, code);
    }

    // A code not seen yet: kept in the next free slot (six at most, under the lock), else the engine's concatenation
    private static string Learn(string prefix, string code)
    {
        _ = NotNull(code) && Assert(ReferenceEquals(prefix, Prefix));
        var name = string.Concat(prefix, code);
        lock (Gate)
        {
            for (var i = 0; i < Faces; i++)
            {
                if (ReferenceEquals(Codes[i], code)) return Names[i]!;
                if (Codes[i] is not null) continue;
                Names[i] = name;
                Volatile.Write(ref Codes[i], code);
                return name;
            }
        }

        _ = Assert(Codes.Length == Faces); // more codes than faces: a new string, as the engine makes
        return name;
    }
}
