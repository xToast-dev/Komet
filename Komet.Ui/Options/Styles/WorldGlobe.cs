using Vintagestory.API.MathTools;

namespace Komet.Options;

// The world around the player for a background shader: its own colours of water, sand, rock, snow and green, averaged
// over the top blocks of the loaded columns (sampled a slice per frame while the screen is open, again once the player
// moved far or a minute passed); where and when the player is (latitude, hour, season) and the weather there.
internal sealed class WorldGlobe(ICoreClientAPI capi) : IDisposable
{
    public const int Size = 128, Kinds = 5;
    private const int PerFrame = 1024, MinStep = 4, MaxStep = 24, Count = Size * Size;
    private const double Refresh = 60, Moved = 256;

    private readonly double[] _sums = new double[Kinds * 4]; // r, g, b, samples per kind
    private readonly BlockPos _pos = new(0);
    private int _next = -1; // the next sample; negative: not sampling
    private (int X, int Z, int Step) _origin;
    private double _madeAt = double.NegativeInfinity;

    // Average colours (r, g, b) of water, sand, rock, snow, green; the shader's own where the world gave none
    public float[] Tints { get; } = new float[Kinds * 3];

    // Where and when the player is, for a globe at the world's true scale: latitude (radians, the game's climate -1..1
    // south to north), the sun's hour angle from noon (radians), its declination by the season (radians), 1 in a world
    public (float Latitude, float Hour, float Declination, float Known) Place { get; private set; }

    // The weather where the player stands: cloud cover and precipitation 0..1, the temperature in °C / 40, 1 in a world
    public (float Clouds, float Rain, float Temperature, float Known) Weather { get; private set; }
    private double _weatherAt = double.NegativeInfinity;

    // The game's climate now at the player, at most once a second (it is not free)
    public void Sky(double clock)
    {
        if (clock - _weatherAt < 1 || capi.World?.Player?.Entity?.Pos is not { } at || !Finite(clock)) return;
        _weatherAt = clock;
        var now = capi.World.BlockAccessor?.GetClimateAt(_pos.Set((int)at.X, (int)at.Y, (int)at.Z), EnumGetClimateMode.NowValues);
        Weather = now is null ? default
            : (Math.Clamp(Math.Max(now.RainCloudOverlay, now.Rainfall), 0, 1), Math.Clamp(now.Rainfall, 0, 1), now.Temperature / 40, 1);
    }

    // Asked each frame: cheap, the calendar's numbers
    public void Locate()
    {
        var (calendar, at) = (capi.World?.Calendar, capi.World?.Player?.Entity?.Pos);
        if (calendar is null || at is null || !Finite(at.Z))
        {
            Place = default;
            return;
        }

        var latitude = calendar.OnGetLatitude?.Invoke(at.Z) ?? 0.5;
        var day = calendar.HoursPerDay > 0 ? calendar.HourOfDay / calendar.HoursPerDay : 0.5;
        var declination = -0.41 * Math.Cos(2 * Math.PI * calendar.YearRel); // northern winter at the year's turn
        Place = ((float)(Math.Clamp(latitude, -1, 1) * 1.3), (float)((day - 0.5) * 2 * Math.PI), (float)declination, 1);
    }
    public bool Known { get; private set; }

    // A new picture when none is there, the player moved far or it is old; at clock in seconds
    public void Begin(double clock)
    {
        if (_next >= 0 || capi.World?.Player?.Entity?.Pos is not { } at || !Finite(clock)) return;
        var center = (X: (int)at.X, Z: (int)at.Z);
        var old = _origin.Step > 0 ? (X: _origin.X + Size / 2 * _origin.Step, Z: _origin.Z + Size / 2 * _origin.Step) : center;
        var far = Math.Abs(old.X - center.X) + Math.Abs(old.Z - center.Z) > Moved;
        if (Known && !far && clock - _madeAt < Refresh) return;
        var reach = Math.Clamp(Vintagestory.Client.NoObf.ClientSettings.ViewDistance, 128, 1536);
        var step = Math.Clamp(2 * reach / Size, MinStep, MaxStep);
        _origin = (center.X - Size / 2 * step, center.Z - Size / 2 * step, step);
        (_next, _madeAt) = (0, clock);
        Array.Clear(_sums);
    }

    // A slice of samples; the last one shades, uploads and averages
    public void Step()
    {
        if (_next < 0 || capi.World?.BlockAccessor is not { } blocks) return;
        var end = Math.Min(Count, _next + PerFrame);
        (int X, int Z, IMapChunk? Chunk) last = (-1, -1, null); // samples 4..24 blocks apart: up to 8 in a row share a chunk
        for (var i = _next; i < Math.Min(end, Count); i++) Sample(blocks, i, ref last);
        _next = end;
        if (_next < Count) return;
        _next = -1;
        Average();
        Known = true;
    }

    private void Sample(IBlockAccessor blocks, int i, ref (int X, int Z, IMapChunk? Chunk) last)
    {
        var (x, z) = (_origin.X + i % Size * _origin.Step, _origin.Z + i / Size * _origin.Step);
        if (x < 0 || z < 0) return;
        var (chunkX, chunkZ) = (x / GlobalConstants.ChunkSize, z / GlobalConstants.ChunkSize);
        if (last.X != chunkX || last.Z != chunkZ) last = (chunkX, chunkZ, blocks.GetMapChunk(chunkX, chunkZ));
        if (last.Chunk?.RainHeightMap is not { Length: > 0 } heights) return;
        var height = heights[z % GlobalConstants.ChunkSize * GlobalConstants.ChunkSize + x % GlobalConstants.ChunkSize];
        var block = blocks.GetBlock(_pos.Set(x, height, z));
        if (block is null || block.Id == 0) return;
        var color = block.GetColor(capi, _pos) | unchecked((int)0xFF000000);
        var kind = KindOf(block);
        if (kind < 0) return;
        _sums[kind * 4] += color & 0xFF;
        _sums[kind * 4 + 1] += (color >> 8) & 0xFF;
        _sums[kind * 4 + 2] += (color >> 16) & 0xFF;
        _sums[kind * 4 + 3]++;
    }

    // 0 water, 1 sand, 2 rock, 3 snow, 4 green; -1 none of them
    private static int KindOf(Block block)
    {
        if (block.IsLiquid()) return 0;
        return block.BlockMaterial switch
        {
            EnumBlockMaterial.Sand or EnumBlockMaterial.Gravel => 1,
            EnumBlockMaterial.Stone or EnumBlockMaterial.Ore or EnumBlockMaterial.Mantle => 2,
            EnumBlockMaterial.Snow or EnumBlockMaterial.Ice => 3,
            EnumBlockMaterial.Soil or EnumBlockMaterial.Plant or EnumBlockMaterial.Leaves or EnumBlockMaterial.Wood => 4,
            _ => -1
        };
    }

    private void Average()
    {
        for (var k = 0; k < Kinds; k++)
        {
            var n = _sums[k * 4 + 3];
            for (var c = 0; c < 3; c++) Tints[k * 3 + c] = n > 0 ? (float)(_sums[k * 4 + c] / n / 255) : -1; // -1: the shader's own
        }
    }

    public void Dispose()
    {
        Known = false;
    }
}
