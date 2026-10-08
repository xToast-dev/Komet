using System.Runtime.CompilerServices;
using Vintagestory.Client.NoObf;

namespace Komet.Tessellation;

// What every replacement of a cube, topsoil or cross slot does around its own drawing: the switch and the stand-down, the count, and
// the block handed to the engine's tesselator it replaced wherever Draw declines it (Draw writes nothing before it can decline)
internal abstract class OwnSlot(IBlockTesselator engine) : IBlockTesselator
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Tesselate(TCTCache vars)
    {
        _ = Assert(engine is not OwnSlot); // Swap replaces only the engine's own types
        if (OwnTessellation.Active && NotNull(vars) && vars.block is { } block && Draw(vars, block))
        {
            OwnTessellation.Count(true);
            return;
        }

        OwnTessellation.Count(false);
        engine.Tesselate(vars);
    }

    // The block drawn, or false with nothing written
    protected abstract bool Draw(TCTCache vars, Block block);
}
