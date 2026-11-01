using Komet.Core;

namespace Komet.Test.Core;

// The module layering the mod is built from: the mod's assembly is the only one with mod systems (the game refuses a mod
// with two), every module references only modules below it, and the module list orders every feature
public sealed class ModulesTests
{
    // Bottom to top; a module may reference only what stands before it here
    private static readonly string[] Layers =
    [
        "Komet.Core", "Komet.Diagnostics", "Komet.Rendering", "Komet.Tessellation", "Komet.Chunks", "Komet.Shapes", "Komet.World",
        "Komet.Vulkan", "Komet.Ui", "Komet"
    ];

    private static Assembly Of(string name) => Assembly.Load(name);

    [Test]
    public void OnlyTheModAssemblyHasModSystems()
    {
        foreach (var name in Layers.Bounded(Layers.Length))
        {
            var systems = Of(name).GetTypes().Where(t => typeof(ModSystem).IsAssignableFrom(t) && !t.IsAbstract).Select(t => t.Name).ToArray();
            if (name == "Komet") Assert.That(systems, Is.EquivalentTo((string[])[nameof(KometModSystem), nameof(MapTracking)]));
            else Assert.That(systems, Is.Empty, name + " must hold no ModSystem: move it to the Komet project");
        }
    }

    [Test]
    public void EveryModuleReferencesOnlyTheModulesBelowIt()
    {
        for (var i = 0; i < Layers.Length; i++)
        {
            var above = Layers[(i + 1)..];
            var referenced = Of(Layers[i]).GetReferencedAssemblies().Select(a => a.Name).Where(n => n is not null && n.StartsWith("Komet", StringComparison.Ordinal));
            Assert.That(referenced.Intersect(above), Is.Empty, Layers[i] + " references a module above it");
        }
    }

    [Test]
    public void TheModuleListOrdersEveryFeature()
    {
        Modules.Register();
        Assert.Multiple(() =>
        {
            Assert.That(Features.All.Length, Is.GreaterThan(90));
            Assert.That(Features.All.ToArray().Select(f => f.Id), Is.Unique);
            Assert.That(Knobs.BuiltInCount, Is.GreaterThan(85));
        });
    }
}
