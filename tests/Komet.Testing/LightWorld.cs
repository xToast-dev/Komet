using Vintagestory.API.Server;

namespace Komet.Testing;

// A 128 x 96 x 128 world on the engine's own chunk storage - ChunkData from a ChunkDataPool, held by a WorldChunk - behind an
// IChunkProvider that is an array, and a ChunkIlluminator over it exactly as the server builds one. The terrain is fixed by the seed:
// stone hills with caves, a glass wall and leaves (light passes, weakened), water, and light sources on chunk borders, at the map's
// edges, under water, in caves and in clusters whose ranges overlap. A script of block light changes then runs on it: the light
// sources placed, removed and re-placed, blocks put into and taken out of their light. After every step the touched chunks the engine
// returns and a hash of every light value of those chunks are recorded. There is no sunlight: the block light code neither reads nor
// writes it, and flooding the columns would take most of the test's time.
public sealed class LightWorld : IChunkProvider
{
    public const int Size = 32, ChunksX = 4, ChunksY = 3, ChunksZ = 4;
    public const int SizeX = Size * ChunksX, SizeY = Size * ChunksY, SizeZ = Size * ChunksZ;
    private const ushort Sunlight = 24;

    // Block ids; the light sources are the engine's hsv triples: value 18 is the one RemoveBlockLight widens to 20
    public const int Air = 0, Stone = 1, Glass = 2, Leaves = 3, Water = 4, Torch = 5, Lantern = 6, Crystal = 7,
        Lava = 8, Beacon = 9, Nesting = 10, NestingLight = 11;

    private readonly LightChunk[] _chunks = new LightChunk[ChunksX * ChunksY * ChunksZ];
    private readonly List<(int X, int Y, int Z, int Id)> _lights = [];
    private readonly List<(int X, int Y, int Z, int Id)> _placed = [];

    public LightWorld(IList<Block> blocks, int seed)
    {
        Blocks = blocks;
        var pool = new ChunkDataPool(Size, null!); // no server: the client's palette rules, which never compact
        for (var i = 0; i < _chunks.Length; i++) _chunks[i] = new LightChunk(pool);
        // readBlockAccess only reaches Block.GetLightHsv, which ignores it
        Illuminator = new ChunkIlluminator(this, null!, Size);
        Illuminator.InitForWorld(blocks, Sunlight, SizeX, SizeY, SizeZ);
        Terrain(new Random(seed));
    }

    public IList<Block> Blocks { get; }
    public ChunkIlluminator Illuminator { get; }
    public List<long[]> Touched { get; } = [];
    public List<ulong> Hashes { get; } = [];
    public List<string> Errors { get; } = [];
    public bool Recording { get; set; } = true; // off for the measurements: the hash reads light values
    public bool Catching { get; set; }
    public ILogger Logger { get; } = new QuietLogger();

    // Chunks the provider answers as not loaded, as a server with a column loaded only in part; the world keeps them
    public HashSet<(int X, int Y, int Z)> Unloaded { get; } = [];

    public IWorldChunk GetChunk(int chunkX, int chunkY, int chunkZ)
    {
        return Unloaded.Contains((chunkX, chunkY, chunkZ)) ? null! : Chunk(chunkX, chunkY, chunkZ)!;
    }

    public IWorldChunk GetUnpackedChunkFast(int chunkX, int chunkY, int chunkZ, bool notRecentlyAccessed = false)
    {
        // as the world holds it: every chunk keeps its ChunkData, but one a test packs, which the code under test must unpack first
        return GetChunk(chunkX, chunkY, chunkZ);
    }

    public long ChunkIndex3D(int chunkX, int chunkY, int chunkZ)
    {
        return ((long)chunkY * ChunksZ + chunkZ) * ChunksX + chunkX;
    }

    public long ChunkIndex3D(EntityPos pos)
    {
        throw new NotSupportedException();
    }

    private LightChunk? Chunk(int x, int y, int z)
    {
        return x is >= 0 and < ChunksX && y is >= 0 and < ChunksY && z is >= 0 and < ChunksZ
            ? _chunks[ChunkIndex3D(x, y, z)]
            : null;
    }

    private static int Local(int x, int y, int z)
    {
        return (y % Size * Size + z % Size) * Size + x % Size;
    }

    public int BlockAt(int x, int y, int z)
    {
        var cells = Chunk(x / Size, y / Size, z / Size)!.Cells;
        return cells[Local(x, y, z)];
    }

    public void Set(int x, int y, int z, int id)
    {
        var cells = Chunk(x / Size, y / Size, z / Size)!.Cells;
        if (id == Water || id == Lava)
        {
            cells.SetFluid(Local(x, y, z), id);
        }
        else
        {
            cells[Local(x, y, z)] = id;
            if (cells.GetFluid(Local(x, y, z)) != 0) cells.SetFluid(Local(x, y, z), 0);
        }
    }

    // Every light value of every chunk, in chunk order
    public int[][] Light()
    {
        var all = new int[_chunks.Length][];
        for (var c = 0; c < _chunks.Length; c++)
        {
            var layer = _chunks[c].Cells.lightLayer;
            all[c] = new int[Size * Size * Size];
            if (layer == null) continue;
            for (var i = 0; i < all[c].Length; i++) all[c][i] = layer.Get(i);
        }

        return all;
    }

    // Every light value of these chunks, in the order given
    public ulong Hash(IEnumerable<long> chunks)
    {
        var hash = 14695981039346656037UL;
        foreach (var index in chunks)
        {
            var layer = _chunks[index].Cells.lightLayer;
            hash = (hash ^ (ulong)index) * 1099511628211UL;
            if (layer == null) continue;
            for (var i = 0; i < Size * Size * Size; i++) hash = (hash ^ (uint)layer.Get(i)) * 1099511628211UL;
        }

        return hash;
    }

    public ulong Hash()
    {
        return Hash(Enumerable.Range(0, _chunks.Length).Select(i => (long)i));
    }

    private void Record(FastSetOfLongs touched)
    {
        if (!Recording) return;
        long[] chunks = [.. touched];
        Touched.Add(chunks);
        Hashes.Add(Hash(chunks));
    }

    private byte[] Hsv(int id)
    {
        return Blocks[id].GetLightHsv(null!, null!);
        // the engine's own conversion, a fresh byte[3]
    }

    // Every light source placed in a fixed order
    public void LightUp()
    {
        foreach (var (x, y, z, id) in _lights) Do(() => Illuminator.PlaceBlockLight(Hsv(id), x, y, z));
        _placed.AddRange(_lights);
    }

    // The same steps in the same order on every world built from the same seed: every choice reads only block ids and the
    // script's own lists, which lighting never changes. One step per MoveNext, so that two worlds can take turns on one thread.
    public IEnumerable<int> Script(int seed, int steps)
    {
        var random = new Random(seed);
        List<(int X, int Y, int Z, int Id)> blocked = [];
        int[] sources = [Torch, Lantern, Crystal, Beacon, Lava, NestingLight];
        int[] solids = [Stone, Stone, Glass, Leaves, Nesting];
        for (var step = 0; step < steps; step++)
        {
            var kind = random.Next(8);
            if ((kind is 2 or 3 or 4 or 6 && _placed.Count == 0) || (kind == 5 && blocked.Count == 0)) kind = 0;
            if (kind is 0 or 1 or 7)
            {
                var (x, y, z) = Open(random);
                var id = kind == 7 ? Torch : sources[random.Next(sources.Length)];
                Set(x, y, z, id);
                Do(() => Illuminator.PlaceBlockLight(Hsv(id), x, y, z));
                _placed.Add((x, y, z, id));
            }
            else if (kind == 2)
            {
                var at = random.Next(_placed.Count);
                var (x, y, z, id) = _placed[at];
                _placed.RemoveAt(at);
                Set(x, y, z, Air);
                Do(() => Illuminator.RemoveBlockLight(Hsv(id), x, y, z));
            }
            else if (kind is 3 or 4 or 6)
            {
                // a block put into a light's reach, the way a player or a structure would; next to it when that spot is taken
                var (lx, ly, lz, _) = _placed[random.Next(_placed.Count)];
                var (x, y, z) = (Clamp(lx + random.Next(-4, 5), SizeX), Clamp(ly + random.Next(-3, 4), SizeY),
                    Clamp(lz + random.Next(-4, 5), SizeZ));
                var id = solids[random.Next(solids.Length)];
                if (BlockAt(x, y, z) == Air && Fluid(x, y, z) == 0)
                {
                    Set(x, y, z, id);
                    Do(() => Illuminator.UpdateBlockLight(Blocks[Air].LightAbsorption, Blocks[id].LightAbsorption, x, y,
                        z));
                    blocked.Add((x, y, z, id));
                }
            }
            else
            {
                var at = random.Next(blocked.Count);
                var (x, y, z, id) = blocked[at];
                blocked.RemoveAt(at);
                Set(x, y, z, Air);
                Do(() => Illuminator.UpdateBlockLight(Blocks[id].LightAbsorption, Blocks[Air].LightAbsorption, x, y,
                    z));
            }

            yield return step;
        }
    }

    // One light update; with Catching set an exception is recorded as a step of its own, with a hash of the whole world, and the
    // script goes on: a light update nested in another one on the same illuminator can make the engine's own enumerations throw
    private void Do(Func<FastSetOfLongs> update)
    {
        if (!Catching)
        {
            Record(update());
            return;
        }

        try
        {
            Record(update());
        }
        catch (Exception e)
        {
            Errors.Add(e.GetType().Name);
            Touched.Add([long.MinValue]);
            if (Recording) Hashes.Add(Hash());
        }
    }

    public int Fluid(int x, int y, int z)
    {
        return Chunk(x / Size, y / Size, z / Size)!.Cells.GetFluid(Local(x, y, z));
    }

    private static int Clamp(int value, int size)
    {
        return Math.Clamp(value, 0, size - 1);
    }

    // An air block, preferring ones near the chunk borders and the map's edges
    private (int X, int Y, int Z) Open(Random random)
    {
        for (var attempt = 0; attempt < 1000; attempt++)
        {
            int Near(int size)
            {
                return random.Next(3) switch
                {
                    0 => random.Next(size),
                    1 => Clamp(random.Next(size / Size) * Size + random.Next(-2, 2), size),
                    _ => random.Next(2) == 0 ? random.Next(3) : size - 1 - random.Next(3)
                };
            }

            var (x, y, z) = (Near(SizeX), 1 + random.Next(SizeY - 2), Near(SizeZ));
            if (BlockAt(x, y, z) == Air && Fluid(x, y, z) == 0) return (x, y, z);
        }

        throw new InvalidOperationException("no air left");
    }

    private void Terrain(Random random)
    {
        var (p, q) = (random.NextDouble() * 6, random.NextDouble() * 6);
        for (var x = 0; x < SizeX; x++)
        {
            for (var z = 0; z < SizeZ; z++)
            {
                var height = 30 + (int)(6 * Math.Sin(x / 11.0 + p) + 5 * Math.Cos(z / 9.0 + q));
                for (var y = 0; y < height; y++) Set(x, y, z, Stone);
            }
        }

        for (var cave = 0; cave < 14; cave++) // caves, some cut open to the sky, some crossing chunk borders
        {
            var (cx, cy, cz, r) = (random.Next(SizeX), 4 + random.Next(30), random.Next(SizeZ), 3 + random.Next(5));
            for (var x = Math.Max(0, cx - r); x <= Math.Min(SizeX - 1, cx + r); x++)
            {
                for (var y = Math.Max(1, cy - r); y <= Math.Min(SizeY - 1, cy + r); y++)
                {
                    for (var z = Math.Max(0, cz - r); z <= Math.Min(SizeZ - 1, cz + r); z++)
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz) <= r * r)
                            Set(x, y, z, Air);
                }
            }

            _lights.Add((cx, cy, cz, cave % 3 == 0 ? Beacon : Torch));
        }

        for (var y = 20; y < 60; y++) // a glass wall across the border between chunk columns 1 and 2
        {
            for (var z = 40; z < 90; z++)
                if (BlockAt(64, y, z) == Air)
                    Set(64, y, z, Glass);
        }
        for (var tree = 0; tree < 10; tree++) // leaf crowns above the hills
        {
            var (tx, tz) = (4 + random.Next(SizeX - 8), 4 + random.Next(SizeZ - 8));
            var top = 38 + random.Next(8);
            for (var x = tx - 2; x <= tx + 2; x++)
            {
                for (var y = top; y <= top + 3; y++)
                {
                    for (var z = tz - 2; z <= tz + 2; z++)
                        if (BlockAt(x, y, z) == Air)
                            Set(x, y, z, Leaves);
                }
            }
        }

        for (var x = 90; x < 110; x++) // a pond with a light under its water
        {
            for (var z = 10; z < 28; z++)
            {
                for (var y = 30; y < 38; y++)
                    Set(x, y, z, Water);
            }
        }

        _lights.Add((100, 31, 18, Crystal));
        for (var x = 0; x < 6; x++) // lava at the map's corner
        {
            for (var z = SizeZ - 6; z < SizeZ; z++)
                Set(x, 36, z, Lava);
        }
        _lights.AddRange([(0, 36, SizeZ - 1, Lava), (5, 36, SizeZ - 6, Lava), (2, 36, SizeZ - 3, Lava)]);
        // clusters on chunk borders and at the map's edges, overlapping one another
        _lights.AddRange([
            (31, 45, 31, Lantern), (32, 45, 32, Torch), (33, 46, 30, Beacon), (95, 50, 64, Lantern),
            (96, 50, 63, Crystal),
            (0, 48, 0, Torch), (SizeX - 1, 48, SizeZ - 1, Beacon), (64, 64, 64, Lantern), (63, 63, 63, Torch),
            (1, 70, 64, Beacon)
        ]);
        for (var x = 28; x < 36; x++) // blocks whose absorption a test can hook, around the cluster on the chunk corner
        {
            for (var z = 26; z < 29; z++)
                Set(x, 44, z, Nesting);
        }
        _lights.Add((34, 47, 34, NestingLight));
        foreach (var (x, y, z, id) in _lights)
            if (id != Lava)
                Set(x, y, z, id);
        foreach (var (x, y, z, _) in _lights)
            _ = Chunk(x / Size, y / Size, z / Size)!.LightPositions.Add(Local(x, y, z));
        // one lava block lit only as a neighbour's source position
        _ = _lights.RemoveAll(light => light.Id == Lava && light.X == 2);
    }

    // The engine's blocks for this world: absorption as the game's, light as hsv triples
    public static Block[] MakeBlocks(Block? nesting = null, Block? nestingLight = null)
    {
        static Block Make(Block block, int id, string code, int absorption, byte h = 0, byte s = 0, byte v = 0)
        {
            block.BlockId = id;
            block.Code = new AssetLocation("komet", code);
            block.LightAbsorption = absorption;
            block.LightHsv = new[] { h, s, v };
            return block;
        }

        return
        [
            Make(new Block(), Air, "air", 0), Make(new Block(), Stone, "stone", 99),
            Make(new Block(), Glass, "glass", 1),
            Make(new Block(), Leaves, "leaves", 2), Make(new Block(), Water, "water", 2),
            Make(new Block(), Torch, "torch", 0, 4, 2, 14),
            Make(new Block(), Lantern, "lantern", 0, 8, 3, 18), Make(new Block(), Crystal, "crystal", 1, 40, 7, 9),
            Make(new Block(), Lava, "lava", 0, 2, 7, 16), Make(new Block(), Beacon, "beacon", 0, 20, 5, 24),
            Make(nesting ?? new Block(), Nesting, "nesting", 1),
            Make(nestingLight ?? new Block(), NestingLight, "nestinglight", 0, 30, 4, 15)
        ];
    }

    private sealed class LightChunk : WorldChunk
    {
        public LightChunk(ChunkDataPool pool)
        {
            datapool = pool;
            chunkdata = ChunkData.CreateNew(Size, pool);
            MaybeBlocks = chunkdata;
        }

        public ChunkData Cells => chunkdata;
        public override IMapChunk MapChunk => null!;
        public override HashSet<int> LightPositions { get; set; } = [];
        public override Dictionary<string, byte[]> ModData { get; set; } = [];
    }
}
