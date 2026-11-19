using Komet.Chunks;
using Komet.Core;
using Komet.World;
using Vintagestory.ServerMods;
using ColumnNoise = Vintagestory.API.MathTools.NewNormalizedSimplexFractalNoise.ColumnNoise;

namespace Komet.Test.Core;

public sealed class IlTests
{
    private static readonly ConstructorInfo Queue = AccessTools.Constructor(typeof(QueueOfInt), []);

    private static Site Feature(string name)
    {
        _ = typeof(GenTerra).Assembly; // VSEssentials, which holds the cloud renderer, is found by name
        var decompress = AccessTools.Method(typeof(ICompression), nameof(ICompression.Decompress),
            [typeof(byte[]), typeof(int), typeof(int)]);
        return name switch
        {
            nameof(CloudTileScratch) => new Site(CloudTileScratch.Target()!,
                c => c.opcode == OpCodes.Newobj && Equals(c.operand, CloudTileScratch.Constructor()),
                AccessTools.Method(typeof(CloudTileScratch), nameof(CloudTileScratch.At)), true,
                code => CloudTileScratch.Substitute(code, CloudTileScratch.Target()!)),
            nameof(DecompressScratch) => new Site(DecompressScratch.Target()!, c => c.Calls(decompress),
                AccessTools.Method(typeof(DecompressScratch), nameof(DecompressScratch.Decompress)), false,
                DecompressScratch.Rewrite),
            _ => new Site(LightScratch.Method("SpreadDarkness")!,
                c => c.opcode == OpCodes.Newobj && Equals(c.operand, Queue),
                AccessTools.Method(typeof(LightScratch), nameof(LightScratch.Queue)), true,
                LightScratch.RewriteDarkness)
        };
    }

    // Single finds the one site the feature's transpiler rewrites, and Substitute turns it into the same call in place; where that is
    // all the feature changes, the whole body is the feature's
    [TestCase(nameof(CloudTileScratch))]
    [TestCase(nameof(DecompressScratch))]
    public void SingleFindsAndSubstituteRewritesTheFeaturesSite(string feature)
    {
        var site = Feature(feature);
        var theirs = site.Transpile(PatchProcessor.GetOriginalInstructions(site.Target));
        var code = PatchProcessor.GetOriginalInstructions(site.Target);
        var at = Il.Single(code, site.Match);
        Assert.That(at, Is.GreaterThanOrEqualTo(0).And.EqualTo(theirs.FindIndex(c => c.Calls(site.Mine))),
            "the site the feature rewrites");
        var before = code[at];
        Assert.Multiple(() =>
        {
            Assert.That(Il.Count(code, site.Match), Is.EqualTo(1));
            Assert.That(Il.Single(code, c => c.opcode.FlowControl == FlowControl.Call), Is.EqualTo(-1), "several");
            Assert.That(Il.Single(code, _ => false), Is.EqualTo(-1), "none");
            Assert.That(Il.Substitute(code, at, site.Mine), Is.True);
            Assert.That(code[at], Is.SameAs(before), "in place: labels and exception blocks stay on it");
            Assert.That((code[at].opcode, code[at].operand), Is.EqualTo((theirs[at].opcode, theirs[at].operand)));
            if (site.OnlyThere)
            {
                Assert.That(code.Select(Shape), Is.EqualTo(theirs.Select(Shape)), "the feature's whole body");
            }
        });
    }

    [Test]
    public void SubstituteRefusesAnotherStackEffect()
    {
        var site = Feature(nameof(CloudTileScratch));
        var code = PatchProcessor.GetOriginalInstructions(site.Target);
        var at = Il.Single(code, site.Match);
        var abs = AccessTools.Method(typeof(Math), nameof(Math.Abs), [typeof(double)]);
        Assert.Multiple(() =>
        {
            Assert.That(Il.Substitute(code, at, abs), Is.False, "one double where new Vec3d takes three");
            Assert.That((code[at].opcode, code[at].operand),
                Is.EqualTo((OpCodes.Newobj, (object)CloudTileScratch.Constructor()!)));
        });
    }

    // The value pushed at an index goes to the instruction that pops it, as the operand it lands in; a jump or a label on the way, or
    // a walk past its reach, proves nothing
    [Test]
    public void ConsumerFollowsTheStack()
    {
        var max = AccessTools.Method(typeof(Math), nameof(Math.Max), [typeof(int), typeof(int)]);
        var length = AccessTools.PropertyGetter(typeof(string), nameof(string.Length));
        List<CodeInstruction> sum =
        [
            new(OpCodes.Ldloc_0), new(OpCodes.Ldc_I4_1), new(OpCodes.Ldc_I4_2), new(OpCodes.Add),
            new(OpCodes.Call, max), new(OpCodes.Pop)
        ];
        List<CodeInstruction> labelled =
            [new(OpCodes.Ldloc_0), new(OpCodes.Ldc_I4_1), new(OpCodes.Call, max) { labels = [new Label()] }];
        Assert.Multiple(() =>
        {
            Assert.That(Il.Consumer(sum, 0), Is.EqualTo((4, 0)), "under what add left, the first of Max");
            Assert.That(Il.Consumer(sum, 2), Is.EqualTo((3, 1)), "the second of add");
            Assert.That(Il.Consumer(sum, 4), Is.EqualTo((5, 0)));
            Assert.That(Il.Consumer(sum, 0, 3), Is.EqualTo((-1, -1)), "past its reach");
            Assert.That(Il.Consumer(labelled, 0), Is.EqualTo((-1, -1)), "a jump could land with another stack");
            Assert.That(
                Il.Consumer([new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Br, new Label())], 0),
                Is.EqualTo((-1, -1)));
            Assert.That(Il.Consumer([new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Dup)], 0),
                Is.EqualTo((1, 0)), "a copy is a use");
            Assert.That(
                Il.Consumer([new CodeInstruction(OpCodes.Ldloc_0), new CodeInstruction(OpCodes.Callvirt, length)], 0),
                Is.EqualTo((1, 0)), "`this`");
            Assert.That(Il.Effect(new CodeInstruction(OpCodes.Newobj, CloudTileScratch.Constructor())),
                Is.EqualTo((3, 1)));
            Assert.That(Il.Effect(new CodeInstruction(OpCodes.Ret)), Is.EqualTo((-1, -1)), "depends on the method");
        });
    }

    private static MethodInfo? ColumnBody()
    {
        _ = typeof(GenTerra).Assembly; // VSEssentials is found by name, so it has to be loaded first
        return ColumnNoiseScratch.ColumnBody();
    }

    private static bool Member(CodeInstruction c, int operand) =>
        operand == 0 && c.opcode == OpCodes.Call && c.operand is MethodInfo { IsStatic: false } m &&
               m.DeclaringType == typeof(ColumnNoise) && m.ReturnType == typeof(double);

    // Each call of GetOriginalInstructions declares its own LocalBuilders: a local by its index
    private static (OpCode, object?) Shape(CodeInstruction code) =>
        (code.opcode, code.operand is LocalBuilder local ? local.LocalIndex : code.operand);

    private static Il.Uses Kind(CodeInstruction code)
    {
        if (code.IsStloc()) return Il.Uses.Store;

        if (!code.IsLdloc()) return Il.Uses.None;

        return code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S ? Il.Uses.Address : Il.Uses.Load;
    }

    private sealed record Site(
        MethodBase Target,
        System.Func<CodeInstruction, bool> Match,
        MethodInfo Mine,
        bool OnlyThere,
        System.Func<IEnumerable<CodeInstruction>, List<CodeInstruction>> Transpile);
}
