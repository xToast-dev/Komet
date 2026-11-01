using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Vintagestory.GameContent;

namespace Komet.Test.Synth;

// Komet.World's garbage and lookup features against the engine methods they patch, each with the workload the game gives it
[Category(Synth.Category)]
[NonParallelizable]
public sealed class WorldMapSynth
{
    // An item stack's or block entity's attributes as they arrive in a chunk or inventory packet: a dozen keys, one nested tree
    [Test]
    public void TreeKeys()
    {
        var tree = new TreeAttribute();
        tree.SetString("type", "game:barrel");
        tree.SetInt("posx", 512000);
        tree.SetInt("posy", 110);
        tree.SetInt("posz", 511980);
        tree.SetDouble("lastTickTotalHours", 1234.5);
        tree.SetBool("sealed", true);
        tree.SetFloat("transitionstate", 0.25f);
        tree.SetString("recipecode", "game:pickledvegetable");
        var inventory = tree.GetOrAddTreeAttribute("inventory");
        inventory.SetInt("qslots", 2);
        var slots = inventory.GetOrAddTreeAttribute("slots");
        slots.SetString("0", "game:vegetable-carrot");
        slots.SetString("1", "game:waterportion");
        var bytes = tree.ToBytes();
        TreeAttribute? last = null;
        using (new FeatureRig("TreeKeys")) Assert.That(global::Komet.World.TreeKeys.Rewritten, Is.True);
        _ = Synth.Run("TreeKeys", "fromBytes", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                last = new TreeAttribute();
                last.FromBytes(bytes);
            }
        }, 2000, () => last?.ToJsonToken());
    }

    // EntityPlayer.PlayerUID: GetString on the player's watched attributes, dozens of times a frame
    [Test]
    public void TreeKeysReads()
    {
        var trees = Enumerable.Range(0, 8).Select(i =>
        {
            var tree = new TreeAttribute();
            tree.SetString("playerUID", Guid.NewGuid().ToString("N")[..22] + i);
            tree.SetString("entityName", "player" + i);
            return tree;
        }).ToArray();
        var sum = 0;
        using (new FeatureRig("TreeKeys")) Assert.That(global::Komet.World.TreeKeys.Reads, Is.True);
        _ = Synth.Run("TreeKeys", "getString", ops =>
        {
            for (var i = 0; i < ops; i++) sum += trees[i & 7].GetString("playerUID").Length;
        }, 20000, () => trees[3].GetString("playerUID"), () => sum = 0);
    }

    // A join decodes every sound: the survival mod's block sounds of 8 to 64 KB, one decoded per op
    [Test]
    public void SoundScratch()
    {
        GameInstall.RequireAssets();
        var files = Directory.GetFiles(Path.Combine(GameInstall.Assets, "survival", "sounds", "block"), "*.ogg",
                SearchOption.AllDirectories).Where(f => new FileInfo(f).Length is > 8 << 10 and < 64 << 10).Order().Take(24)
            .Select(File.ReadAllBytes).ToArray();
        Assert.That(files, Is.Not.Empty);
        var at = 0;
        var length = 0L;
        using (new FeatureRig("SoundScratch")) Assert.That(global::Komet.World.SoundScratch.Rewritten, Is.True);
        _ = Synth.Run("SoundScratch", "blockSounds", ops =>
        {
            for (var i = 0; i < ops; i++)
            {
                var ogg = files[at++ % files.Length];
                length += new OggDecoder()
                    .OggToWav(new MemoryStream(ogg), new Asset(ogg, new AssetLocation("game", "sounds/test.ogg"), null)).Pcm.Length;
            }
        }, 8, () => length, () => (at, length) = (0, 0));
    }

    // A grass block's growth check: WalkBlocks over the 7x2x7 around it, each block's attributes asked for killPlantRadius
    [Test]
    public void TickScratchSoil()
    {
        var soil = new BlockSoil { BlockId = 1, Code = new AssetLocation("game", "soil-medium-normal") };
        AccessTools.Field(typeof(BlockSoil), "growthChanceOnTick").SetValue(soil, 2f);
        var kinds = Enumerable.Range(0, 6).Select(k => new Block
        {
            BlockId = 10 + k, Code = new AssetLocation("game", "kind" + k),
            Attributes = new JsonObject(JObject.Parse(k == 5 ? """{ "killPlantRadius": 1 }""" : """{ "handbook": { "groupBy": ["a"] } }"""))
        }).ToArray();
        var air = new Block { BlockId = 0, Code = new AssetLocation("game", "air"), SideSolid = new SmallBoolArray(0) };
        var accessor = Answers.Of<IBlockAccessor>(new()
        {
            ["GetBlock"] = _ => air,
            ["WalkBlocks"] = args =>
            {
                var (min, max) = ((BlockPos)args![0]!, (BlockPos)args[1]!);
                var visit = (Action<Block, int, int, int>)args[2]!;
                var (dx, dy) = (max.X - min.X + 1, max.Y - min.Y + 1);
                var cells = dx * dy * (max.Z - min.Z + 1);
                for (var c = 0; c < cells; c++)
                {
                    var (x, y, z) = (min.X + c % dx, min.Y + c / dx % dy, min.Z + c / dx / dy);
                    visit(kinds[c == cells - 1 ? kinds.Length - 1 : (c * 7) % (kinds.Length - 1)], x, y, z);
                }

                return null;
            }
        });
        var world = Answers.Of<IWorldAccessor>(new() { ["get_BlockAccessor"] = _ => accessor });
        var random = new Random(1);
        var pos = new BlockPos(512000, 110, 512000);
        var ticked = 0;
        using (new FeatureRig("TickScratch")) Assert.That(global::Komet.World.TickScratch.Rewritten, Is.EqualTo(2));
        _ = Synth.Run("TickScratch", "soilWalk", ops =>
        {
            for (var i = 0; i < ops; i++)
                if (soil.ShouldReceiveServerGameTicks(world, pos, random, out _))
                    ticked++;
        }, 500, () => ticked, () => ticked = 0);
    }

    // A barrel with one item no recipe takes, matched against every recipe every 3 s: most answer an empty pairing
    [Test]
    public void TickScratchBarrel()
    {
        var recipes = Enumerable.Range(0, 40).Select(r => new BarrelRecipe
        {
            Code = "r" + r,
            Ingredients = [.. Enumerable.Range(0, 1 + r % 2).Select(_ => new BarrelRecipeIngredient())]
        }).ToArray();
        var stack = new ItemStack(new Block { BlockId = 3, Code = new AssetLocation("game", "rot") }, 4);
        ItemSlot[] slots = [new DummySlot(stack), new DummySlot()];
        var matched = 0;
        _ = Synth.Run("TickScratch", "barrelPairs", ops =>
        {
            for (var i = 0; i < ops; i++)
                if (recipes[i % recipes.Length].Matches(slots, out _))
                    matched++;
        }, 4000, () => matched, () => matched = 0);
    }

    // The client's weather readers ask for their regions many times a frame; all of them known
    [Test]
    public void WeatherLock()
    {
        var weather = (WeatherSystemClient)RuntimeHelpers.GetUninitializedObject(typeof(WeatherSystemClient));
        AccessTools.Field(typeof(WeatherSystemBase), "weatherSimByMapRegionLock").SetValue(weather, new object());
        weather.weatherSimByMapRegion = [];
        var keys = new long[64];
        for (var i = 0; i < keys.Length; i++)
        {
            keys[i] = weather.MapRegionIndex2D(1000 + i % 8, 1000 + i / 8);
            weather.weatherSimByMapRegion[keys[i]] =
                (WeatherSimulationRegion)RuntimeHelpers.GetUninitializedObject(typeof(WeatherSimulationRegion));
        }

        var found = 0;
        using (new FeatureRig("WeatherLock")) Assert.That(global::Komet.World.WeatherLock.Patched, Is.True);
        _ = Synth.Run("WeatherLock", "knownRegions", ops =>
        {
            for (var i = 0; i < ops; i++)
                if (weather.getOrCreateWeatherSimForRegion(keys[(i * 7) & 63], null) is not null)
                    found++;
        }, 20000, () => found, () => found = 0);
    }

    // The map thread saves its finished pieces: eight 32x32 pieces of opaque colours per save
    [Test]
    public void MapSaveScratch()
    {
        var folder = Directory.CreateTempSubdirectory("komet-synth-mapdb").FullName;
        var db = new MapDB(new QuietLogger());
        var error = "";
        Assert.That(db.OpenOrCreate(Path.Combine(folder, "map.db"), ref error, true, false, false), Is.True, error);
        try
        {
            var random = new Random(3);
            var pieces = Enumerable.Range(0, 8).ToDictionary(i => new FastVec2i(100 + i, 200 - i),
                _ => new MapPieceDB { Pixels = [.. Enumerable.Range(0, 1024).Select(_ => unchecked((int)0xFF000000) | random.Next(1 << 24))] });
            using (new FeatureRig("MapSaveScratch")) Assert.That(global::Komet.World.MapSaveScratch.Matched, Is.True);
            _ = Synth.Run("MapSaveScratch", "save8", ops =>
            {
                for (var i = 0; i < ops; i++) db.SetMapPieces(pieces);
            }, 20, () => db.GetMapPiece(new FastVec2i(103, 197)).Pixels.Sum(p => (long)p));
        }
        finally
        {
            db.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }
}
